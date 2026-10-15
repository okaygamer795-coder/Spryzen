using System.IO;
using System.Text.RegularExpressions;

namespace SideLoadr.Services;

public enum AutoFolderMode
{
    Off,
    InstallSigned,
    SignAndInstall,
}

/// <summary>Watches a folder and auto-processes dropped .ipa files.
/// Loop-guard: files we generated (*-signed, *-clone, *-custom) are ignored so
/// the watcher can never process its own output.</summary>
public sealed class WatchFolderService : IDisposable
{
    private static readonly string[] OutputMarkers =
        { "-signed", "-clone", "-custom", "-signed2", "-clone2", "-custom2" };

    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public bool Running => _watcher is not null;

    public void Start(string folder, Func<string, Task> onFile, Action<string> log)
    {
        Stop();
        Directory.CreateDirectory(folder);
        _handled.Clear();
        _watcher = new FileSystemWatcher(folder, "*.ipa")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Created += async (_, e) =>
        {
            try
            {
                await Task.Delay(2000); // let the copy finish
                lock (_handled)
                {
                    if (!ShouldProcess(e.FullPath) || !_handled.Add(e.FullPath))
                        return;
                }
                await onFile(e.FullPath);
            }
            catch (Exception ex)
            {
                log("Auto-folder error: " + ex.Message);
            }
        };
        log($"Watching {folder} for new .ipa files.");
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    public static bool ShouldProcess(string path)
        => path.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase)
            && !IsOutputFile(Path.GetFileName(path))
            && File.Exists(path);

    internal static bool IsOutputFile(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return OutputMarkers.Any(m =>
            stem.EndsWith(m, StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(stem, Regex.Escape(m) + @"\d+$", RegexOptions.IgnoreCase));
    }
}
