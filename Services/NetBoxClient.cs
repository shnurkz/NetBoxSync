using NetBoxSync.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetBoxSync.Utilities;

namespace NetBoxSync.Services;

public class NetBoxClient
{
  private readonly HttpClient _client;
  private readonly string _baseUrl;
  private readonly Dictionary<string, string> _tagSlugMap = new(StringComparer.OrdinalIgnoreCase);
  private readonly Dictionary<string, int> _clusterCache = new();
  private int? _defaultClusterType;
  private int? _defaultSite;

  public NetBoxClient(string url, string token)
  {
    _baseUrl = url.TrimEnd('/');
    var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = delegate { return true; } };
    _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token.Trim());
  }

  public async Task SyncVirtualMachinesAsync(List<VmAsset> vms, int clusterId, Dictionary<string, int> hostMap)
  {
    SyncLogger.Info($"--- [NetBox] Синхронизация {vms.Count} машин ---");
    var cache = await LoadVmCacheAsync();
    await PreloadTagsAsync();

    int count = 0;
    foreach (var vm in vms)
    {
      try
      {
          vm.Name = DataNormalizer.NormalizeString(vm.Name);
          if (string.IsNullOrEmpty(vm.Name)) continue;

          string commentsMarkdown = MarkdownReportGenerator.Generate(vm);
          int resolvedClusterId = await ResolveClusterAsync(vm, clusterId);

          var tagsToAssign = new List<string>();
          if (!string.IsNullOrEmpty(vm.Tenant))
          {
              tagsToAssign.Add(DataNormalizer.NormalizeString(vm.Tenant));
          }

          foreach (var tagName in tagsToAssign)
          {
              await EnsureTagExistsAsync(tagName);
          }

          string endpoint = "/virtualization/virtual-machines/";
          var payload = new Dictionary<string, object>
          {
            { "name", vm.Name },
            { "cluster", resolvedClusterId },
            { "status", vm.IsRunning ? "active" : "offline" },
            { "vcpus", (float)vm.Vcpus },
            { "memory", (int)vm.MemoryMb },
            { "disk", (int)vm.DiskGb * 1024 },
            { "comments", commentsMarkdown },
            { "custom_fields", new Dictionary<string, object>
              {
                { "cpu_cost", (int)Math.Round(vm.HardwareCost) },
                { "total_cost", (int)Math.Round(vm.TotalCost) }
              }
            }
          };

          if (hostMap.TryGetValue(vm.NodeName, out int devId)) payload["device"] = devId;
          
          if (tagsToAssign.Count > 0)
          {
              payload["tags"] = tagsToAssign.Select(t => new { name = t }).ToList<object>();
          }

          HttpResponseMessage res;
          int currentVmId = 0;
          if (cache.TryGetValue(vm.Name, out int id))
          {
            currentVmId = id;
            endpoint += $"{id}/";
            res = await PatchWithRetryAsync(endpoint, payload);
          }
          else
          {
            res = await PostWithRetryAsync(endpoint, payload);
          }

          if (res.IsSuccessStatusCode) 
          {
              if (currentVmId == 0)
              {
                  var respJson = await res.Content.ReadFromJsonAsync<JsonElement>();
                  if (respJson.TryGetProperty("id", out var newIdProp)) currentVmId = newIdProp.GetInt32();
              }

              if (currentVmId > 0)
              {
                  await SyncNetworkingAsync(currentVmId, vm);
              }
              
              SyncLogger.Info($"[{++count}/{vms.Count}] {vm.Name} ... OK.");
          }
          else 
          {
              SyncLogger.Error($"[{++count}/{vms.Count}] {vm.Name} ... ОШИБКА: {res.StatusCode}");
          }
      }
      catch (Exception ex)
      {
          SyncLogger.Error($"[NetBoxClient] Failed to sync VM {vm.Name}: {ex.Message}");
      }
    }
  }

  private async Task SyncNetworkingAsync(int vmId, VmAsset vm)
  {
      try
      {
          int interfaceId = 0;
          var ifaceResp = await GetWithRetryAsync($"/virtualization/interfaces/?virtual_machine_id={vmId}");
          if (ifaceResp.IsSuccessStatusCode)
          {
              var ifaceJson = await ifaceResp.Content.ReadFromJsonAsync<JsonElement>();
              if (ifaceJson.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
              {
                  interfaceId = results[0].GetProperty("id").GetInt32();
              }
              else
              {
                  var createIface = await PostWithRetryAsync("/virtualization/interfaces/", new { virtual_machine = vmId, name = "eth0" });
                  if (createIface.IsSuccessStatusCode)
                  {
                      var newIfaceJson = await createIface.Content.ReadFromJsonAsync<JsonElement>();
                      interfaceId = newIfaceJson.GetProperty("id").GetInt32();
                  }
              }
          }

          string primaryIp = vm.IpAddresses.FirstOrDefault();
          if (!string.IsNullOrEmpty(primaryIp) && interfaceId > 0)
          {
              string ipCidr = primaryIp.Contains("/") ? primaryIp : $"{primaryIp}/32";
              
              int ipId = 0;
              var ipResp = await GetWithRetryAsync($"/ipam/ip-addresses/?address={ipCidr}");
              if (ipResp.IsSuccessStatusCode)
              {
                  var ipJson = await ipResp.Content.ReadFromJsonAsync<JsonElement>();
                  if (ipJson.TryGetProperty("results", out var ipResults) && ipResults.GetArrayLength() > 0)
                  {
                      ipId = ipResults[0].GetProperty("id").GetInt32();
                  }
                  else
                  {
                      var createIp = await PostWithRetryAsync("/ipam/ip-addresses/", new { 
                          address = ipCidr, 
                          status = "active",
                          assigned_object_type = "virtualization.vminterface",
                          assigned_object_id = interfaceId
                      });
                      if (createIp.IsSuccessStatusCode)
                      {
                          var newIpJson = await createIp.Content.ReadFromJsonAsync<JsonElement>();
                          ipId = newIpJson.GetProperty("id").GetInt32();
                      }
                  }
              }

              if (ipId > 0)
              {
                  await PatchWithRetryAsync($"/virtualization/virtual-machines/{vmId}/", new { primary_ip4 = ipId });
              }
          }
      }
      catch (Exception ex)
      {
          SyncLogger.Warning($"[NetBox] Failed to sync networking for VM {vm.Name}: {ex.Message}");
      }
  }

  private async Task<int> ResolveClusterAsync(VmAsset vm, int defaultClusterId)
  {
      if (!string.IsNullOrEmpty(vm.ClusterName)) return defaultClusterId;

      string standaloneName = DataNormalizer.NormalizeString($"Standalone-{vm.NodeName}");
      if (_clusterCache.TryGetValue(standaloneName, out int id)) return id;

      if (_defaultClusterType == null || _defaultSite == null)
      {
          var defResp = await GetWithRetryAsync($"/virtualization/clusters/{defaultClusterId}/");
          if (defResp.IsSuccessStatusCode)
          {
              var defJson = await defResp.Content.ReadFromJsonAsync<JsonElement>();
              if (defJson.TryGetProperty("type", out var typeProp) && typeProp.TryGetProperty("id", out var typeIdProp))
                  _defaultClusterType = typeIdProp.GetInt32();
              
              if (defJson.TryGetProperty("site", out var siteProp) && siteProp.TryGetProperty("id", out var siteIdProp))
                  _defaultSite = siteIdProp.GetInt32();
          }
      }

      var res = await GetWithRetryAsync($"/virtualization/clusters/?name={standaloneName}");
      if (res.IsSuccessStatusCode)
      {
          var json = await res.Content.ReadFromJsonAsync<JsonElement>();
          if (json.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
          {
              int existingId = results[0].GetProperty("id").GetInt32();
              _clusterCache[standaloneName] = existingId;
              return existingId;
          }
      }

      if (_defaultClusterType.HasValue && _defaultSite.HasValue)
      {
          var payload = new Dictionary<string, object>
          {
              { "name", standaloneName },
              { "type", _defaultClusterType.Value },
              { "site", _defaultSite.Value }
          };
          
          var createRes = await PostWithRetryAsync("/virtualization/clusters/", payload);
          if (createRes.IsSuccessStatusCode)
          {
              var newJson = await createRes.Content.ReadFromJsonAsync<JsonElement>();
              int newId = newJson.GetProperty("id").GetInt32();
              _clusterCache[standaloneName] = newId;
              SyncLogger.Info($"Created Synthetic Cluster: {standaloneName}");
              return newId;
          }
      }

      return defaultClusterId;
  }

  private async Task<Dictionary<string, int>> LoadVmCacheAsync()
  {
    var cache = new Dictionary<string, int>();
    var res = await GetWithRetryAsync("/virtualization/virtual-machines/?limit=0");
    if (!res.IsSuccessStatusCode) return cache;
    var json = await res.Content.ReadFromJsonAsync<JsonElement>();
    
    if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in results.EnumerateArray())
      {
        if (item.ValueKind == JsonValueKind.Object && 
            item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String &&
            item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
        {
          string name = nameProp.GetString() ?? "";
          name = DataNormalizer.NormalizeString(name);
          if (!string.IsNullOrEmpty(name))
          {
            cache[name] = idProp.GetInt32();
          }
        }
      }
    }
    return cache;
  }

  private async Task PreloadTagsAsync()
  {
      try
      {
          var res = await GetWithRetryAsync("/extras/tags/?limit=0");
          if (res.IsSuccessStatusCode)
          {
              var json = await res.Content.ReadFromJsonAsync<JsonElement>();
              if (json.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
              {
                  foreach (var item in results.EnumerateArray())
                  {
                      if (item.TryGetProperty("name", out var nameProp) && item.TryGetProperty("slug", out var slugProp))
                      {
                          _tagSlugMap[nameProp.GetString() ?? ""] = slugProp.GetString() ?? "";
                      }
                  }
              }
          }
      }
      catch (Exception ex)
      {
          SyncLogger.Warning($"[NetBox] Failed to preload tags: {ex.Message}");
      }
  }

  public async Task EnsureTagExistsAsync(string name)
  {
      try
      {
          string encodedName = Uri.EscapeDataString(name);
          var res = await GetWithRetryAsync($"/extras/tags/?name={encodedName}");
          if (res.IsSuccessStatusCode)
          {
              var json = await res.Content.ReadFromJsonAsync<JsonElement>();
              if (json.TryGetProperty("count", out var countProp) && countProp.GetInt32() > 0)
              {
                  return;
              }
          }

          string slug = name.ToLowerInvariant().Replace(" ", "-");
          slug = Regex.Replace(slug, @"[^a-z0-9\-]", "");

          var payload = new Dictionary<string, string>
          {
              { "name", name },
              { "slug", slug }
          };

          await PostWithRetryAsync("/extras/tags/", payload);
      }
      catch (Exception ex)
      {
          SyncLogger.Error($"[NetBoxClient] HTTP exception in EnsureTagExistsAsync for '{name}': {ex.Message}");
      }
  }

  public async Task<Dictionary<string, int>> EnsureDevicesExistAsync(List<HostAsset> h, int s, int r) => new();
  public async Task<Dictionary<string, CostCalculator.HostPricing>> GetHostPricingsAsync() => new();

  private async Task<HttpResponseMessage> PostWithRetryAsync(string endpoint, object payload, int maxRetries = 3)
  {
      string json = JsonSerializer.Serialize(payload);
      for (int i = 0; i < maxRetries; i++)
      {
          try
          {
              var content = new StringContent(json, Encoding.UTF8, "application/json");
              var response = await _client.PostAsync(_baseUrl + endpoint, content);
              if (response.IsSuccessStatusCode) return response;
              
              string errBody = await response.Content.ReadAsStringAsync();
              SyncLogger.Warning($"POST to {endpoint} returned {response.StatusCode}. Body: {errBody}. Retrying...");
          }
          catch (Exception ex)
          {
              SyncLogger.Warning($"POST to {endpoint} failed: {ex.Message}. Retrying...");
          }
          await Task.Delay(2000);
      }
      var finalContent = new StringContent(json, Encoding.UTF8, "application/json");
      var finalResp = await _client.PostAsync(_baseUrl + endpoint, finalContent);
      if (!finalResp.IsSuccessStatusCode)
      {
          string errBody = await finalResp.Content.ReadAsStringAsync();
          if (finalResp.StatusCode == System.Net.HttpStatusCode.BadRequest)
          {
              Console.WriteLine($"[NETBOX 400 EXACT PAYLOAD ERROR] POST {endpoint}: {errBody}");
              try 
              {
                  var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(errBody);
                  var fields = string.Join(", ", dict.Select(kv => $"{kv.Key}: {kv.Value}"));
                  SyncLogger.Error($"NetBox Validation Error on POST {endpoint} -> Fields: {fields}");
              } 
              catch { SyncLogger.Error($"Final POST to {endpoint} failed with 400. Body: {errBody}"); }
          }
          else
          {
              SyncLogger.Error($"Final POST to {endpoint} failed with {finalResp.StatusCode}. Body: {errBody}");
          }
      }
      return finalResp;
  }

  private async Task<HttpResponseMessage> PatchWithRetryAsync(string endpoint, object payload, int maxRetries = 3)
  {
      string json = JsonSerializer.Serialize(payload);
      for (int i = 0; i < maxRetries; i++)
      {
          try
          {
              var content = new StringContent(json, Encoding.UTF8, "application/json");
              var request = new HttpRequestMessage(HttpMethod.Patch, _baseUrl + endpoint) { Content = content };
              var response = await _client.SendAsync(request);
              if (response.IsSuccessStatusCode) return response;
              
              string errBody = await response.Content.ReadAsStringAsync();
              SyncLogger.Warning($"PATCH to {endpoint} returned {response.StatusCode}. Body: {errBody}. Retrying...");
          }
          catch (Exception ex)
          {
              SyncLogger.Warning($"PATCH to {endpoint} failed: {ex.Message}. Retrying...");
          }
          await Task.Delay(2000);
      }
      var finalContent = new StringContent(json, Encoding.UTF8, "application/json");
      var finalReq = new HttpRequestMessage(HttpMethod.Patch, _baseUrl + endpoint) { Content = finalContent };
      var finalResp = await _client.SendAsync(finalReq);
      if (!finalResp.IsSuccessStatusCode)
      {
          string errBody = await finalResp.Content.ReadAsStringAsync();
          if (finalResp.StatusCode == System.Net.HttpStatusCode.BadRequest)
          {
              Console.WriteLine($"[NETBOX 400 EXACT PAYLOAD ERROR] PATCH {endpoint}: {errBody}");
          }
          SyncLogger.Error($"Final PATCH to {endpoint} failed with {finalResp.StatusCode}. Body: {errBody}");
      }
      return finalResp;
  }

  private async Task<HttpResponseMessage> GetWithRetryAsync(string endpoint, int maxRetries = 3)
  {
      for (int i = 0; i < maxRetries; i++)
      {
          try
          {
              var response = await _client.GetAsync(_baseUrl + endpoint);
              if (response.IsSuccessStatusCode) return response;
              SyncLogger.Warning($"GET to {endpoint} returned {response.StatusCode}. Retrying...");
          }
          catch (Exception ex)
          {
              SyncLogger.Warning($"GET to {endpoint} failed: {ex.Message}. Retrying...");
          }
          await Task.Delay(2000);
      }
      return await _client.GetAsync(_baseUrl + endpoint);
  }
}
