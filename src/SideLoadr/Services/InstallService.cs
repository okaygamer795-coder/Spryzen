using System.Diagnostics;
using System.IO;
namespace SideLoadr.Services;

/// <summary>Installs an (already signed) IPA onto a connected device via
/// libimobiledevice's ideviceinstaller.</summary>
public static class InstallService
{
    public static async Task InstallAsync(string ipaPath, string? udid, Action<string> log, CancellationToken ct)
    {
        string? tool = DeviceService.FindTool("ideviceinstaller.exe")
            ?? throw new FileNotFoundException(
                "ideviceinstaller.exe not found. Put libimobiledevice Windows binaries " +
                "in the SideLoadr\\tools folder or on PATH (see README).");

        string args = (udid is null ? "" : $"-u {udid} ") + $"-i \"{ipaPath}\"";
        log($"Running: ideviceinstaller {args}");

        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(tool, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!p.Start())
            throw new InvalidOperationException("Could not start ideviceinstaller.");

        Task<string> stdOut = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stdErr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        string output = (await stdOut).Trim();
        if (output.Length > 0)
            log(output);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Install failed (exit {p.ExitCode}). {(await stdErr).Trim()}".Trim());
        log("Install finished.");
    }

    public sealed record BatchResult(string IpaPath, string BundleId, bool Ok, string? Error);

    /// <summary>Installs several already-signed IPAs in sequence, recording each
    /// success in the tracker. One failure doesn't stop the rest.</summary>
    public static async Task<List<BatchResult>> InstallManyAsync(
        IEnumerable<string> ipaPaths, string? udid, Action<string> log, CancellationToken ct)
    {
        var results = new List<BatchResult>();
        foreach (string path in ipaPaths)
        {
            ct.ThrowIfCancellationRequested();
            IpaInfo info;
            try
            {
                info = IpaReader.Read(path);
            }
            catch (Exception ex)
            {
                results.Add(new BatchResult(path, "?", false, "unreadable IPA: " + ex.Message));
                log($"SKIP {Path.GetFileName(path)}: not a valid IPA.");
                continue;
            }
            try
            {
                await InstallAsync(path, udid, log, ct);
                AppTracker.RecordInstalled(info.BundleId, info.BundleName, udid, path);
                results.Add(new BatchResult(path, info.BundleId, true, null));
            }
            catch (Exception ex)
            {
                results.Add(new BatchResult(path, info.BundleId, false, ex.Message));
                log($"FAILED {info.BundleName}: {ex.Message}");
            }
        }
        int ok = results.Count(r => r.Ok);
        log($"Batch done: {ok}/{results.Count} installed.");
        return results;
    }
}
