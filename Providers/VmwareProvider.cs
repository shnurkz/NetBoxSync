using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NetBoxSync.Models;
using NetBoxSync.Utilities;

namespace NetBoxSync.Providers;

public class VmwareProvider : IVirtualizationProvider
{
  private readonly HttpClient _client;
  private readonly string _baseUrl;
  private readonly string _username;
  private readonly string _password;
  private readonly string _clusterName;
  private string? _sessionId;

  public VmwareProvider(string url, string username, string password, string clusterName = "VMware-Cluster")
  {
    _baseUrl = url.TrimEnd('/');
    _username = username;
    _password = password;
    _clusterName = clusterName;

    var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = delegate { return true; } };
    _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
  }

  private async Task AuthenticateAsync()
  {
    var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
    var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/session");
    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);
    var resp = await _client.SendAsync(req);
    
    var respStr = await resp.Content.ReadAsStringAsync();
    try
    {
      using var doc = JsonDocument.Parse(respStr);
      var sessionJson = doc.RootElement;
      if (sessionJson.ValueKind == JsonValueKind.String)
      {
        _sessionId = sessionJson.GetString();
      }
      else if (sessionJson.ValueKind == JsonValueKind.Object && sessionJson.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
      {
        _sessionId = val.GetString();
      }
      else
      {
        _sessionId = respStr.Trim('"');
      }
    }
    catch
    {
      _sessionId = respStr.Trim('"');
    }

    if (!string.IsNullOrEmpty(_sessionId))
    {
      _client.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", _sessionId);
    }
  }

  public async Task<List<VmAsset>> GetVirtualMachinesAsync()
  {
    if (string.IsNullOrEmpty(_sessionId)) await AuthenticateAsync();

    var hostDict = new Dictionary<string, string>();
    try
    {
      var hostResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/host");
      if (hostResp.IsSuccessStatusCode)
      {
        var hostJson = await hostResp.Content.ReadFromJsonAsync<JsonElement>();
        var hostItems = hostJson.ValueKind == JsonValueKind.Array ? hostJson.EnumerateArray() :
                       (hostJson.ValueKind == JsonValueKind.Object && hostJson.TryGetProperty("value", out var hv) && hv.ValueKind == JsonValueKind.Array ? hv.EnumerateArray() : Enumerable.Empty<JsonElement>());
        foreach (var h in hostItems)
        {
          if (h.ValueKind == JsonValueKind.Object && h.TryGetProperty("host", out var idProp) && idProp.ValueKind == JsonValueKind.String &&
              h.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
          {
            hostDict[idProp.GetString()!] = nameProp.GetString()!;
          }
        }
      }
    }
    catch (Exception ex) { Console.WriteLine($"\n      [!] Ошибка VMware Host API: {ex.Message}"); }

    var netDict = new Dictionary<string, string>();
    try
    {
      var netResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/network");
      if (netResp.IsSuccessStatusCode)
      {
        var netJson = await netResp.Content.ReadFromJsonAsync<JsonElement>();
        var netItems = netJson.ValueKind == JsonValueKind.Array ? netJson.EnumerateArray() :
                       (netJson.ValueKind == JsonValueKind.Object && netJson.TryGetProperty("value", out var nv) && nv.ValueKind == JsonValueKind.Array ? nv.EnumerateArray() : Enumerable.Empty<JsonElement>());
        foreach (var n in netItems)
        {
          if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty("network", out var idProp) && idProp.ValueKind == JsonValueKind.String &&
              n.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
          {
            netDict[idProp.GetString()!] = nameProp.GetString()!;
          }
        }
      }
    }
    catch (Exception ex) { Console.WriteLine($"\n      [!] Ошибка VMware Network API: {ex.Message}"); }

    Console.Write("   [VMware] Получение списка виртуальных машин... ");
    var resp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm");
    var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

    List<JsonElement> vmItems = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToList() :
                               (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Array ? val.EnumerateArray().ToList() : new());

    Console.WriteLine($"Найдено: {vmItems.Count}");

    var vms = new List<VmAsset>();
    var semaphore = new SemaphoreSlim(10);
    var tasks = vmItems.Select(async item =>
    {
      await semaphore.WaitAsync();
      try
      {
        string vmId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("vm", out var vmProp) && vmProp.ValueKind == JsonValueKind.String ? (vmProp.GetString() ?? "") : "";
        string hostId = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("host", out var hProp) && hProp.ValueKind == JsonValueKind.String ? (hProp.GetString() ?? "") : "";
        string nodeNameRaw = hostDict.TryGetValue(hostId, out var hn) ? hn : "unknown";
        string guestOs = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("guest_OS", out var gos) && gos.ValueKind == JsonValueKind.String ? gos.GetString() ?? "" : "";
        string rawName = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? (nameProp.GetString() ?? "unknown") : "unknown";

        string name = DataNormalizer.NormalizeString(rawName);
        if (string.IsNullOrEmpty(name)) return;

        var asset = new VmAsset
        {
          Name = name,
          Provider = DataNormalizer.NormalizeString("VMware"),
          ClusterName = DataNormalizer.NormalizeString(_clusterName),
          NodeName = DataNormalizer.NormalizeString(nodeNameRaw),
          IsRunning = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("power_state", out var pwrProp) && pwrProp.ValueKind == JsonValueKind.String && pwrProp.GetString() == "POWERED_ON",
          MemoryMb = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("memory_size_MiB", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0,
          Vcpus = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("cpu_count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1
        };

        await EnrichVmwareAssetAsync(asset, vmId, guestOs, netDict);

        lock (vms) { vms.Add(asset); }
      }
      catch { }
      finally { semaphore.Release(); }
    });

    await Task.WhenAll(tasks);
    return vms;
  }

  private async Task EnrichVmwareAssetAsync(VmAsset asset, string vmId, string fallbackOs, Dictionary<string, string> netDict)
  {
    try
    {
      var ethResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm/{vmId}/hardware/ethernet");
      if (ethResp.IsSuccessStatusCode)
      {
        var ethJson = await ethResp.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement ethData = ethJson.ValueKind == JsonValueKind.Object && ethJson.TryGetProperty("value", out var ev) ? ev : ethJson;
        
        IEnumerable<JsonElement> ethItems = Enumerable.Empty<JsonElement>();
        if (ethData.ValueKind == JsonValueKind.Array) ethItems = ethData.EnumerateArray();
        else if (ethData.ValueKind == JsonValueKind.Object) ethItems = ethData.EnumerateObject().Select(p => p.Value);

        var nicIds = new List<string>();
        foreach (var eth in ethItems)
        {
          if (eth.ValueKind == JsonValueKind.String)
          {
             nicIds.Add(eth.GetString()!);
          }
          else if (eth.ValueKind == JsonValueKind.Object && eth.TryGetProperty("nic", out var nicProp) && nicProp.ValueKind == JsonValueKind.String)
          {
             nicIds.Add(nicProp.GetString()!);
          }
          else if (eth.ValueKind == JsonValueKind.Object && eth.TryGetProperty("key", out var keyProp) && keyProp.ValueKind == JsonValueKind.String)
          {
             nicIds.Add(keyProp.GetString()!);
          }
        }

        foreach (var nicId in nicIds)
        {
           var nicResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm/{vmId}/hardware/ethernet/{nicId}");
           if (nicResp.IsSuccessStatusCode)
           {
               var nicJson = await nicResp.Content.ReadFromJsonAsync<JsonElement>();
               var nicData = nicJson.ValueKind == JsonValueKind.Object && nicJson.TryGetProperty("value", out var nv) ? nv : nicJson;
               if (nicData.ValueKind == JsonValueKind.Object && nicData.TryGetProperty("backing", out var backing) && backing.ValueKind == JsonValueKind.Object &&
                   backing.TryGetProperty("network", out var netProp) && netProp.ValueKind == JsonValueKind.String)
               {
                  string netId = netProp.GetString()!;
                  if (netDict.TryGetValue(netId, out var netName))
                  {
                      asset.PrimaryVlan = netName;
                      break;
                  }
               }
           }
        }
      }

      if (asset.IsRunning)
      {
        var idResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm/{vmId}/guest/identity");
        if (idResp.IsSuccessStatusCode)
        {
          var json = await idResp.Content.ReadFromJsonAsync<JsonElement>();
          JsonElement data = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("value", out var v) ? v : json;
          if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("full_name", out var fn)) 
          {
              if (fn.ValueKind == JsonValueKind.String)
              {
                  asset.FullOsName = fn.GetString() ?? "";
              }
              else if (fn.ValueKind == JsonValueKind.Object && fn.TryGetProperty("default_message", out var dm) && dm.ValueKind == JsonValueKind.String)
              {
                  asset.FullOsName = dm.GetString() ?? "";
              }
          }
        }

        if (string.IsNullOrEmpty(asset.FullOsName) || asset.FullOsName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            asset.FullOsName = fallbackOs;
        }

        var netResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm/{vmId}/guest/networking/interfaces");
        if (netResp.IsSuccessStatusCode)
        {
          var netJson = await netResp.Content.ReadFromJsonAsync<JsonElement>();
          var interfaces = netJson.ValueKind == JsonValueKind.Object && netJson.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() :
                          (netJson.ValueKind == JsonValueKind.Array ? netJson.EnumerateArray() : Enumerable.Empty<JsonElement>());

          foreach (var iface in interfaces)
          {
            if (iface.ValueKind == JsonValueKind.Object && iface.TryGetProperty("ip", out var ipObj) && ipObj.ValueKind == JsonValueKind.Object && ipObj.TryGetProperty("ip_addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
            {
              foreach (var addr in addrs.EnumerateArray())
              {
                if (addr.ValueKind == JsonValueKind.Object && addr.TryGetProperty("ip_address", out var ipVal) && ipVal.ValueKind == JsonValueKind.String)
                {
                  string ip = ipVal.GetString() ?? "";
                  if (!string.IsNullOrEmpty(ip) && !ip.Contains(":") &&
                      ip != "127.0.0.1" && !ip.StartsWith("169.254.") && !ip.StartsWith("10.233."))
                  {
                    asset.IpAddresses.Add(ip);
                  }
                }
              }
            }
          }
        }
      }
      else
      {
        if (string.IsNullOrEmpty(asset.FullOsName))
        {
            asset.FullOsName = fallbackOs;
        }
      }
    }
    catch (Exception ex) { Console.WriteLine($"\n      [!] Ошибка VMware Enrichment API: {ex.Message}"); }
  }

  public async Task<List<HostAsset>> GetHostsAsync() => new();
}

