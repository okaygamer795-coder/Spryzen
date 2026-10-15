using Microsoft.Win32;
using SideLoadr.Services;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace SideLoadr;

/// <summary>Colors the "days left" column: red when expiring, orange when close, green otherwise.</summary>
public sealed class DaysLeftToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is int days
            ? days <= 2 ? Brushes.Tomato : days <= 4 ? Brushes.Orange : Brushes.LightGreen
            : Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private IpaInfo? _ipa;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var settings = AppTracker.LoadSettings();
        AppleIdBox.Text = settings.AppleId;
        AnisetteBox.Text = string.IsNullOrWhiteSpace(settings.AnisetteUrl)
            ? AppTracker.DefaultAnisetteUrl
            : settings.AnisetteUrl;
        RefreshTracked();
        _ = RefreshDevicesAsync();
        _ = UpdateService.CheckForUpdatesAsync(msg => Log(msg), CancellationToken.None);
    }

    // ---------- logging ----------

    private void Log(string message, bool isError = false)
    {
        Dispatcher.Invoke(() =>
        {
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            p.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = Brushes.Gray });
            p.Inlines.Add(new Run(message) { Foreground = isError ? Brushes.Tomato : Brushes.LightGray });
            LogBox.Document.Blocks.Add(p);
            while (LogBox.Document.Blocks.Count > 500)
                LogBox.Document.Blocks.Remove(LogBox.Document.Blocks.FirstBlock);
            LogBox.ScrollToEnd();
        });
    }

    private void SetStatus(string status)
        => Dispatcher.Invoke(() => StatusLabel.Text = status);

    // ---------- IPA ----------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "iPhone apps (*.ipa)|*.ipa" };
        if (dlg.ShowDialog() == true)
            SetIpa(dlg.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        string? file = ((string[])e.Data.GetData(DataFormats.FileDrop))
            .FirstOrDefault(f => f.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase));
        if (file is not null)
            SetIpa(file);
    }

    private void SetIpa(string path)
    {
        try
        {
            _ipa = IpaReader.Read(path);
            IpaPathBox.Text = path;
            IpaInfoLabel.Text = $"{_ipa.BundleName} v{_ipa.Version} — {_ipa.BundleId}" +
                (_ipa.MinOs is null ? "" : $" — needs iOS {_ipa.MinOs}+");
            Log($"Loaded IPA: {_ipa.BundleName} ({_ipa.BundleId}).");
        }
        catch (Exception ex)
        {
            _ipa = null;
            IpaPathBox.Text = "";
            IpaInfoLabel.Text = "Could not read IPA.";
            Log("IPA error: " + ex.Message, isError: true);
        }
    }

    // ---------- devices ----------

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        DriverStatusLabel.Text = DeviceService.DriverStatus();
        ToolsStatusLabel.Text =
            $"ideviceinstaller: {(DeviceService.FindTool("ideviceinstaller.exe") is null ? "missing (see README)" : "found")} · " +
            $"zsign: {(DeviceService.FindTool("zsign.exe") is null ? "missing (see README)" : "found")}";
        try
        {
            var devices = await Task.Run(DeviceService.GetConnectedDevices);
            DeviceBox.ItemsSource = devices;
            if (devices.Count > 0)
                DeviceBox.SelectedIndex = 0;
            Log(devices.Count == 0
                ? "No iOS device found. Connect via USB, trust the PC, and press Refresh."
                : $"Found {devices.Count} device(s).");
        }
        catch (Exception ex)
        {
            Log("Device refresh failed: " + ex.Message, isError: true);
        }
    }

    private DeviceInfo? SelectedDevice => DeviceBox.SelectedItem as DeviceInfo;

    // ---------- anisette ----------

    private void DefaultAnisette_Click(object sender, RoutedEventArgs e)
    {
        AnisetteBox.Text = AppTracker.DefaultAnisetteUrl;
        Log("Anisette URL reset to the public default.");
    }

    private async void TestAnisette_Click(object sender, RoutedEventArgs e)
    {
        string url = AnisetteBox.Text.Trim();
        if (url.Length == 0)
        {
            url = AppTracker.DefaultAnisetteUrl;
            AnisetteBox.Text = url;
        }
        await RunGuardedAsync("anisette test", async (log, ct) =>
        {
            var (data, server) = await AppleAuthService.FetchAnisetteAsync(url, ct);
            log($"Anisette OK via {server} ({data.Count} fields: {string.Join(", ", data.Keys.Take(6))}…).");
        });
    }

    // ---------- power tools ----------

    private void IconBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PNG images (*.png)|*.png" };
        if (dlg.ShowDialog() == true)
            IconPathBox.Text = dlg.FileName;
    }

    private void Clone_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA first.", isError: true);
            return;
        }
        try
        {
            var result = IpaCustomizer.CloneIpa(_ipa.FilePath, NewBundleIdBox.Text, NewNameBox.Text);
            foreach (string note in result.Notes)
                Log("Clone: " + note);
            SetIpa(result.Path);
        }
        catch (Exception ex)
        {
            Log("Clone failed: " + ex.Message, isError: true);
        }
    }

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA first.", isError: true);
            return;
        }
        try
        {
            var result = IpaCustomizer.CustomizeIpa(_ipa.FilePath, NewBundleIdBox.Text, NewNameBox.Text,
                string.IsNullOrWhiteSpace(IconPathBox.Text) ? null : IconPathBox.Text);
            foreach (string note in result.Notes)
                Log("Customize: " + note);
            SetIpa(result.Path);
        }
        catch (Exception ex)
        {
            Log("Customize failed: " + ex.Message, isError: true);
        }
    }

    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "iPhone apps (*.ipa)|*.ipa",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true || dlg.FileNames.Length == 0)
            return;
        string[] files = dlg.FileNames;
        await RunGuardedAsync($"batch install ({files.Length} files)", async (log, ct) =>
        {
            await InstallService.InstallManyAsync(files, SelectedDevice?.Udid, log, ct);
            Dispatcher.Invoke(RefreshTracked);
        });
    }

    private async void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        string appleId = AppleIdBox.Text.Trim();
        string password = PwdBox.Password;
        string anisette = string.IsNullOrWhiteSpace(AnisetteBox.Text) ? AppTracker.DefaultAnisetteUrl : AnisetteBox.Text.Trim();
        if (appleId.Length == 0 || password.Length == 0)
        {
            Log("Enter your Apple ID and password first.", isError: true);
            return;
        }
        var expiring = AppTracker.Load().Where(a => a.DaysLeft <= 2 && !string.IsNullOrEmpty(a.IpaPath)).ToList();
        var skipped = AppTracker.Load().Count - expiring.Count;
        if (expiring.Count == 0)
        {
            Log(skipped > 0
                ? "Nothing to refresh (tracked apps were installed before path tracking — reinstall once to enable)."
                : "Nothing expiring within 2 days.");
            return;
        }
        AppTracker.SaveSettings(new AppSettings(appleId, anisette, SelectedDevice?.Udid));
        await RunGuardedAsync($"refresh {expiring.Count} app(s)", async (log, ct) =>
        {
            int ok = 0;
            foreach (var app in expiring)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(app.IpaPath))
                {
                    log($"SKIP {app.BundleName}: original IPA not found at {app.IpaPath}");
                    continue;
                }
                try
                {
                    var req = new SignRequest(app.IpaPath, appleId, password, anisette, app.Udid, Request2faCodeAsync);
                    string signed = await SigningService.SignWithFreeAccountAsync(req, log, ct);
                    await InstallService.InstallAsync(signed, string.IsNullOrEmpty(app.Udid) ? null : app.Udid, log, ct);
                    AppTracker.RecordInstalled(app.BundleId, app.BundleName, app.Udid, app.IpaPath);
                    ok++;
                }
                catch (Exception ex)
                {
                    log($"FAILED {app.BundleName}: {ex.Message}");
                }
            }
            Dispatcher.Invoke(RefreshTracked);
            log($"Refresh-all done: {ok}/{expiring.Count} renewed.");
        });
    }

    private async void AppIds_Click(object sender, RoutedEventArgs e)
    {
        string appleId = AppleIdBox.Text.Trim();
        string password = PwdBox.Password;
        string anisette = string.IsNullOrWhiteSpace(AnisetteBox.Text) ? AppTracker.DefaultAnisetteUrl : AnisetteBox.Text.Trim();
        if (appleId.Length == 0 || password.Length == 0)
        {
            Log("Enter your Apple ID and password first.", isError: true);
            return;
        }
        await RunGuardedAsync("list App IDs", async (log, ct) =>
        {
            using var http = new System.Net.Http.HttpClient();
            var auth = new AppleAuthService(http, anisette);
            AppleSession session = await auth.LoginAsync(appleId, password, Request2faCodeAsync, log, ct);
            var portal = new DeveloperPortalService(http, session);
            var ids = await portal.ListAppIdsAsync(log, ct);
            foreach (var (identifier, name) in ids)
                log($"  {identifier}" + (name.Length > 0 && name != identifier ? $"  ({name})" : ""));
        });
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
        => await RunGuardedAsync("update check", (log, ct) => UpdateService.CheckForUpdatesAsync(log, ct));

    // ---------- actions ----------

    private async void SignInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA first.", isError: true);
            return;
        }
        string appleId = AppleIdBox.Text.Trim();
        string password = PwdBox.Password;
        string anisette = AnisetteBox.Text.Trim();
        if (appleId.Length == 0 || password.Length == 0)
        {
            Log("Enter your Apple ID and password (or app-specific password) first.", isError: true);
            return;
        }
        if (anisette.Length == 0)
        {
            anisette = AppTracker.DefaultAnisetteUrl;
            AnisetteBox.Text = anisette;
        }
        AppTracker.SaveSettings(new AppSettings(appleId, anisette, SelectedDevice?.Udid));

        await RunGuardedAsync("sign + install", async (log, ct) =>
        {
            var req = new SignRequest(_ipa.FilePath, appleId, password, anisette,
                SelectedDevice?.Udid, Request2faCodeAsync);
            string signed = await SigningService.SignWithFreeAccountAsync(req, log, ct);
            await InstallService.InstallAsync(signed, SelectedDevice?.Udid, log, ct);
            AppTracker.RecordInstalled(_ipa.BundleId, _ipa.BundleName, SelectedDevice?.Udid, _ipa.FilePath);
            Dispatcher.Invoke(RefreshTracked);
            log("Done. Free certificates last 7 days — come back to re-sign before expiry.");
        });
    }

    private async void InstallSigned_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA first (must already be signed).", isError: true);
            return;
        }
        await RunGuardedAsync("install", async (log, ct) =>
        {
            await InstallService.InstallAsync(_ipa.FilePath, SelectedDevice?.Udid, log, ct);
            AppTracker.RecordInstalled(_ipa.BundleId, _ipa.BundleName, SelectedDevice?.Udid, _ipa.FilePath);
            Dispatcher.Invoke(RefreshTracked);
        });
    }

    private Task<string> Request2faCodeAsync()
    {
        string code = Dispatcher.Invoke(() => TwoFactorBox.Text.Trim());
        if (code.Length == 0)
            throw new InvalidOperationException(
                "Apple asked for a 2FA code: approve on your trusted device, type the code " +
                "into the 2FA box, and run again.");
        return Task.FromResult(code);
    }

    private async Task RunGuardedAsync(string name, Func<Action<string>, CancellationToken, Task> work)
    {
        SetBusy(true, name + "…");
        _cts = new CancellationTokenSource();
        try
        {
            Log($"Starting {name}...");
            await Task.Run(() => work(msg => Log(msg), _cts.Token));
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.", isError: true);
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.Message}", isError: true);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, "Ready");
        }
    }

    private void SetBusy(bool busy, string status)
    {
        Dispatcher.Invoke(() =>
        {
            SignInstallButton.IsEnabled = !busy;
            InstallSignedButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            TestAnisetteButton.IsEnabled = !busy;
            CloneButton.IsEnabled = !busy;
            CustomizeButton.IsEnabled = !busy;
            BatchButton.IsEnabled = !busy;
            RefreshAllButton.IsEnabled = !busy;
            AppIdsButton.IsEnabled = !busy;
            UpdateButton.IsEnabled = !busy;
            IconBrowseButton.IsEnabled = !busy;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusLabel.Text = status;
        });
    }

    private void RefreshTracked()
    {
        var apps = AppTracker.Load().OrderBy(a => a.ExpiresAt).ToList();
        TrackedList.ItemsSource = apps;
        foreach (var app in apps.Where(a => a.DaysLeft <= 2))
            Log($"Reminder: {app.BundleName} expires in {app.DaysLeft} day(s) — re-sign soon.", isError: true);
    }
}
