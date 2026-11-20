using System.Collections.ObjectModel;
namespace SmartEject.Core.Models;
public sealed record StorageDevice(string Id, string Name, string Manufacturer, string DeviceType, IReadOnlyList<StorageVolume> Volumes, ulong CapacityBytes)
{
    public string Letters => string.Join(", ", Volumes.Select(v => v.DriveLetter));
}
public sealed record StorageVolume(string DriveLetter, string Label, ulong CapacityBytes, ulong FreeBytes, string DeviceId);
public enum BlockerSeverity { Informational, RequiresUserAction, UnsafeToEject }
public sealed record ResourceBlocker(string Application, int ProcessId, string? FilePath, BlockerSeverity Severity, string Recommendation);
public sealed record EjectResult(bool Success, string Message, IReadOnlyList<ResourceBlocker> Blockers, DateTimeOffset CompletedAt);
public sealed record EjectProgress(string Stage, string Message);
