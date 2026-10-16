using System.IO;
using System.Windows;
using System.Windows.Media;

namespace SideLoadr;

/// <summary>First-run wizard: ordered readiness checks (drivers, tools, device,
/// signing helper). Read-only — it never changes anything, only reports.</summary>
public partial class SetupWizard : Window
{
    private readonly Action<string> _log;

    public SetupWizard(Action<string> log)
    {
        InitializeComponent();
        _log = log;
        ShowOnStartupBox.IsChecked = !Services.AppTracker.IsSetupDone();
    }

    private static void SetDot(System.Windows.Controls.TextBlock dot, bool? ok)
        => dot.Foreground = ok is null ? Brushes.Gray : ok.Value ? Brushes.LightGreen : Brushes.Tomato;

    private void Step1_Click(object sender, RoutedEventArgs e)
    {
        string status = Services.DeviceService.DriverStatus();
        Step1Text.Text = status;
        SetDot(Step1Dot, status.Contains("Running", StringComparison.OrdinalIgnoreCase));
        _log("Setup check — drivers: " + status);
    }

    private void Step2_Click(object sender, RoutedEventArgs e)
    {
        bool hasInstaller = Services.DeviceService.FindTool("ideviceinstaller.exe") is not null;
        bool hasZsign = Services.DeviceService.FindTool("zsign.exe") is not null;
        Step2Text.Text = $"ideviceinstaller: {(hasInstaller ? "found" : "MISSING")} · zsign: {(hasZsign ? "found" : "MISSING")}";
        SetDot(Step2Dot, hasInstaller && hasZsign);
        _log("Setup check — tools: " + Step2Text.Text);
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "tools");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private async void Step3_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var devices = await Task.Run(Services.DeviceService.GetConnectedDevices);
            Step3Text.Text = devices.Count == 0
                ? "No iPhone found — check cable, Trust dialog, drivers."
                : $"{devices.Count} device(s): {string.Join(", ", devices.Select(d => d.Name))}";
            SetDot(Step3Dot, devices.Count > 0);
            _log("Setup check — devices: " + Step3Text.Text);
        }
        catch (Exception ex)
        {
            Step3Text.Text = "Check failed: " + ex.Message;
            SetDot(Step3Dot, false);
        }
    }

    private async void Step4_Click(object sender, RoutedEventArgs e)
    {
        string url = Services.AppTracker.LoadSettings().AnisetteUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = Services.AppTracker.DefaultAnisetteUrl;
        try
        {
            var (data, server) = await Services.AppleAuthService.FetchAnisetteAsync(url, CancellationToken.None);
            Step4Text.Text = $"OK via {server} ({data.Count} fields).";
            SetDot(Step4Dot, true);
            _log("Setup check — anisette: " + Step4Text.Text);
        }
        catch (Exception ex)
        {
            Step4Text.Text = "Failed: " + ex.Message;
            SetDot(Step4Dot, false);
            _log("Setup check — anisette failed: " + ex.Message);
        }
    }

    private async void RunAll_Click(object sender, RoutedEventArgs e)
    {
        Step1_Click(sender, e);
        Step2_Click(sender, e);
        Step3_Click(sender, e);
        await Task.Delay(100);
        Step4_Click(sender, e);
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        new GuideWindow { Owner = this }.Show();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Services.AppTracker.MarkSetupDone(!(ShowOnStartupBox.IsChecked == true));
        Close();
    }
}
