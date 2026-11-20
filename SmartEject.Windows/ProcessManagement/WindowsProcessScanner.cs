using System.Runtime.InteropServices;
using SmartEject.Core.Interfaces;
using SmartEject.Core.Models;
using SmartEject.Windows.ProcessManagement;
namespace SmartEject.Windows.NativeInterop;
internal static class RestartManager
{
    private const int ERROR_MORE_DATA = 234;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct RmUniqueProcess { public int ProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME StartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct RmProcessInfo { public RmUniqueProcess Process; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string AppName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=64)] public string ServiceShortName; public uint ApplicationType; public uint AppStatus; public uint TSSessionId; [MarshalAs(UnmanagedType.Bool)] public bool Restartable; }
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] internal static extern int RmStartSession(out uint session, int flags, string key);
    [DllImport("rstrtmgr.dll", CharSet=CharSet.Unicode)] internal static extern int RmRegisterResources(uint session, uint fileCount, string[]? files, uint appCount, IntPtr apps, uint serviceCount, string[]? services);
    [DllImport("rstrtmgr.dll")] internal static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, ref uint rebootReasons);
    [DllImport("rstrtmgr.dll")] internal static extern int RmEndSession(uint session);
    internal static IReadOnlyList<ResourceBlocker> Scan(string volume, CancellationToken cancellationToken)
    {
        var blockers = new List<ResourceBlocker>(); var key = Guid.NewGuid().ToString("N");
        if (RmStartSession(out var session, 0, key) != 0) return blockers;
        try
        {
            // Register the volume root with Restart Manager. Direct file-handle
            // attribution is provided by NativeFileHandleScanner without a full walk.
            RmRegisterResources(session, 1, new[] { volume }, 0, IntPtr.Zero, 0, null);
            uint needed = 0, count = 0, reasons = 0;
            var status = RmGetList(session, out needed, ref count, null, ref reasons);
            if (status != ERROR_MORE_DATA || needed == 0) return blockers;
            var info = new RmProcessInfo[needed]; count = needed;
            if (RmGetList(session, out _, ref count, info, ref reasons) != 0) return blockers;
            foreach (var item in info.Take((int)count))
            {
                try { using var p = System.Diagnostics.Process.GetProcessById(item.Process.ProcessId); blockers.Add(new(p.ProcessName, item.Process.ProcessId, null, BlockerSeverity.RequiresUserAction, "Close this application after saving work, then retry. Restart Manager does not provide the exact open path.")); }
                catch { blockers.Add(new(item.AppName ?? "Unknown application", item.Process.ProcessId, null, BlockerSeverity.UnsafeToEject, "Windows reports this process as using the device. Do not force close it.")); }
            }
        }
        finally { RmEndSession(session); }
        return blockers;

    }
}
public sealed class WindowsProcessScanner : IProcessScanner
{
    public Task<IReadOnlyList<ResourceBlocker>> ScanAsync(StorageDevice device, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<ResourceBlocker>>(() =>
    {
        var results = new Dictionary<int, ResourceBlocker>();
        foreach (var volume in device.Volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var b in RestartManager.Scan(volume.DriveLetter + "\\", cancellationToken)) results[b.ProcessId] = b;
            // Restart Manager is cooperative; this read-only handle snapshot finds direct
            // file handles from processes that did not register resources with it.
            foreach (var b in NativeFileHandleScanner.FindForVolume(volume.DriveLetter + "\\", cancellationToken)) results[b.ProcessId] = b;
        }
        return results.Values.ToArray();
    }, cancellationToken);
}
