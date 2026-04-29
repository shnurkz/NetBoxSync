using NetBoxSync.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetBoxSync.Providers;

public class ProxmoxProvider : IVirtualizationProvider
{
  private readonly HttpClient _client;
  private readonly string _baseUrl;
  private readonly string _clusterName;

  public ProxmoxProvider(string url, string token, string clusterName = "Proxmox-Cluster")
  {
    _baseUrl = url.TrimEnd('/');
    _clusterName = clusterName;
    var handler = new SocketsHttpHandler { SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = delegate { return true; } } };
    _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
    _client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token.Trim());
  }

  public async Task<List<VmAsset>> GetVirtualMachinesAsync()
  {
    var resResp = await _client.GetAsync($"{_baseUrl}/cluster/resources");
    var json = await resResp.Content.ReadFromJsonAsync<JsonElement>();
    var allResources = json.GetProperty("data").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "qemu").ToList();

    var assets = new List<VmAsset>();
    var semaphore = new SemaphoreSlim(10);
    var tasks = allResources.Select(async item =>
    {
      await semaphore.WaitAsync();
      try
      {
        int vmid = item.GetProperty("vmid").GetInt32();
        string node = item.GetProperty("node").GetString() ?? "unknown";
        var asset = new VmAsset
        {
          Name = item.GetProperty("name").GetString() ?? "unknown",
          Provider = "Proxmox",
          NodeName = node,
          IsRunning = item.GetProperty("status").GetString() == "running",
          Vcpus = item.TryGetProperty("maxcpu", out var c) ? c.GetInt32() : 1,
          MemoryMb = item.TryGetProperty("maxmem", out var m) ? (int)(m.GetInt64() / 1024 / 1024) : 0,
          DiskGb = item.TryGetProperty("maxdisk", out var d) ? (int)(d.GetInt64() / 1073741824) : 0
        };

        await EnrichViaConfigAsync(asset, node, vmid);
        if (asset.IsRunning) await EnrichViaAgentAsync(asset, node, vmid);
        lock (assets) { assets.Add(asset); }
      }
      finally { semaphore.Release(); }
    });
    await Task.WhenAll(tasks);
    return assets;
  }

  private async Task EnrichViaAgentAsync(VmAsset asset, string node, int vmid)
  {
    try
    {
      var osResp = await _client.GetAsync($"{_baseUrl}/nodes/{node}/qemu/{vmid}/agent/get-osinfo");
      if (osResp.IsSuccessStatusCode)
      {
        var osJson = await osResp.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement data = osJson.TryGetProperty("data", out var d) ? d : osJson;
        if (data.TryGetProperty("pretty-name", out var pn)) asset.FullOsName = pn.GetString() ?? "";
      }
      var netResp = await _client.GetAsync($"{_baseUrl}/nodes/{node}/qemu/{vmid}/agent/network-get-interfaces");
      if (netResp.IsSuccessStatusCode)
      {
        var netJson = await netResp.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement data = netJson.TryGetProperty("data", out var d) ? d : netJson;
        foreach (var iface in data.EnumerateArray())
        {
          if (iface.TryGetProperty("ip-addresses", out var ips))
          {
            foreach (var ip in ips.EnumerateArray())
            {
              var addr = ip.GetProperty("ip-address").GetString() ?? "";
              if (!addr.Contains(":") && addr != "127.0.0.1" && !addr.StartsWith("10.233.")) asset.IpAddresses.Add(addr);
            }
          }
        }
      }
    }
    catch { }
  }

  private async Task EnrichViaConfigAsync(VmAsset asset, string node, int vmid)
  {
    try
    {
      var resp = await _client.GetAsync($"{_baseUrl}/nodes/{node}/qemu/{vmid}/config");
      var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
      if (json.TryGetProperty("data", out var data))
      {
        if (data.TryGetProperty("net0", out var net0))
        {
          var match = Regex.Match(net0.GetString() ?? "", @"tag=(\d+)");
          if (match.Success) asset.PrimaryVlan = match.Groups[1].Value;
        }
        if (data.TryGetProperty("tags", out var tags)) asset.Tenant = tags.GetString()?.Split(',')[0];
      }
    }
    catch { }
  }
  public async Task<List<HostAsset>> GetHostsAsync() => new();
}
