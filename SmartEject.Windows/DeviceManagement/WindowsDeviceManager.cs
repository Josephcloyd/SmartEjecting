using System.Management;
using System.IO;
using SmartEject.Core.Interfaces;
using SmartEject.Core.Models;
namespace SmartEject.Windows.DeviceManagement;
public sealed class WindowsDeviceManager : IDeviceManager
{
    public Task<IReadOnlyList<StorageDevice>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<StorageDevice>>(() =>
    {
        var result = new List<StorageDevice>();
        var usbDiskNumbers = new HashSet<int>();
        var protectedDiskNumbers = new HashSet<int>();
        // Win32_DiskDrive may report USB bridge devices as SCSI. MSFT_Disk exposes
        // the actual bus and boot/system flags on current Windows releases.
        try
        {
            var storageScope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            using var storageSearcher = new ManagementObjectSearcher(storageScope,
                new ObjectQuery("SELECT Number, BusType, IsBoot, IsSystem FROM MSFT_Disk"));
            using var storageDisks = storageSearcher.Get();
            foreach (ManagementObject storageDisk in storageDisks)
            {
                var number = Convert.ToInt32(storageDisk["Number"]);
                if (Convert.ToInt32(storageDisk["BusType"]) == 7) usbDiskNumbers.Add(number); // STORAGE_BUS_TYPE: USB
                if (Convert.ToBoolean(storageDisk["IsBoot"]) || Convert.ToBoolean(storageDisk["IsSystem"])) protectedDiskNumbers.Add(number);
            }
        }
        catch (ManagementException) { /* Older Windows/provider: use Win32_DiskDrive media metadata below. */ }
        using var searcher = new ManagementObjectSearcher("SELECT Index, DeviceID, Model, Manufacturer, InterfaceType, MediaType, Size, PNPDeviceID FROM Win32_DiskDrive");
        using var drives = searcher.Get();
        foreach (ManagementObject disk in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = disk["Model"]?.ToString()?.Trim() ?? "Storage device";
            var interfaceType = disk["InterfaceType"]?.ToString() ?? "";
            var pnp = disk["PNPDeviceID"]?.ToString() ?? "";
            var media = disk["MediaType"]?.ToString() ?? "";
            var index = Convert.ToInt32(disk["Index"]);
            var removable = media.Contains("removable", StringComparison.OrdinalIgnoreCase)
                || media.Contains("external", StringComparison.OrdinalIgnoreCase)
                || pnp.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase)
                || pnp.Contains("USB", StringComparison.OrdinalIgnoreCase)
                || (usbDiskNumbers.Contains(index) && !protectedDiskNumbers.Contains(index));
            if (!removable || string.IsNullOrWhiteSpace(pnp) || protectedDiskNumbers.Contains(index)) continue;
            var volumes = new List<StorageVolume>();
            try
            {
                using var assoc = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{Escape(disk["DeviceID"]?.ToString() ?? "")}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                using var partitions = assoc.Get();
                foreach (ManagementObject partition in partitions)
                {
                    using var logicalSearcher = new ManagementObjectSearcher($"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{Escape(partition["DeviceID"]?.ToString() ?? "")}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                    using var logicals = logicalSearcher.Get();
                    foreach (ManagementObject logical in logicals)
                    {
                        var letter = logical["DeviceID"]?.ToString(); if (string.IsNullOrWhiteSpace(letter)) continue;
                        var (total, free) = ReadSpace(letter);
                        volumes.Add(new(letter, logical["VolumeName"]?.ToString() ?? "", total, free, pnp));
                    }
                }
            }
            catch (ManagementException) { }
            if (volumes.Count == 0) continue;
            ulong capacity = 0; try { capacity = Convert.ToUInt64(disk["Size"] ?? 0UL); } catch { }
            result.Add(new(pnp, model, disk["Manufacturer"]?.ToString() ?? "", interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB storage" : "Removable storage", volumes, capacity));
        }
        return result;
    }, cancellationToken);
    // WMI accepts the backslashes in Win32_DiskDrive.DeviceID verbatim. Doubling
    // them here makes the association query miss PHYSICALDRIVE devices.
    // A BitLocker-locked or not-ready volume throws here; that must not hide every other device.
    private static (ulong Total, ulong Free) ReadSpace(string letter)
    {
        try { var di = new DriveInfo(letter + "\\"); return ((ulong)Math.Max(0, di.TotalSize), (ulong)Math.Max(0, di.AvailableFreeSpace)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, 0); }
    }
    private static string Escape(string value) => value.Replace("'", "\\'");
}
