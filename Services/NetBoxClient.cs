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
  private readonly HashSet<string> _verifiedTags = new();
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

    int count = 0;
    foreach (var vm in vms)
    {
      try
      {
          string commentsMarkdown = MarkdownReportGenerator.Generate(vm);
          int resolvedClusterId = await ResolveClusterAsync(vm, clusterId);

          string endpoint = "/virtualization/virtual-machines/";
          var payload = new Dictionary<string, object>
          {
            { "name", vm.Name },
            { "cluster", resolvedClusterId },
            { "status", vm.IsRunning ? "active" : "offline" },
            { "vcpus", (decimal)vm.Vcpus },
            { "memory", (int)vm.MemoryMb },
            { "disk", vm.DiskGb * 1024 },
            { "comments", commentsMarkdown },
            { "custom_fields", new Dictionary<string, object>
              {
                { "cpu_cost", (int)Math.Round(vm.HardwareCost) },
                { "total_cost", (int)Math.Round(vm.TotalCost) }
              }
            }
          };

          if (hostMap.TryGetValue(vm.NodeName, out int devId)) payload["device"] = devId;
          if (!string.IsNullOrEmpty(vm.Tenant)) payload["tags"] = new List<object> { new { name = DataNormalizer.NormalizeString(vm.Tenant) } };

          HttpResponseMessage res;
          if (cache.TryGetValue(vm.Name, out int id))
          {
            endpoint += $"{id}/";
            res = await PatchWithRetryAsync(endpoint, payload);
          }
          else
          {
            res = await PostWithRetryAsync(endpoint, payload);
          }

          if (res.IsSuccessStatusCode) SyncLogger.Info($"[{++count}/{vms.Count}] {vm.Name} ... OK.");
          else SyncLogger.Error($"[{++count}/{vms.Count}] {vm.Name} ... ОШИБКА: {res.StatusCode}");
      }
      catch (Exception ex)
      {
          SyncLogger.Error($"[NetBoxClient] Failed to sync VM {vm.Name}: {ex.Message}");
      }
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
    var res = await GetWithRetryAsync("/virtualization/virtual-machines/?limit=2000");
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

  public async Task EnsureTagExistsAsync(string name) => await Task.CompletedTask;
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
              SyncLogger.Warning($"POST to {endpoint} returned {response.StatusCode}. Retrying...");
          }
          catch (Exception ex)
          {
              SyncLogger.Warning($"POST to {endpoint} failed: {ex.Message}. Retrying...");
          }
          await Task.Delay(2000);
      }
      var finalContent = new StringContent(json, Encoding.UTF8, "application/json");
      return await _client.PostAsync(_baseUrl + endpoint, finalContent);
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
              SyncLogger.Warning($"PATCH to {endpoint} returned {response.StatusCode}. Retrying...");
          }
          catch (Exception ex)
          {
              SyncLogger.Warning($"PATCH to {endpoint} failed: {ex.Message}. Retrying...");
          }
          await Task.Delay(2000);
      }
      var finalContent = new StringContent(json, Encoding.UTF8, "application/json");
      return await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, _baseUrl + endpoint) { Content = finalContent });
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
