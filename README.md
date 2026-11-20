# Smart Eject

Smart Eject is a local Windows desktop utility built with C#, .NET 10, and WPF. It discovers mounted removable disks, checks registered file resource users with Windows Restart Manager, and asks Windows Configuration Manager to safely remove a selected physical device. It makes no cloud or external API calls.

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK to build, .NET 10 Desktop Runtime to run
- Internet is not used by the running application; initial NuGet restore may need package access for System.Management

## Build and run

From this directory, on a Windows machine with the .NET 10 SDK:

    dotnet build SmartEject.slnx
    dotnet run --project SmartEject.App

The solution builds with the .NET 10 SDK with no warnings. Device discovery, the blocker scan, and the eject decision logic have been verified on Windows 11; validate ejection on disposable test media before relying on it for valuable data.

## Safety behavior and limits

- Device discovery joins Win32_DiskDrive to partitions and logical disks, and only presents devices whose media/instance ID indicates removable/USB storage. Devices without mounted drive-letter volumes are not shown.
- Restart Manager reports registered applications using each volume. Windows does not expose an exact open path through this API. A scan returning no registered users does not prove that no kernel driver, raw handle, pending write, or other unregistered user exists.
- The app never terminates processes or closes arbitrary handles. Reported apps must be closed normally by the user after saving work. Explorer windows are not closed automatically.
- Ejection is requested with CM_Request_Device_EjectW; a veto is surfaced. The app reports success only after the physical device disappears from subsequent discovery. If Windows accepts the request but it remains present after five seconds, the result is explicitly unconfirmed.
- The app does not force-lock or dismount volumes. Windows owns the final safety decision. Administrator elevation is not requested by default.
- The current UI includes device refresh, scan/eject flow, progress messages, blocker details, and local JSONL diagnostics at %LocalAppData%\SmartEject\history.jsonl. Settings, history management, tray integration, theme switching, desktop notifications, installer/publishing, and automatic Explorer close are not implemented yet.

## Native APIs

WindowsDeviceManager uses WMI for physical disk and volume relationships. WindowsProcessScanner uses Restart Manager plus a read-only system handle scan. WindowsDeviceEjector uses Configuration Manager. See Documentation/Native-API.md.

## Author

Built by **Joseph Clyde Dano**. Portfolio: [josephclyde.getlaunchph.com](https://josephclyde.getlaunchph.com)
