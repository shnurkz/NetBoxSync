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
  var pveList = allVms.Where(v => v.Provider == "Proxmox").ToList();
  if (pveList.Any())
  {
    Console.WriteLine("\n--- Синхронизация Proxmox ---");
    await netbox.SyncVirtualMachinesAsync(pveList, ProxmoxClusterId, hostMap);
  }

  var vmwareList = allVms.Where(v => v.Provider == "VMware").ToList();
  if (vmwareList.Any())
  {
    Console.WriteLine("\n--- Синхронизация VMware ---");
    await netbox.SyncVirtualMachinesAsync(vmwareList, VmwareClusterId, hostMap);
  }

  // --- ИТОГОВЫЙ ОТЧЕТ ПО ЕМКОСТИ КЛАСТЕРА ---
  Console.WriteLine("\n--- ИТОГОВЫЙ ОТЧЕТ ПО ЕМКОСТИ КЛАСТЕРА ---");
  int totalCpu = allHosts.Sum(h => h.TotalCpuThreads);
  int totalRam = allHosts.Sum(h => h.TotalRamGb);
  int totalDisk = allHosts.Sum(h => h.TotalDiskGb);

  int usedCpu = allVms.Sum(v => v.Vcpus);
  int usedRam = (int)Math.Round((double)allVms.Sum(v => v.MemoryMb) / 1024);
  int usedDisk = allVms.Sum(v => v.DiskGb);

  string cpuPct = totalCpu > 0 ? $"({Math.Round((double)usedCpu / totalCpu * 100, 1)}%)" : "(0%)";
  string ramPct = totalRam > 0 ? $"({Math.Round((double)usedRam / totalRam * 100, 1)}%)" : "(0%)";
  string diskPct = totalDisk > 0 ? $"({Math.Round((double)usedDisk / totalDisk * 100, 1)}%)" : "(0%)";

  Console.WriteLine($"Всего ВМ: {allVms.Count}");
  Console.WriteLine($"CPU:  Занято {usedCpu} vCPU / Всего {totalCpu} потоков (Свободно: {totalCpu - usedCpu}) {cpuPct}");
  Console.WriteLine($"RAM:  Занято {usedRam} GB / Всего {totalRam} GB (Свободно: {totalRam - usedRam} GB) {ramPct}");
  Console.WriteLine($"DISK: Занято {usedDisk} GB / Всего {totalDisk} GB (Свободно: {totalDisk - usedDisk} GB) {diskPct}\n");

  Console.WriteLine(">>> Все задачи успешно завершены!");
}
catch (Exception ex)
{
  Console.WriteLine($"\nКРИТИЧЕСКИЙ СБОЙ: {ex.Message}");
}
