using SmartEject.Core.Models;
namespace SmartEject.Core.Interfaces;
public interface IDeviceManager { Task<IReadOnlyList<StorageDevice>> DiscoverAsync(CancellationToken cancellationToken = default); }
public interface IProcessScanner { Task<IReadOnlyList<ResourceBlocker>> ScanAsync(StorageDevice device, CancellationToken cancellationToken = default); }
public interface IDeviceEjector { Task<(bool Success, string Message)> EjectAsync(StorageDevice device, CancellationToken cancellationToken = default); }
public interface IResourceResolver { Task<IReadOnlyList<string>> RequestGracefulCloseAsync(IReadOnlyList<ResourceBlocker> blockers, CancellationToken cancellationToken = default); }
