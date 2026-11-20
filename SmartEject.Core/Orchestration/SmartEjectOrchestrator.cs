using SmartEject.Core.Interfaces;
using SmartEject.Core.Models;
namespace SmartEject.Core.Orchestration;
public sealed class SmartEjectOrchestrator(IDeviceManager devices, IProcessScanner scanner, IDeviceEjector ejector)
{
    public async Task<EjectResult> RunAsync(StorageDevice selected, IProgress<EjectProgress>? progress = null, CancellationToken ct = default)
    {
        void Report(string stage, string message) => progress?.Report(new(stage, message));
        Report("Identify", "Verifying device identity and connected volumes…");
        var current = await devices.DiscoverAsync(ct);
        var match = current.FirstOrDefault(d => string.Equals(d.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
        if (match is null) return Fail("This device is no longer connected. Refresh the device list.");
        if (match.Volumes.Count == 0) return Fail("Windows did not report an accessible volume for this device.");
        Report("Scan", "Checking registered apps and active file handles…");
        // Informational users (e.g. Explorer) release the device when Windows sends the
        // query-remove request, so only actionable blockers stop the eject here.
        var blockers = (await scanner.ScanAsync(match, ct)).Where(b => b.Severity != BlockerSeverity.Informational).ToArray();
        if (blockers.Length > 0) return new(false, "Windows reports applications using the device. Close or safely release them, then retry.", blockers, DateTimeOffset.Now);
        Report("Verify", "Rechecking device presence before removal…");
        current = await devices.DiscoverAsync(ct);
        if (!current.Any(d => string.Equals(d.Id, selected.Id, StringComparison.OrdinalIgnoreCase))) return Fail("The device disconnected before removal could be confirmed.");
        Report("Remove", "Requesting safe removal through Windows…");
        var result = await ejector.EjectAsync(match, ct);
        if (result.Success)
        {
            // CM_Request_Device_Eject succeeding means Windows accepted the request; confirm
            // the same device actually disappears before reporting completion.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                current = await devices.DiscoverAsync(ct);
                if (!current.Any(d => string.Equals(d.Id, selected.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Report("Complete", "Windows confirmed that the device is no longer connected.");
                    return new(true, "Windows confirmed safe removal. You can now disconnect the device.", Array.Empty<ResourceBlocker>(), DateTimeOffset.Now);
                }
            }
            return new(false, "Windows accepted the removal request, but the device is still detected. Safe removal is not confirmed; wait and retry.", Array.Empty<ResourceBlocker>(), DateTimeOffset.Now);
        }
        Report(result.Success ? "Complete" : "Blocked", result.Message);
        return new(false, result.Message, Array.Empty<ResourceBlocker>(), DateTimeOffset.Now);
        EjectResult Fail(string message) => new(false, message, Array.Empty<ResourceBlocker>(), DateTimeOffset.Now);
    }
}
