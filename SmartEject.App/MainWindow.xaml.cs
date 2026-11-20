using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using SmartEject.Core.Models;
using SmartEject.Core.Orchestration;
using SmartEject.Windows.DeviceManagement;
using SmartEject.Windows.NativeInterop;
using SmartEject.Windows.ProcessManagement;
namespace SmartEject.App;
public partial class MainWindow : Window
{
    private readonly WindowsDeviceManager _devices = new();
    private readonly WindowsProcessScanner _scanner = new();
    private readonly SmartEjectOrchestrator _orchestrator;
    private readonly ObservableCollection<StorageDevice> _items = new();
    private readonly string _historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartEject", "history.jsonl");
    public MainWindow() { InitializeComponent(); _orchestrator = new(_devices, _scanner, new WindowsDeviceEjector()); DeviceList.ItemsSource = _items; Loaded += async (_, _) => await RefreshAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        try { BusyBar.Visibility = Visibility.Visible; StatusText.Text = "Discovering removable storage…"; var found = await _devices.DiscoverAsync(); _items.Clear(); foreach (var d in found) _items.Add(d); StatusText.Text = found.Count == 0 ? "No mounted removable storage devices found." : $"Found {found.Count} removable storage device(s)."; }
        catch (Exception ex) { StatusText.Text = "Device discovery failed. See local diagnostics for details."; Log(new { Event = "DiscoveryFailed", At = DateTimeOffset.Now, Error = ex.ToString() }); }
        finally { BusyBar.Visibility = Visibility.Collapsed; }
    }
    private async void Eject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StorageDevice device) return;
        if (MessageBox.Show($"Scan {device.Name} ({device.Letters}) for applications using it? If blockers are found, Smart Eject can ask you before sending those apps a normal close request. Save your work first.", "Safe eject", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        BusyBar.Visibility = Visibility.Visible; StatusText.Text = "Starting safe eject checks…";
        var progress = new Progress<EjectProgress>(p => StatusText.Text = p.Message);
        try
        {
            var result = await _orchestrator.RunAsync(device, progress);
            var closeNotes = new List<string>();
            if (result.Blockers.Count > 0)
            {
                var list = string.Join(Environment.NewLine, result.Blockers.Select(b => $"• {b.Application} (PID {b.ProcessId})"));
                var answer = MessageBox.Show(
                    $"These applications have files or resources open on {device.Letters}:\n\n{list}\n\nClose requests can affect other files open in those apps. Save your work first. Smart Eject will ask each app to close normally; it will not force-quit anything. Continue?",
                    "Close apps using this drive?", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes)
                {
                    closeNotes = (await new WindowsResourceResolver().RequestGracefulCloseAsync(result.Blockers)).ToList();
                    Log(new { Event = "GracefulCloseRequests", Device = device.Name, At = DateTimeOffset.Now, Actions = closeNotes });
                    result = await _orchestrator.RunAsync(device, progress);
                }
            }
            Log(new { Event = result.Success ? "EjectSucceeded" : "EjectFailed", Device = device.Name, DeviceId = device.Id, At = result.CompletedAt, Result = result.Message, Blockers = result.Blockers });
            if (result.Blockers.Count > 0)
            {
                var list = string.Join(Environment.NewLine, result.Blockers.Select(b => $"• {b.Application} (PID {b.ProcessId}) — {b.Recommendation}"));
                var notes = closeNotes.Count > 0 ? "\n\nClose request results:\n" + string.Join(Environment.NewLine, closeNotes) : "";
                MessageBox.Show($"These applications still appear to be using the device:\n\n{list}{notes}\n\nHandle any save prompts, close them, then retry.", "Device in use", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else MessageBox.Show(result.Message, result.Success ? "Safe removal requested" : "Ejection not confirmed", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            StatusText.Text = result.Message; if (result.Success) await RefreshAsync();
        }
        catch (Exception ex) { StatusText.Text = "The safe removal operation failed."; Log(new { Event = "EjectException", Device = device.Name, At = DateTimeOffset.Now, Error = ex.ToString() }); MessageBox.Show("Smart Eject could not complete the request. Review the device state and retry.", "Safe eject failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { BusyBar.Visibility = Visibility.Collapsed; }
    }
    private void Log(object entry) { try { Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!); File.AppendAllText(_historyPath, JsonSerializer.Serialize(entry) + Environment.NewLine); } catch { } }
}

