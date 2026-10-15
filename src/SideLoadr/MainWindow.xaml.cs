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
    private WatchFolderService? _watcher;

    private static string DefaultWatchFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SideLoadr", "Auto");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _watcher?.Dispose();
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
        WatchModeBox.ItemsSource = new[] { "Install signed IPAs", "Sign + install (needs Apple ID)" };
        WatchModeBox.SelectedIndex = 0;
        WatchFolderBox.Text = DefaultWatchFolder;
        if (CredentialStore.TryLoad(out string savedId, out string savedPwd))
        {
            AppleIdBox.Text = savedId;
            PwdBox.Password = savedPwd;
            RememberMeBox.IsChecked = true;
            Log("Loaded remembered credentials.");
        }
        _ = UpdateKeepAliveStatusAsync();
        CheckHelperTools();
    }

    /// <summary>Beginner safety net: if the helper tools are missing, offer the guide once.</summary>
    private void CheckHelperTools()
    {
        bool hasInstaller = DeviceService.FindTool("ideviceinstaller.exe") is not null;
        bool hasZsign = DeviceService.FindTool("zsign.exe") is not null;
        if (hasInstaller && hasZsign)
            return;
        Log("Helper tools missing — installs and signing need them (see tools folder / README).", isError: true);
        var answer = MessageBox.Show(
            "SideLoadr needs helper tools to install and sign apps:\n\n" +
            (hasInstaller ? "" : "• ideviceinstaller.exe (installing) — MISSING\n") +
            (hasZsign ? "" : "• zsign.exe (signing) — MISSING\n") +
            "\nOpen the Beginner guide to set them up?",
            "Setup needed", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            new GuideWindow { Owner = this }.Show();
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
            Log("IPA file error: " + ex.Message, isError: true);
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

    // ---------- guide ----------

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        new GuideWindow { Owner = this }.Show();
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
            Log("Pick an IPA file first.", isError: true);
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
            Log("Pick an IPA file first.", isError: true);
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
        if (expiring.Count == 0)
        {
            Log(AppTracker.Load().Any(a => string.IsNullOrEmpty(a.IpaPath))
                ? "Nothing to refresh (some tracked apps were installed before path tracking — reinstall once to enable)."
                : "Nothing expiring within 2 days.");
            return;
        }
        AppTracker.SaveSettings(new AppSettings(appleId, anisette, SelectedDevice?.Udid));
        await RunGuardedAsync($"refresh {expiring.Count} app(s)", async (log, ct) =>
        {
            var (ok, total) = await RefreshAllService.RunAsync(appleId, password, anisette, Request2faCodeAsync, log, ct);
            Dispatcher.Invoke(RefreshTracked);
            log($"Refresh-all done: {ok}/{total} renewed.");
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

    // ---------- pairing ----------

    private async void Pair_Click(object sender, RoutedEventArgs e)
        => await RunGuardedAsync("pair", async (log, ct) =>
        {
            log(await PairingService.RunAsync("pair", SelectedDevice?.Udid, log, ct));
            await RefreshDevicesAsync();
        });

    private async void ValidatePair_Click(object sender, RoutedEventArgs e)
        => await RunGuardedAsync("validate pairing", async (log, ct) =>
            log(await PairingService.RunAsync("validate", SelectedDevice?.Udid, log, ct)));

    private async void Unpair_Click(object sender, RoutedEventArgs e)
        => await RunGuardedAsync("unpair", async (log, ct) =>
        {
            log(await PairingService.RunAsync("unpair", SelectedDevice?.Udid, log, ct));
            await RefreshDevicesAsync();
        });

    // ---------- auto-folder ----------

    private void WatchFolderDefault_Click(object sender, RoutedEventArgs e)
    {
        WatchFolderBox.Text = DefaultWatchFolder;
    }

    private void OpenWatchFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = string.IsNullOrWhiteSpace(WatchFolderBox.Text) ? DefaultWatchFolder : WatchFolderBox.Text.Trim();
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start("explorer.exe", folder);
    }

    private void Watch_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher?.Running == true)
        {
            _watcher.Stop();
            WatchButton.Content = "Start watching";
            Log("Stopped watching.");
            return;
        }
        string folder = string.IsNullOrWhiteSpace(WatchFolderBox.Text) ? DefaultWatchFolder : WatchFolderBox.Text.Trim();
        WatchFolderBox.Text = folder;
        bool sign = WatchModeBox.SelectedIndex == 1;
        string appleId = AppleIdBox.Text.Trim();
        string password = PwdBox.Password;
        string anisette = string.IsNullOrWhiteSpace(AnisetteBox.Text) ? AppTracker.DefaultAnisetteUrl : AnisetteBox.Text.Trim();
        string? udid = SelectedDevice?.Udid;
        if (sign)
        {
            if (appleId.Length == 0 || password.Length == 0)
            {
                if (!CredentialStore.TryLoad(out appleId, out password))
                {
                    Log("Sign+install mode needs your Apple ID (or Remember me).", isError: true);
                    return;
                }
            }
        }
        var watcher = new WatchFolderService();
        watcher.Start(folder, path => ProcessWatchFileAsync(path, sign, appleId, password, anisette, udid), msg => Log(msg));
        _watcher = watcher;
        WatchButton.Content = "Stop watching";
    }

    private async Task ProcessWatchFileAsync(string path, bool sign, string appleId, string password, string anisette, string? udid)
    {
        Log($"Auto-folder picked up {Path.GetFileName(path)}.");
        try
        {
            if (!sign)
            {
                await InstallService.InstallAsync(path, udid, msg => Log(msg), CancellationToken.None);
                var info = IpaReader.Read(path);
                AppTracker.RecordInstalled(info.BundleId, info.BundleName, udid, path);
            }
            else
            {
                var req = new SignRequest(path, appleId, password, anisette, udid, () =>
                    Task.FromException<string>(new InvalidOperationException(
                        "Auto-folder hit a 2FA prompt — sign in manually once first.")));
                string signed = await SigningService.SignWithFreeAccountAsync(req, msg => Log(msg), CancellationToken.None);
                await InstallService.InstallAsync(signed, udid, msg => Log(msg), CancellationToken.None);
                var info = IpaReader.Read(path);
                AppTracker.RecordInstalled(info.BundleId, info.BundleName, udid, path);
            }
            Dispatcher.Invoke(RefreshTracked);
        }
        catch (Exception ex)
        {
            Log($"Auto-folder failed for {Path.GetFileName(path)}: {ex.Message}", isError: true);
        }
    }

    // ---------- keep-alive ----------

    private async void KeepAlive_Click(object sender, RoutedEventArgs e)
        => await RunGuardedAsync("schedule auto-refresh", async (log, ct) =>
        {
            await KeepAliveService.CreateDailyTaskAsync(log, ct);
            await UpdateKeepAliveStatusAsync();
        });

    private async void KeepAliveRemove_Click(object sender, RoutedEventArgs e)
        => await RunGuardedAsync("remove auto-refresh", async (log, ct) =>
        {
            await KeepAliveService.DeleteTaskAsync(log, ct);
            await UpdateKeepAliveStatusAsync();
        });

    // ---------- tweak injection ----------

    private void DylibBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Dynamic libraries (*.dylib)|*.dylib" };
        if (dlg.ShowDialog() == true)
            DylibPathBox.Text = dlg.FileName;
    }

    private void Inject_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA file first.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(DylibPathBox.Text))
        {
            Log("Pick a .dylib tweak first.", isError: true);
            return;
        }
        try
        {
            var result = IpaCustomizer.InjectTweak(_ipa.FilePath, DylibPathBox.Text.Trim());
            foreach (string note in result.Notes)
                Log("Inject: " + note);
            SetIpa(result.Path);
        }
        catch (Exception ex)
        {
            Log("Inject failed: " + ex.Message, isError: true);
        }
    }

    // ---------- god mode ----------

    private void Supercharge_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA file first.", isError: true);
            return;
        }
        try
        {
            var opts = new IpaCustomizer.SuperchargeOptions(
                string.IsNullOrWhiteSpace(NewBundleIdBox.Text) ? null : NewBundleIdBox.Text.Trim(),
                string.IsNullOrWhiteSpace(NewNameBox.Text) ? null : NewNameBox.Text.Trim(),
                string.IsNullOrWhiteSpace(IconPathBox.Text) ? null : IconPathBox.Text.Trim(),
                string.IsNullOrWhiteSpace(DylibPathBox.Text) ? null : DylibPathBox.Text.Trim(),
                RelaxCompatibility: CompatCheck.IsChecked == true,
                EnableFileSharing: SharingCheck.IsChecked == true,
                MinOs: string.IsNullOrWhiteSpace(MinOsBox.Text) ? null : MinOsBox.Text.Trim());
            var result = IpaCustomizer.Supercharge(_ipa.FilePath, opts);
            foreach (string note in result.Notes)
                Log("Makeover: " + note);
            SetIpa(result.Path);
        }
        catch (Exception ex)
        {
            Log("Makeover failed: " + ex.Message, isError: true);
        }
    }

    private async Task UpdateKeepAliveStatusAsync()    {
        try
        {
            string status = await KeepAliveService.QueryStatusAsync(CancellationToken.None);
            Dispatcher.Invoke(() => KeepAliveStatusLabel.Text = status);
        }
        catch
        {
            Dispatcher.Invoke(() => KeepAliveStatusLabel.Text = "Auto-refresh: unknown");
        }
    }

    // ---------- actions ----------

    private async void SignInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_ipa is null)
        {
            Log("Pick an IPA file first.", isError: true);
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
        try
        {
            if (RememberMeBox.IsChecked == true)
            {
                CredentialStore.Save(appleId, password);
                Log("Credentials remembered on this PC.");
            }
            else
            {
                CredentialStore.Delete();
            }
        }
        catch (Exception ex)
        {
            Log("Remember-me failed: " + ex.Message, isError: true);
        }

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
            Log("Pick an IPA file first (it must already be signed).", isError: true);
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
            PairButton.IsEnabled = !busy;
            ValidatePairButton.IsEnabled = !busy;
            UnpairButton.IsEnabled = !busy;
            WatchButton.IsEnabled = !busy;
            WatchFolderDefaultButton.IsEnabled = !busy;
            OpenWatchFolderButton.IsEnabled = !busy;
            KeepAliveButton.IsEnabled = !busy;
            KeepAliveRemoveButton.IsEnabled = !busy;
            InjectButton.IsEnabled = !busy;
            DylibBrowseButton.IsEnabled = !busy;
            SuperchargeButton.IsEnabled = !busy;
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
