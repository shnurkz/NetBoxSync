using NetBoxSync.Models;

namespace NetBoxSync.Services;

public class CostCalculator
{
  private readonly Dictionary<string, HostPricing> _hostPrices;

  private const decimal RhelHostLicenseCost = 150000;

  // Конструктор теперь принимает данные из NetBox
  public CostCalculator(Dictionary<string, HostPricing> hostPrices)
  {
    _hostPrices = hostPrices;
  }

  public void Calculate(List<VmAsset> vms)
  {
    var rhelVmsPerHost = vms.Where(IsRhel).GroupBy(v => v.NodeName)
                            .ToDictionary(g => g.Key, g => g.Count());

    foreach (var vm in vms)
    {
      if (_hostPrices.TryGetValue(vm.NodeName, out var prices))
      {
        decimal ramGb = vm.MemoryMb / 1024M;
        vm.HardwareCost = (vm.Vcpus * prices.PricePerCpu) + (ramGb * prices.PricePerRamGb) + (vm.DiskGb * prices.PricePerDiskGb);
      }
      else
      {
        vm.HardwareCost = 0;
      }

      vm.LicenseCost = 0;
      if (IsRhel(vm) && rhelVmsPerHost.TryGetValue(vm.NodeName, out int rhelCount) && rhelCount > 0)
      {
        vm.LicenseCost = RhelHostLicenseCost / rhelCount;
      }
    }
  }

  private bool IsRhel(VmAsset vm) => vm.Name.Contains("rhel", StringComparison.OrdinalIgnoreCase);

  public class HostPricing
  {
    public decimal PurchasePrice { get; set; }
    public decimal CpuWeight { get; set; }
    public decimal RamWeight { get; set; }
    public decimal DiskWeight { get; set; }
    public decimal CpuOvercommit { get; set; }
    public int TotalCpuThreads { get; set; }
    public int TotalRamGb { get; set; }
    public int TotalDiskGb { get; set; }

    public decimal PricePerCpu => TotalCpuThreads > 0 && CpuOvercommit > 0 ? (PurchasePrice * CpuWeight) / (TotalCpuThreads * CpuOvercommit) : 0;
    public decimal PricePerRamGb => TotalRamGb > 0 ? (PurchasePrice * RamWeight) / TotalRamGb : 0;
    public decimal PricePerDiskGb => TotalDiskGb > 0 ? (PurchasePrice * DiskWeight) / TotalDiskGb : 0;
  }
}
