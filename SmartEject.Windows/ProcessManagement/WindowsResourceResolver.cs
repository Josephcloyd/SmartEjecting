using System.Diagnostics;
using SmartEject.Core.Interfaces;
using SmartEject.Core.Models;
namespace SmartEject.Windows.ProcessManagement;
/// <summary>Sends only normal window-close requests. Never terminates a process.</summary>
public sealed class WindowsResourceResolver : IResourceResolver
{
    private static readonly HashSet<string> NeverRequestClose = new(StringComparer.OrdinalIgnoreCase)
    { "explorer", "winlogon", "csrss", "services", "lsass", "svchost", "smss", "wininit", "System", "Registry", "SmartEject" };

    public async Task<IReadOnlyList<string>> RequestGracefulCloseAsync(IReadOnlyList<ResourceBlocker> blockers, CancellationToken cancellationToken = default)
    {
        var results = new List<string>();
        foreach (var blocker in blockers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(blocker.ProcessId);
                if (process.SessionId == 0 || NeverRequestClose.Contains(process.ProcessName))
                {
                    results.Add($"{blocker.Application} (PID {blocker.ProcessId}): skipped for safety; close or stop it manually.");
                    continue;
                }
                if (process.MainWindowHandle == IntPtr.Zero || !process.CloseMainWindow())
                {
                    results.Add($"{blocker.Application} (PID {blocker.ProcessId}): no normal window-close request was available; close it manually.");
                    continue;
                }
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (!process.HasExited && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(250, cancellationToken);
                    process.Refresh();
                }
                results.Add(process.HasExited
                    ? $"{blocker.Application} (PID {blocker.ProcessId}): closed normally."
                    : $"{blocker.Application} (PID {blocker.ProcessId}): still open; it may need input or a save decision.");
            }
            catch (ArgumentException) { results.Add($"{blocker.Application} (PID {blocker.ProcessId}): already closed."); }
            catch (System.ComponentModel.Win32Exception) { results.Add($"{blocker.Application} (PID {blocker.ProcessId}): Windows denied the close request; close it manually."); }
        }
        return results;
    }
}
