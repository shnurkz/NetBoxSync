using NetBoxSync.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetBoxSync.Services;
using NetBoxSync.Utilities;

namespace NetBoxSync.Providers;

public class ProxmoxProvider : IVirtualizationProvider
{
  private readonly HttpClient _client;
  private readonly string _baseUrl;
  private readonly string _clusterName;
  private readonly SshDiscoveryService _sshDiscovery;
  private readonly string _sshUser;
  private readonly string _sshPass;

  public ProxmoxProvider(string url, string token, string clusterName = "Proxmox-Cluster", string sshUser = "tech_svc", string sshPass = "P@ssw0rd123!")
  {
    _baseUrl = url.TrimEnd('/');
    _clusterName = clusterName;
    _sshUser = sshUser;
    _sshPass = sshPass;
    _sshDiscovery = new SshDiscoveryService();
    var handler = new SocketsHttpHandler { SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = delegate { return true; } } };
    _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
    _client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token.Trim());
  }

  public async Task<List<VmAsset>> GetVirtualMachinesAsync()
  {
    var resResp = await _client.GetAsync($"{_baseUrl}/cluster/resources");
    var json = await resResp.Content.ReadFromJsonAsync<JsonElement>();
    var allResources = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var dataList) && dataList.ValueKind == JsonValueKind.Array 
      ? dataList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String && typeProp.GetString() == "qemu").ToList() 
      : new List<JsonElement>();

    var assets = new List<VmAsset>();
    var semaphore = new SemaphoreSlim(10);
    var tasks = allResources.Select(async item =>
    {
      await semaphore.WaitAsync();
      try
      {
        int vmid = item.TryGetProperty("vmid", out var vmidProp) && vmidProp.ValueKind == JsonValueKind.Number ? vmidProp.GetInt32() : 0;
        string node = item.TryGetProperty("node", out var nodeProp) && nodeProp.ValueKind == JsonValueKind.String ? nodeProp.GetString() ?? "unknown" : "unknown";
        var asset = new VmAsset
        {
          Name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() ?? "unknown" : "unknown",
          Provider = "Proxmox",
          NodeName = node,
          IsRunning = item.TryGetProperty("status", out var statProp) && statProp.ValueKind == JsonValueKind.String && statProp.GetString() == "running",
          Vcpus = item.TryGetProperty("maxcpu", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1,
          MemoryMb = item.TryGetProperty("maxmem", out var m) && m.ValueKind == JsonValueKind.Number ? (int)(m.GetInt64() / 1024 / 1024) : 0,
          DiskGb = item.TryGetProperty("maxdisk", out var d) && d.ValueKind == JsonValueKind.Number ? (int)(d.GetInt64() / 1073741824) : 0
        };

        await EnrichViaConfigAsync(asset, node, vmid);
        if (asset.IsRunning) 
        {
            await EnrichViaAgentAsync(asset, node, vmid);
            
            bool sshSuccess = false;
            if (asset.IpAddresses.Any())
            {
                var swList = await _sshDiscovery.GetInstalledSoftwareAsync(asset.IpAddresses.First(), _sshUser, _sshPass);
                if (swList.Any())
                {
                    asset.SoftwareList = swList;
                    asset.DiscoveryMethod = "SSH";
                    sshSuccess = true;
                }
            }

            if (!sshSuccess)
            {
                await TryAgentSoftwareDiscoveryAsync(asset, node, vmid);
            }

            if (string.IsNullOrEmpty(asset.DiscoveryMethod))
                asset.DiscoveryMethod = "API Only";
        }
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
        JsonElement data = osJson.ValueKind == JsonValueKind.Object && osJson.TryGetProperty("data", out var d) ? d : osJson;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var resultObj) && resultObj.ValueKind == JsonValueKind.Object)
        {
            data = resultObj;
        }

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("pretty-name", out var pn) && pn.ValueKind == JsonValueKind.String) 
        {
            asset.FullOsName = pn.GetString() ?? "";
        }
      }
      var netResp = await _client.GetAsync($"{_baseUrl}/nodes/{node}/qemu/{vmid}/agent/network-get-interfaces");
      if (netResp.IsSuccessStatusCode)
      {
        var netJson = await netResp.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement data = netJson.ValueKind == JsonValueKind.Object && netJson.TryGetProperty("data", out var d) ? d : netJson;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var resultObj) && resultObj.ValueKind == JsonValueKind.Array)
        {
            data = resultObj;
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var iface in data.EnumerateArray())
            {
              if (iface.ValueKind == JsonValueKind.Object && iface.TryGetProperty("ip-addresses", out var ips) && ips.ValueKind == JsonValueKind.Array)
              {
                foreach (var ip in ips.EnumerateArray())
                {
                  if (ip.ValueKind == JsonValueKind.Object && ip.TryGetProperty("ip-address", out var addrProp) && addrProp.ValueKind == JsonValueKind.String)
                  {
                      var addr = addrProp.GetString() ?? "";
                      if (!string.IsNullOrEmpty(addr) && !addr.Contains(":") && addr != "127.0.0.1" && !addr.StartsWith("10.233.")) 
                      {
                          asset.IpAddresses.Add(addr);
                      }
                  }
                }
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
      if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
      {
        foreach (var prop in data.EnumerateObject())
        {
           if (prop.Name.StartsWith("net") && prop.Value.ValueKind == JsonValueKind.String)
           {
               var match = Regex.Match(prop.Value.GetString() ?? "", @"tag=(\d+)");
               if (match.Success)
               {
                   asset.VlanId = match.Groups[1].Value;
                   asset.PrimaryVlan = $"VLAN_{asset.VlanId}";
                   break;
               }
           }
        }
        if (data.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.String) 
        {
            asset.Tenant = tags.GetString()?.Split(',')[0] ?? "";
        }
      }
    }
    catch { }
  }

  private async Task TryAgentSoftwareDiscoveryAsync(VmAsset asset, string node, int vmid)
  {
      try
      {
          var cmdArray = asset.FullOsName.Contains("Ubuntu", StringComparison.OrdinalIgnoreCase) || asset.FullOsName.Contains("Debian", StringComparison.OrdinalIgnoreCase)
              ? new[] { "dpkg-query", "-W", "-f=${Package}|${Version}\n" }
              : new[] { "rpm", "-qa", "--qf", "%{NAME}|%{VERSION}\n" };

          var payload = new { command = cmdArray };
          var execResp = await _client.PostAsJsonAsync($"{_baseUrl}/nodes/{node}/qemu/{vmid}/agent/exec", payload);
          
          if (execResp.IsSuccessStatusCode)
          {
              var execJson = await execResp.Content.ReadFromJsonAsync<JsonElement>();
              if (execJson.ValueKind == JsonValueKind.Object && execJson.TryGetProperty("data", out var data) && data.TryGetProperty("pid", out var pidProp) && pidProp.ValueKind == JsonValueKind.Number)
              {
                  int pid = pidProp.GetInt32();
                  await Task.Delay(2000); // Give agent time to execute

                  var statusResp = await _client.GetAsync($"{_baseUrl}/nodes/{node}/qemu/{vmid}/agent/exec-status?pid={pid}");
                  if (statusResp.IsSuccessStatusCode)
                  {
                      var statusJson = await statusResp.Content.ReadFromJsonAsync<JsonElement>();
                      if (statusJson.ValueKind == JsonValueKind.Object && statusJson.TryGetProperty("data", out var statusData) && statusData.TryGetProperty("out-data", out var outDataProp) && outDataProp.ValueKind == JsonValueKind.String)
                      {
                          var lines = outDataProp.GetString()?.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                          if (lines != null && lines.Any())
                          {
                              asset.SoftwareList.AddRange(lines.Select(l => l.Trim()));
                              asset.DiscoveryMethod = "Agent API";
                          }
                      }
                  }
              }
          }
      }
      catch { }
  }
  public async Task<List<HostAsset>> GetHostsAsync() => new();
}
