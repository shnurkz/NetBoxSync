namespace NetBoxSync.Models;

public class VmAsset
{
  public string Name { get; set; } = string.Empty;
  public string Provider { get; set; } = string.Empty; // "Proxmox" или "VMware"
  public string ClusterName { get; set; } = string.Empty;
  public string NodeName { get; set; } = string.Empty; // Физический хост
  public string Tenant { get; set; } = string.Empty; // Проект

  public int Vcpus { get; set; }
  public int MemoryMb { get; set; }
  public int DiskGb { get; set; }

  public string OsName { get; set; } = string.Empty;
  public bool IsTemplate { get; set; }
  public bool IsRunning { get; set; }

  // Эти поля заполнит CostCalculator перед отправкой в NetBox
  public decimal HardwareCost { get; set; }
  public decimal LicenseCost { get; set; }
  public decimal TotalCost => HardwareCost + LicenseCost;

  // Новые поля для обогащения
  public string FullOsName { get; set; } = "";
  public List<string> IpAddresses { get; set; } = new();
  public List<string> SoftwareList { get; set; } = new();
  public string GuestState { get; set; } = "";
  public string PrimaryVlan { get; set; } = "";
}
