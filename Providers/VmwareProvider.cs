using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NetBoxSync.Models;

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
    _sessionId = (await resp.Content.ReadAsStringAsync()).Trim('"');
    _client.DefaultRequestHeaders.TryAddWithoutValidation("vmware-api-session-id", _sessionId);
  }

  public async Task<List<VmAsset>> GetVirtualMachinesAsync()
  {
    if (string.IsNullOrEmpty(_sessionId)) await AuthenticateAsync();

    Console.Write("   [VMware] Получение списка виртуальных машин... ");
    var resp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm");
    var json = await resp.Content.ReadFromJsonAsync<JsonElement>();

    List<JsonElement> vmItems = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToList() :
                               (json.TryGetProperty("value", out var val) ? val.EnumerateArray().ToList() : new());

    Console.WriteLine($"Найдено: {vmItems.Count}");

    var vms = new List<VmAsset>();
    var semaphore = new SemaphoreSlim(10);
    var tasks = vmItems.Select(async item =>
    {
      await semaphore.WaitAsync();
      try
      {
        string vmId = item.GetProperty("vm").GetString() ?? "";
        var asset = new VmAsset
        {
          Name = item.GetProperty("name").GetString() ?? "unknown",
          Provider = "VMware",
          ClusterName = _clusterName,
          IsRunning = item.GetProperty("power_state").GetString() == "POWERED_ON",
          MemoryMb = item.TryGetProperty("memory_size_MiB", out var m) ? m.GetInt32() : 0,
          Vcpus = item.TryGetProperty("cpu_count", out var c) ? c.GetInt32() : 1
        };

        if (asset.IsRunning) await EnrichVmwareGuestAsync(asset, vmId);

        lock (vms) { vms.Add(asset); }
      }
      catch { }
      finally { semaphore.Release(); }
    });

    await Task.WhenAll(tasks);
    return vms;
  }

  private async Task EnrichVmwareGuestAsync(VmAsset asset, string vmId)
  {
    try
    {
      var idResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm/{vmId}/guest/identity");
      if (idResp.IsSuccessStatusCode)
      {
        var json = await idResp.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement data = json.TryGetProperty("value", out var v) ? v : json;
        if (data.TryGetProperty("full_name", out var fn)) asset.FullOsName = fn.GetString() ?? "";
      }

      var netResp = await _client.GetAsync($"{_baseUrl}/api/vcenter/vm/{vmId}/guest/networking/interfaces");
      if (netResp.IsSuccessStatusCode)
      {
        var netJson = await netResp.Content.ReadFromJsonAsync<JsonElement>();
        var interfaces = netJson.TryGetProperty("value", out var v) ? v.EnumerateArray() :
                        (netJson.ValueKind == JsonValueKind.Array ? netJson.EnumerateArray() : Enumerable.Empty<JsonElement>());

        foreach (var iface in interfaces)
        {
          if (iface.TryGetProperty("ip", out var ipObj) && ipObj.TryGetProperty("ip_addresses", out var addrs))
          {
            foreach (var addr in addrs.EnumerateArray())
            {
              string ip = addr.GetProperty("ip_address").GetString() ?? "";
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
    catch (Exception ex) { Console.WriteLine($"\n      [!] Ошибка VMware Guest API: {ex.Message}"); }
  }

  public async Task<List<HostAsset>> GetHostsAsync() => new();
}
