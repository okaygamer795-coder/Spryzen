using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SideLoadr.Services;

public sealed record RefreshRequest(string Udid, string BundleId, string Profile, string IpaSha256);
public sealed record HelperResult(bool Ok, string Message);
public sealed record HelperStatus(bool Ok, string Version);

/// <summary>The Spryzen companion endpoint: serves GET /status,
/// POST /refresh (JSON) and POST /install?filename=x (raw .ipa bytes).
/// JSON uses camelCase to match the Swift client. Localhost needs no admin.</summary>
public sealed class HelperServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool Running => _listener?.IsListening == true;

    public void Start(int port,
        Func<RefreshRequest, Task<HelperResult>> onRefresh,
        Func<string, byte[], Task<HelperResult>> onInstall,
        Action<string> log)
    {
        Stop();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            log($"Helper server listening on 127.0.0.1:{port} (Spryzen: use this PC's LAN IP).");
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log("Helper server error: " + ex.Message);
                    break;
                }
                _ = HandleAsync(ctx, onRefresh, onInstall, log);
            }
        }, ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already stopping */ }
        try { _listener?.Stop(); } catch { /* already stopped */ }
        try { _listener?.Close(); } catch { /* already closed */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private static async Task HandleAsync(HttpListenerContext ctx,
        Func<RefreshRequest, Task<HelperResult>> onRefresh,
        Func<string, byte[], Task<HelperResult>> onInstall,
        Action<string> log)
    {
        string path = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant() ?? "/";
        try
        {
            if (ctx.Request.HttpMethod == "GET" && path is "/status" or "/")
            {
                await WriteJsonAsync(ctx, 200, new HelperStatus(true, AppVersion()));
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/refresh")
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                RefreshRequest? req;
                try
                {
                    req = JsonSerializer.Deserialize<RefreshRequest>(body, Json);
                }
                catch (JsonException)
                {
                    req = null;
                }
                if (req is null || string.IsNullOrWhiteSpace(req.BundleId))
                {
                    await WriteJsonAsync(ctx, 400, new HelperResult(false, "Bad /refresh JSON: need udid, bundleID."));
                    return;
                }
                log($"Helper: refresh {req.BundleId}");
                HelperResult result = await onRefresh(req);
                await WriteJsonAsync(ctx, 200, result);
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path == "/install")
            {
                string fileName = ctx.Request.QueryString["filename"] ?? "upload.ipa";
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !fileName.EndsWith(".ipa", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(ctx, 400, new HelperResult(false, "Need ?filename=name.ipa"));
                    return;
                }
                using var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                if (ms.Length == 0 || ms.Length > 500L * 1024 * 1024)
                {
                    await WriteJsonAsync(ctx, 400, new HelperResult(false, "Empty or oversized upload (max 500 MB)."));
                    return;
                }
                log($"Helper: install {fileName} ({ms.Length / 1024} KB)");
                HelperResult result = await onInstall(fileName, ms.ToArray());
                await WriteJsonAsync(ctx, 200, result);
                return;
            }
            await WriteJsonAsync(ctx, 404, new HelperResult(false, "Unknown endpoint. See README for the helper protocol."));
        }
        catch (Exception ex)
        {
            log("Helper request failed: " + ex.Message);
            try { await WriteJsonAsync(ctx, 500, new HelperResult(false, ex.Message)); }
            catch { /* client already gone */ }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static string AppVersion()
        => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
