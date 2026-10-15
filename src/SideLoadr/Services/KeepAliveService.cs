using System.Diagnostics;

namespace SideLoadr.Services;

/// <summary>The unique bit: a Windows scheduled task that runs
/// SideLoadr headless (CLI --refresh-all) every day, so free certificates
/// get renewed before the 7-day expiry without opening the app.
/// Needs "remember me" credentials saved first.</summary>
public static class KeepAliveService
{
    public const string TaskName = "SideLoadrRefresh";

    private static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine own exe path.");

    public static async Task CreateDailyTaskAsync(Action<string> log, CancellationToken ct)
    {
        if (!CredentialStore.TryLoad(out _, out _))
            throw new InvalidOperationException(
                "Turn on 'Remember me' and sign in once first — the scheduled run needs saved credentials.");
        string args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --refresh-all\" /SC DAILY /ST 09:00 /F";
        string output = await RunSchtasksAsync(args, log, ct);
        log("Daily auto-refresh scheduled (09:00). " + output.Trim());
    }

    public static async Task DeleteTaskAsync(Action<string> log, CancellationToken ct)
    {
        string output = await RunSchtasksAsync($"/Delete /TN \"{TaskName}\" /F", log, ct);
        log("Scheduled auto-refresh removed. " + output.Trim());
    }

    public static async Task<string> QueryStatusAsync(CancellationToken ct)
    {
        try
        {
            string output = await RunSchtasksAsync($"/Query /TN \"{TaskName}\" /FO LIST /V", _ => { }, ct);
            foreach (string line in output.Split('\n'))
            {
                if (line.TrimStart().StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
                    return "Auto-refresh: " + line.Split(':', 2)[1].Trim();
            }
            return "Auto-refresh: scheduled";
        }
        catch
        {
            return "Auto-refresh: off";
        }
    }

    private static async Task<string> RunSchtasksAsync(string args, Action<string> log, CancellationToken ct)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo("schtasks.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!p.Start())
            throw new InvalidOperationException("Could not start schtasks.exe.");
        Task<string> stdOut = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stdErr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        string output = (await stdOut).Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"schtasks failed (exit {p.ExitCode}). {(await stdErr).Trim()}".Trim());
        return output;
    }
}
