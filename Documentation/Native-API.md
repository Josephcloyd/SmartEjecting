# Native API notes

## WMI device mapping

WMI disk-to-partition-to-logical-disk associations allow multiple volumes to be grouped under the physical disk instance ID. WMI inventory is best-effort and can lag hot-plug changes. The application refreshes discovery before scanning and before removal; it compares the physical PNP instance ID to reduce the chance of acting on a replacement device.

## Restart Manager

Restart Manager identifies applications/services that registered resources in its session. The scan registers each mounted volume root and merges process results by PID across partitions. It can omit kernel-mode users, raw volume handles, and applications that do not register resources. It does not provide a reliable exact file path for each process, so the UI does not invent one.

## Configuration Manager ejection

The app locates the disk devnode by PNP instance ID and calls CM_Request_Device_EjectW. Windows may return a veto and vetoing device/application information. The API request is not itself proof of physical removal; Smart Eject waits for the matching device ID to disappear from WMI discovery. A timeout is reported as unconfirmed. No process is killed, no foreign handle is closed, and no volume is forcibly dismounted.

## Limitations

The present release is a foundation, not the full feature set in the project brief. The app does not yet monitor plug/unplug events continuously, show exact file paths, release Explorer windows, track pending writes directly, offer settings/history controls, or provide installer artifacts. Disk discovery depends on mounted drive letters and WMI. Validate on disposable test media before relying on it for valuable data.
