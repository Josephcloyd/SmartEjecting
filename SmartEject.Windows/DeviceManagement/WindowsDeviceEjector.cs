using System.Runtime.InteropServices;
using SmartEject.Core.Interfaces;
using SmartEject.Core.Models;
namespace SmartEject.Windows.DeviceManagement;
public sealed class WindowsDeviceEjector : IDeviceEjector
{
    private enum PnpVetoType { Unknown, LegacyDevice, PendingClose, WindowsApp, WindowsService, OutstandingOpen, Device, Driver, IllegalDeviceRequest, InsufficientPower, NonDisableable, LegacyDriver, InsufficientRights, AlreadyRemoved, PowerDisabled, NotDisableable, LegacyProblem, Max }
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parentDevInst, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] private static extern int CM_Get_Device_IDW(uint devInst, System.Text.StringBuilder buffer, int bufferLength, uint flags);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] private static extern int CM_Request_Device_EjectW(uint devInst, out PnpVetoType vetoType, System.Text.StringBuilder vetoName, int nameLength, uint flags);
    public Task<(bool Success, string Message)> EjectAsync(StorageDevice device, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Volumes.Count == 0) return (false, "The device has no confirmed mounted volumes.");
        if (CM_Locate_DevNodeW(out var node, device.Id, 0) != 0) return (false, "Windows could not verify the device identity. Refresh and try again.");
        // USB-attached SCSI/UAS disks can reject eject requests against the disk
        // devnode with CR_ILLEGAL_DEVICE_REQUEST. Ask the USB storage parent instead.
        if (CM_Get_Parent(out var parent, node, 0) == 0) node = parent;
        var veto = new System.Text.StringBuilder(260);
        var status = CM_Request_Device_EjectW(node, out var vetoType, veto, 260, 0);
        if (status == 0) return (true, "Windows accepted the safe removal request.");
        var explanation = vetoType switch
        {
            PnpVetoType.IllegalDeviceRequest => "Windows rejected the removal request for this device. Use the Windows tray eject option or retry after reconnecting the drive.",
            PnpVetoType.OutstandingOpen => "A file or application still has the device open. Close it normally and retry.",
            PnpVetoType.WindowsApp => "A Windows application is preventing removal. Save your work, close it, and retry.",
            PnpVetoType.WindowsService => "A Windows service is using the device. Wait for it to finish, then retry.",
            PnpVetoType.InsufficientRights => "Windows requires additional permission to remove this device.",
            PnpVetoType.NonDisableable or PnpVetoType.NotDisableable => "Windows marked this device as not safe to remove through this request.",
            _ => "Close active files and retry."
        };
        // Veto text is not meaningful for every veto category and may be absent;
        // keep it out of user-facing output instead of showing untrusted raw text.
        return (false, $"Windows did not approve safe removal ({vetoType}). {explanation}");
    }, cancellationToken);
}
