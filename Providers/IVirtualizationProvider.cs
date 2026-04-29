using NetBoxSync.Models;

namespace NetBoxSync.Providers;

public interface IVirtualizationProvider
{
  Task<List<VmAsset>> GetVirtualMachinesAsync();
  // Добавили сбор физических хостов
  Task<List<HostAsset>> GetHostsAsync();
}
