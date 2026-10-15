using System.Diagnostics;
using System.IO;

namespace SideLoadr.Services;

/// <summary>Device pairing via libimobiledevice (idevicepair): pair new iPhones,
/// validate existing pairings, remove them. Pairing is required before any install.</summary>
public static class PairingService
{
    public static async Task<string> RunAsync(string verb, string? udid, Action<string> log, CancellationToken ct)
    {
        if (verb is not ("pair" or "validate" or "unpair"))
            throw new ArgumentException("Unknown pairing command.", nameof(verb));
        string? tool = DeviceService.FindTool("idevicepair.exe")
            ?? throw new FileNotFoundException(
                "idevicepair.exe not found. Put libimobiledevice Windows binaries " +
                "in the SideLoadr\\tools folder or on PATH (see README).");

        string args = (udid is null ? "" : $"-u {udid} ") + verb;
        log($"Running: idevicepair {args}");
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(tool, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!p.Start())
            throw new InvalidOperationException("Could not start idevicepair.");
        Task<string> stdOut = p.StandardOutput.ReadToEndAsync(ct);
        Task<string> stdErr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        string output = ((await stdOut) + " " + (await stdErr)).Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"idevicepair {verb} failed (exit {p.ExitCode}). {output}".Trim());
        return output.Length > 0 ? output : $"idevicepair {verb}: OK";
    }
}
