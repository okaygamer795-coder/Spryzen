using System.IO;
using SideLoadr.Services;

namespace SideLoadr;

/// <summary>Headless entry point: SideLoadr.exe --refresh-all / --install.
/// Used by the scheduled keep-alive task and power users. Exit codes:
/// 0 = ok (or nothing to do), 1 = some items failed, 2 = usage/auth error.</summary>
public sealed record CliOptions(string Mode, string? IpaPath, string? Udid, string? AppleId, string? Password, string? Anisette);

public sealed class UsageException(string message) : Exception(message);

public static class CliParser
{
    public static bool IsCli(string[] args)
        => args.Any(a => a.StartsWith("--", StringComparison.Ordinal));

    public static CliOptions Parse(string[] args)
    {
        string? mode = null;
        string? ipa = null;
        string? udid = null;
        string? appleId = null;
        string? password = null;
        string? anisette = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help": return new CliOptions("help", null, null, null, null, null);
                case "--refresh-all": mode = "refresh-all"; break;
                case "--install":
                    mode = "install";
                    ipa = TakeValue(args, ref i, "--install");
                    break;
                case "--udid": udid = TakeValue(args, ref i, "--udid"); break;
                case "--apple-id": appleId = TakeValue(args, ref i, "--apple-id"); break;
                case "--password": password = TakeValue(args, ref i, "--password"); break;
                case "--anisette": anisette = TakeValue(args, ref i, "--anisette"); break;
                default: throw new UsageException($"Unknown argument '{args[i]}'.\n{Usage}");
            }
        }
        return mode switch
        {
            "refresh-all" => new CliOptions(mode, null, udid, appleId, password, anisette),
            "install" => string.IsNullOrEmpty(ipa)
                ? throw new UsageException("--install needs a file.\n" + Usage)
                : new CliOptions(mode, ipa, udid, appleId, password, anisette),
            _ => throw new UsageException("Nothing to do.\n" + Usage),
        };
    }

    public const string Usage =
        "Usage:\n" +
        "  SideLoadr.exe --refresh-all [--udid U] [--anisette URL] [--apple-id X --password Y]\n" +
        "  SideLoadr.exe --install <file.ipa> [--udid U]\n" +
        "  SideLoadr.exe --help\n" +
        "Credentials come from 'Remember me' (Credential Manager) unless --apple-id/--password are given.";

    private static string TakeValue(string[] args, ref int i, string flag)
        => i + 1 < args.Length ? args[++i] : throw new UsageException($"{flag} needs a value.\n{Usage}");
}

public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args, Action<string> log, CancellationToken ct)
    {
        CliOptions opts;
        try
        {
            opts = CliParser.Parse(args);
        }
        catch (UsageException ex)
        {
            log(ex.Message);
            return 2;
        }
        if (opts.Mode == "help")
        {
            log(CliParser.Usage);
            return 0;
        }
        if (opts.Mode == "install" && opts.IpaPath is not null)
        {
            try
            {
                await InstallService.InstallAsync(opts.IpaPath, opts.Udid, log, ct);
                var info = IpaReader.Read(opts.IpaPath);
                AppTracker.RecordInstalled(info.BundleId, info.BundleName, opts.Udid, opts.IpaPath);
                return 0;
            }
            catch (Exception ex)
            {
                log("FAILED: " + ex.Message);
                return 1;
            }
        }
        // refresh-all
        if (!ResolveCreds(opts, out string appleId, out string password, log))
            return 2;
        string anisette = !string.IsNullOrWhiteSpace(opts.Anisette)
            ? opts.Anisette.Trim()
            : AppTracker.LoadSettings().AnisetteUrl;
        if (string.IsNullOrWhiteSpace(anisette))
            anisette = AppTracker.DefaultAnisetteUrl;
        try
        {
            var (ok, total) = await RefreshAllService.RunAsync(appleId, password, anisette,
                () => Task.FromException<string>(new InvalidOperationException(
                    "Apple asked for a 2FA code, which can't be entered headless. Re-run with fresh login.")),
                log, ct);
            log(total == 0 ? "Nothing expiring." : $"Refresh-all done: {ok}/{total} renewed.");
            return ok == total ? 0 : 1;
        }
        catch (Exception ex)
        {
            log("FAILED: " + ex.Message);
            return 1;
        }
    }

    private static bool ResolveCreds(CliOptions opts, out string appleId, out string password, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(opts.AppleId) && !string.IsNullOrWhiteSpace(opts.Password))
        {
            appleId = opts.AppleId;
            password = opts.Password;
            log("Using credentials from command line (visible in shell history — prefer Remember me).");
            return true;
        }
        if (CredentialStore.TryLoad(out appleId, out password))
        {
            log($"Using saved credentials for {appleId}.");
            return true;
        }
        appleId = "";
        password = "";
        log("No credentials: turn on 'Remember me' in the app once, or pass --apple-id/--password.");
        return false;
    }
}
