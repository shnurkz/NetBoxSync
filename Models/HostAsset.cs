namespace NetBoxSync.Models;

public class HostAsset
{
  public string Name { get; set; } = string.Empty;
  public string Provider { get; set; } = string.Empty;
  public string Vendor { get; set; } = "Generic";
  public string Model { get; set; } = "Generic Server";
  public string SerialNumber { get; set; } = string.Empty;
  public string PartNumber { get; set; } = string.Empty; // Добавили парт-номер
  public string IpAddress { get; set; } = string.Empty;
  public int TotalCpuThreads { get; set; }
  public int TotalRamGb { get; set; }
  public int TotalDiskGb { get; set; }

  // New Hardware/BIOS properties
  public string BiosVersion { get; set; } = string.Empty;
  public string BiosDate { get; set; } = string.Empty;
  public string ManagementIp { get; set; } = string.Empty;
}
