using NetBoxSync.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetBoxSync.Services;

public class NetBoxClient
{
  private readonly HttpClient _client;
  private readonly string _baseUrl;
  private readonly HashSet<string> _verifiedTags = new();

  public NetBoxClient(string url, string token)
  {
    _baseUrl = url.TrimEnd('/');
    var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = delegate { return true; } };
    _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", token.Trim());
  }

  public async Task SyncVirtualMachinesAsync(List<VmAsset> vms, int clusterId, Dictionary<string, int> hostMap)
  {
    Console.WriteLine($"\n--- [NetBox] Синхронизация {vms.Count} машин ---");
    var cache = await LoadVmCacheAsync();

    int count = 0;
    foreach (var vm in vms)
    {
      Console.Write($"   [{++count}/{vms.Count}] {vm.Name}... ");

      // Собираем Markdown-отчет для поля Comments (здесь будет ОС и IP из агента)
      var sb = new StringBuilder();
      sb.AppendLine($"### Данные инвентаризации ({DateTime.Now:yyyy-MM-dd HH:mm})");
      sb.AppendLine($"**ОС:** {(string.IsNullOrEmpty(vm.FullOsName) ? "Не определено (Агент?)" : vm.FullOsName)}");
      sb.AppendLine($"**Провайдер:** {vm.Provider} | **Хост:** {vm.NodeName}");

      if (!string.IsNullOrEmpty(vm.PrimaryVlan))
        sb.AppendLine($"**VLAN:** {vm.PrimaryVlan}");

      if (vm.IpAddresses != null && vm.IpAddresses.Any())
      {
        sb.AppendLine("\n**Основные IP:**");
        foreach (var ip in vm.IpAddresses.Distinct()) sb.AppendLine($"* {ip}");
      }

      string endpoint = "/virtualization/virtual-machines/";
      HttpMethod method = HttpMethod.Post;
      if (cache.TryGetValue(vm.Name, out int id))
      {
        endpoint += $"{id}/";
        method = HttpMethod.Patch;
      }

      var payload = new Dictionary<string, object>
      {
        { "name", vm.Name },
        { "cluster", clusterId },
        { "status", vm.IsRunning ? "active" : "offline" },
        { "vcpus", (decimal)vm.Vcpus },
        { "memory", (int)vm.MemoryMb },
        { "disk", vm.DiskGb * 1024 },
        { "comments", sb.ToString() }, // Тот самый Markdown
        { "custom_fields", new Dictionary<string, object>
          {
            { "cpu_cost", (int)Math.Round(vm.HardwareCost) },
            { "total_cost", (int)Math.Round(vm.TotalCost) }
          }
        }
      };

      if (hostMap.TryGetValue(vm.NodeName, out int devId)) payload["device"] = devId;
      if (!string.IsNullOrEmpty(vm.Tenant)) payload["tags"] = new List<object> { new { name = vm.Tenant } };

      var request = new HttpRequestMessage(method, _baseUrl + endpoint);
      request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
      var res = await _client.SendAsync(request);

      if (res.IsSuccessStatusCode) Console.WriteLine("OK.");
      else Console.WriteLine($"ОШИБКА: {res.StatusCode}");
    }
  }

  private async Task<Dictionary<string, int>> LoadVmCacheAsync()
  {
    var cache = new Dictionary<string, int>();
    var res = await _client.GetAsync($"{_baseUrl}/virtualization/virtual-machines/?limit=2000");
    if (!res.IsSuccessStatusCode) return cache;
    var json = await res.Content.ReadFromJsonAsync<JsonElement>();
    foreach (var item in json.GetProperty("results").EnumerateArray())
      cache[item.GetProperty("name").GetString()!] = item.GetProperty("id").GetInt32();
    return cache;
  }

  public async Task EnsureTagExistsAsync(string name) => await Task.CompletedTask;
  public async Task<Dictionary<string, int>> EnsureDevicesExistAsync(List<HostAsset> h, int s, int r) => new();
  public async Task<Dictionary<string, CostCalculator.HostPricing>> GetHostPricingsAsync() => new();
}
