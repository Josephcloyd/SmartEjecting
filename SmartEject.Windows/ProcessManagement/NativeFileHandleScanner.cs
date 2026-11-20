using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SmartEject.Core.Models;
namespace SmartEject.Windows.ProcessManagement;
/// <summary>Read-only fallback for file handles missed by Restart Manager. It never closes source handles.</summary>
internal static class NativeFileHandleScanner
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const uint ProcessDuplicateHandle = 0x0040;
    private const uint DuplicateSameAccess = 0x00000002;
    private const int MaxHandleTableBytes = 256 * 1024 * 1024;
    // NtQueryObject(ObjectNameInformation) can block forever on some synchronous pipe
    // handles. A watchdog abandons a stalled worker and resumes after the stuck handle.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ScanDeadline = TimeSpan.FromSeconds(10);
    private const int MaxAbandonedWorkers = 8;

    // These processes register for PnP query-remove notifications and release their
    // handles when Windows asks, so they must not stop an otherwise safe eject.
    private static readonly HashSet<string> ReleasesOnQueryRemove = new(StringComparer.OrdinalIgnoreCase)
    { "explorer", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "MsMpEng" };

    [StructLayout(LayoutKind.Sequential)] private struct HandleEntry
    {
        public IntPtr Object;
        public UIntPtr ProcessId;
        public UIntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern uint QueryDosDeviceW(string deviceName, System.Text.StringBuilder targetPath, int maxChars);

    /// <summary>Shared between the watchdog and its workers; the table snapshot is copied so abandoned workers stay memory-safe.</summary>
    private sealed class ScanState(HandleEntry[] entries, string prefix)
    {
        public HandleEntry[] Entries { get; } = entries;
        public string Prefix { get; } = prefix;
        public ConcurrentDictionary<ushort, bool> IsFileType { get; } = new();
        public ConcurrentDictionary<int, string> Found { get; } = new();
        public volatile bool Stop;
    }

    /// <summary>Per-worker progress, so a worker that resumes after being abandoned cannot confuse the watchdog.</summary>
    private sealed class WorkerProgress(int start)
    {
        public int Index = start;
        public volatile bool Abandoned;
    }

    internal static IReadOnlyList<ResourceBlocker> FindForVolume(string driveLetter, CancellationToken cancellationToken)
    {
        var deviceName = new System.Text.StringBuilder(1024);
        if (QueryDosDeviceW(driveLetter.TrimEnd('\\'), deviceName, deviceName.Capacity) == 0) return Array.Empty<ResourceBlocker>();
        var prefix = deviceName.ToString().Split('\0')[0];
        try
        {
            var entries = SnapshotHandleTable(cancellationToken);
            if (entries.Length == 0) return Array.Empty<ResourceBlocker>();
            var state = new ScanState(entries, prefix);
            RunWithWatchdog(state, cancellationToken);
            return ToBlockers(state.Found);
        }
        catch (OperationCanceledException) { throw; }
        catch { return Array.Empty<ResourceBlocker>(); }
    }

    private static HandleEntry[] SnapshotHandleTable(CancellationToken cancellationToken)
    {
        var bufferSize = 4 * 1024 * 1024;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = Marshal.AllocHGlobal(bufferSize);
            try
            {
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, table, bufferSize, out var required);
                if (status >= 0)
                {
                    var count = checked((int)Marshal.ReadIntPtr(table).ToInt64());
                    var entrySize = Marshal.SizeOf<HandleEntry>();
                    var offset = IntPtr.Size * 2;
                    var entries = new HandleEntry[count];
                    for (var i = 0; i < count; i++)
                        entries[i] = Marshal.PtrToStructure<HandleEntry>(IntPtr.Add(table, checked(offset + i * entrySize)));
                    return entries;
                }
                if (status != StatusInfoLengthMismatch || bufferSize >= MaxHandleTableBytes) return Array.Empty<HandleEntry>();
                bufferSize = Math.Max(bufferSize * 2, required + 1024);
            }
            finally { Marshal.FreeHGlobal(table); }
        }
    }

    private static void RunWithWatchdog(ScanState state, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var abandoned = 0;
        var start = 0;
        try
        {
            while (start < state.Entries.Length)
            {
                var progress = new WorkerProgress(start);
                var worker = new Thread(() => InspectRange(state, progress)) { IsBackground = true, Name = "SmartEject handle scan" };
                worker.Start();
                var lastIndex = start;
                var lastProgress = Stopwatch.StartNew();
                while (!worker.Join(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (deadline.Elapsed > ScanDeadline) return;
                    var index = Volatile.Read(ref progress.Index);
                    if (index != lastIndex) { lastIndex = index; lastProgress.Restart(); continue; }
                    if (lastProgress.Elapsed < StallTimeout) continue;
                    // The worker is blocked inside a kernel query; leave it parked and skip that handle.
                    progress.Abandoned = true;
                    if (++abandoned > MaxAbandonedWorkers) return;
                    break;
                }
                if (!progress.Abandoned) return; // worker ran to the end
                start = lastIndex + 1;
            }
        }
        finally { state.Stop = true; } // also halts any abandoned worker that later resumes
    }

    private static void InspectRange(ScanState state, WorkerProgress progress)
    {
        // An unhandled exception on a raw thread would terminate the app; a failed pass only loses coverage.
        try { InspectRangeCore(state, progress); }
        catch (Exception) { }
    }

    private static void InspectRangeCore(ScanState state, WorkerProgress progress)
    {
        var from = progress.Index;
        var ownPid = (ulong)Environment.ProcessId;
        var current = GetCurrentProcess();
        var processHandles = new Dictionary<int, IntPtr>();
        try
        {
            for (var i = from; i < state.Entries.Length && !state.Stop && !progress.Abandoned; i++)
            {
                Volatile.Write(ref progress.Index, i);
                var entry = state.Entries[i];
                var pid64 = entry.ProcessId.ToUInt64();
                if (pid64 == 0 || pid64 > int.MaxValue || pid64 == ownPid) continue;
                // Skip non-file object types before paying for DuplicateHandle.
                if (state.IsFileType.TryGetValue(entry.ObjectTypeIndex, out var isFile) && !isFile) continue;
                var pid = (int)pid64;
                if (state.Found.ContainsKey(pid)) continue;
                if (!processHandles.TryGetValue(pid, out var source))
                {
                    source = OpenProcess(ProcessDuplicateHandle, false, pid);
                    processHandles[pid] = source;
                }
                if (source == IntPtr.Zero) continue;
                if (!DuplicateHandle(source, new IntPtr(unchecked((long)entry.HandleValue.ToUInt64())), current, out var duplicate, 0, false, DuplicateSameAccess)) continue;
                try { InspectHandle(state, entry, pid, duplicate); }
                finally { CloseHandle(duplicate); }
            }
        }
        finally { foreach (var handle in processHandles.Values) if (handle != IntPtr.Zero) CloseHandle(handle); }
    }

    private static void InspectHandle(ScanState state, HandleEntry entry, int pid, IntPtr duplicate)
    {
        if (!state.IsFileType.TryGetValue(entry.ObjectTypeIndex, out var isFile))
        {
            var typeName = QueryObjectString(duplicate, ObjectTypeInformation);
            if (string.IsNullOrEmpty(typeName)) return;
            isFile = typeName.Equals("File", StringComparison.Ordinal);
            state.IsFileType[entry.ObjectTypeIndex] = isFile;
        }
        if (!isFile) return;
        var objectPath = QueryObjectString(duplicate, ObjectNameInformation);
        if (!string.IsNullOrEmpty(objectPath) &&
            (objectPath.Equals(state.Prefix, StringComparison.OrdinalIgnoreCase) ||
             objectPath.StartsWith(state.Prefix + "\\", StringComparison.OrdinalIgnoreCase)))
            state.Found.TryAdd(pid, objectPath);
    }

    private static IReadOnlyList<ResourceBlocker> ToBlockers(ConcurrentDictionary<int, string> found)
    {
        var results = new List<ResourceBlocker>();
        foreach (var (pid, path) in found)
        {
            string name;
            try { using var process = Process.GetProcessById(pid); name = process.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { continue; } // exited since the snapshot
            results.Add(ReleasesOnQueryRemove.Contains(name)
                ? new(name, pid, path, BlockerSeverity.Informational,
                    "Windows normally asks this process to release the drive during safe removal.")
                : new(name, pid, path, BlockerSeverity.RequiresUserAction,
                    "This process has a file handle on the selected drive. Close the application normally; Smart Eject will not force it to close."));
        }
        return results;
    }

    private static string QueryObjectString(IntPtr handle, int infoClass)
    {
        var size = 4096;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryObject(handle, infoClass, buffer, size, out var needed);
            if (status < 0 && needed > 0 && needed <= 1024 * 1024)
            {
                Marshal.FreeHGlobal(buffer); size = needed + 64; buffer = Marshal.AllocHGlobal(size);
                status = NtQueryObject(handle, infoClass, buffer, size, out _);
            }
            if (status < 0) return "";
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == IntPtr.Zero || value.Length == 0 ? "" : Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
