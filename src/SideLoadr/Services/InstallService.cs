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
}
