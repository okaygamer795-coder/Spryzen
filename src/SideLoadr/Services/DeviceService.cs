using System.Diagnostics;
using System.IO;
using System.Management;

namespace SideLoadr.Services;

/// <summary>Detects connected iOS devices and helper tools.</summary>
public static class DeviceService
{
    /// <summary>Returns connected iOS devices. Prefers UDIDs from libimobiledevice
    /// tools when present, falls back to Apple USB entries from WMI.</summary>
    public static IReadOnlyList<DeviceInfo> GetConnectedDevices()
    {
        var viaTools = TryGetUdidsFromTools();
        if (viaTools.Count > 0)
        {
            return viaTools
                .Select(u => new DeviceInfo(
                    $"iOS device {Shorten(u)}",
                    u,
                    "connected (UDID via libimobiledevice)"))
                .ToArray();
        }

        var devices = new List<DeviceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Status FROM Win32_PnPEntity WHERE " +
                "Manufacturer LIKE '%Apple%' OR Name LIKE '%Apple Mobile%' OR " +
                "Name LIKE '%iPhone%' OR Name LIKE '%iPad%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = mo["Name"]?.ToString() ?? "Apple device";
                string status = mo["Status"]?.ToString() ?? "OK";
                devices.Add(new DeviceInfo(name, null,
                    $"USB visible ({status}) — UDID needs libimobiledevice tools, see README"));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Device query failed: " + ex.Message, ex);
        }
        return devices;
    }

    public static string DriverStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT State FROM Win32_Service WHERE Name = 'Apple Mobile Device Service'");
            foreach (ManagementObject mo in searcher.Get())
                return $"Apple Mobile Device Service: {mo["State"]}";
            return "Apple drivers NOT found — install Apple Devices (Microsoft Store) or iTunes first.";
        }
        catch (Exception ex)
        {
            return "Driver check failed: " + ex.Message;
        }
    }

    /// <summary>Locates a helper exe in SideLoadr/tools or on PATH.</summary>
    public static string? FindTool(string fileName)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "tools", fileName);
        if (File.Exists(local))
            return local;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // ignore malformed PATH entries
            }
        }
        return null;
    }

    /// <summary>UDIDs of devices currently visible to libimobiledevice tools (empty if none).</summary>
    public static IReadOnlyList<string> GetUdids() => TryGetUdidsFromTools();

    private static IReadOnlyList<string> TryGetUdidsFromTools()
    {
        string? tool = FindTool("idevice_id.exe");
        if (tool is null)
            return Array.Empty<string>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "-l")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
                return Array.Empty<string>();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(); } catch { /* already exiting */ }
                return Array.Empty<string>();
            }
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length >= 8)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string Shorten(string udid)
        => udid.Length <= 12 ? udid : udid[..8] + "…" + udid[^4..];
}
