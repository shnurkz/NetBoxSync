using NetBoxSync.Providers;
using NetBoxSync.Services;

// --- NETBOX ---
const string NetboxUrl = "https://10.1.128.20/api/";
const string NetboxToken = "6G76oJimgPJcoEgvpYTY3hFUwam8ZKZ4BdI6GZCy";

// --- ШАБЛОНЫ ДЛЯ АВТОСОЗДАНИЯ ХОСТОВ ---
const int DefaultSiteId = 1;       // ID для Площадки (Site)
const int DefaultRoleId = 1;       // ID для Роли устройства (Device Role)
// DefaultDeviceTypeId удален, так как типы теперь создаются динамически

// --- PROXMOX ---
const string ProxmoxUrl = "https://10.1.11.27:8006/api2/json/";
const string ProxmoxAuth = "PVEAPIToken=root@pam!auditor=9189fc7c-ea90-410d-aadf-29ea62a9a5c4";
const int ProxmoxClusterId = 2;

// --- VMWARE ---
const string VmwareUrl = "https://vcenter-dit-test.npck.kz/";
const string VmwareUser = "administrator@npck.kz";
const string VmwarePass = "Almaty_1985";
const int VmwareClusterId = 1; // Укажи реальный ID кластера VMware из NetBox

Console.WriteLine(">>> Старт модульной синхронизации (Proxmox + VMware) <<<");

try
{
  var allVms = new List<NetBoxSync.Models.VmAsset>();
  var allHosts = new List<NetBoxSync.Models.HostAsset>();

  // 1. Сбор данных из Proxmox
  IVirtualizationProvider proxmox = new ProxmoxProvider(ProxmoxUrl, ProxmoxAuth);
  allVms.AddRange(await proxmox.GetVirtualMachinesAsync());
  allHosts.AddRange(await proxmox.GetHostsAsync()); // Добавили сбор железа

  // 2. Сбор данных из VMware
  IVirtualizationProvider vmware = new VmwareProvider(VmwareUrl, VmwareUser, VmwarePass);
  allVms.AddRange(await vmware.GetVirtualMachinesAsync());
  allHosts.AddRange(await vmware.GetHostsAsync());  // Добавили сбор железа

  Console.WriteLine($"\nВсего собрано ВМ: {allVms.Count}, Физических хостов: {allHosts.Count}");

  // Инициализируем клиент NetBox один раз
  var netbox = new NetBoxClient(NetboxUrl, NetboxToken);

  // 3. АВТОСОЗДАНИЕ ФИЗИЧЕСКИХ ХОСТОВ И МОДЕЛЕЙ (Сохраняем карту ID)
  var hostMap = await netbox.EnsureDevicesExistAsync(allHosts, DefaultSiteId, DefaultRoleId);

  // 4. Расчет стоимости
  Console.WriteLine("\nРасчет стоимости ресурсов и лицензий...");
  var hostPrices = await netbox.GetHostPricingsAsync();
  var calculator = new CostCalculator(hostPrices);
  calculator.Calculate(allVms);

  // 5. Отправка ВМ в NetBox (передаем hostMap)
  var pveList = allVms.Where(v => string.Equals(v.Provider, "Proxmox", StringComparison.OrdinalIgnoreCase)).ToList();
  if (pveList.Any())
  {
    Console.WriteLine("\n--- Синхронизация Proxmox ---");
    await netbox.SyncVirtualMachinesAsync(pveList, ProxmoxClusterId, hostMap);
  }

  var vmwareList = allVms.Where(v => string.Equals(v.Provider, "VMware", StringComparison.OrdinalIgnoreCase)).ToList();
  if (vmwareList.Any())
  {
    Console.WriteLine("\n--- Синхронизация VMware ---");
    await netbox.SyncVirtualMachinesAsync(vmwareList, VmwareClusterId, hostMap);
  }

  // --- ИТОГОВЫЙ ОТЧЕТ ПО ЕМКОСТИ КЛАСТЕРА ---
  NetBoxSync.Utilities.SyncLogger.Info("\n--- ИТОГОВЫЙ ОТЧЕТ ПО ЕМКОСТИ КЛАСТЕРА ---");
  
  if (!allHosts.Any()) 
  {
      NetBoxSync.Utilities.SyncLogger.Warning("No hosts found!");
  }

  int totalCpu = allHosts.Sum(h => h.TotalCpuThreads);
  int totalRam = allHosts.Sum(h => h.TotalRamGb);
  int totalDisk = allHosts.Sum(h => h.TotalDiskGb);

  int usedCpu = allVms.Sum(v => v.Vcpus);
  double usedRam = allVms.Sum(v => v.MemoryMb) / 1024.0;
  int usedDisk = allVms.Sum(v => v.DiskGb);

  string cpuPct = totalCpu > 0 ? $"({((double)usedCpu * 100 / totalCpu):F1}%)" : "(0%)";
  string ramPct = totalRam > 0 ? $"({(usedRam * 100 / totalRam):F1}%)" : "(0%)";
  string diskPct = totalDisk > 0 ? $"({((double)usedDisk * 100 / totalDisk):F1}%)" : "(0%)";

  NetBoxSync.Utilities.SyncLogger.Info($"Всего ВМ: {allVms.Count}");
  NetBoxSync.Utilities.SyncLogger.Info($"CPU:  Занято {usedCpu:N0} vCPU / Всего {totalCpu:N0} потоков (Свободно: {(totalCpu - usedCpu):N0}) {cpuPct}");
  NetBoxSync.Utilities.SyncLogger.Info($"RAM:  Занято {usedRam:F1} GB / Всего {totalRam:N0} GB (Свободно: {(totalRam - usedRam):F1} GB) {ramPct}");
  NetBoxSync.Utilities.SyncLogger.Info($"DISK: Занято {usedDisk:N0} GB / Всего {totalDisk:N0} GB (Свободно: {(totalDisk - usedDisk):N0} GB) {diskPct}\n");

  NetBoxSync.Utilities.SyncLogger.Info(">>> Все задачи успешно завершены!");
}
catch (Exception ex)
{
  NetBoxSync.Utilities.SyncLogger.Error($"\nКРИТИЧЕСКИЙ СБОЙ: {ex.Message}");
}
