using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FileExplorer.Services;

/// A deliberately small, read-only HTTP/1.1 server (hand-rolled on TcpListener so it needs no
/// admin URL-ACL, unlike HttpListener) that serves ONE folder tree for browsing media from a phone
/// or another PC on the LAN.
///
/// This reverses the "no local HTTP server" stance in [[BrowserIntegrationService]], so it only
/// ships behind every one of these guards:
///  - opt-in: hidden unless SettingsService.EnableWebBrowse, and only running while a session is open;
///  - unguessable token: every request must carry ?k=&lt;128-bit random&gt; or gets 403 - a random
///    page probing the port can't browse anything;
///  - root scoping: every path is resolved and rejected if it escapes the chosen folder;
///  - GET only, no write/delete/upload endpoints;
///  - idle auto-stop, and MainWindow stops it on exit.
/// Bind is 0.0.0.0 (LAN) by design; Windows Firewall prompts on first use. Plain HTTP, no TLS.
public sealed class MediaWebServer
{
    public static MediaWebServer Instance { get; } = new();

    private static readonly int[] PreferredPorts = { 8787, 8181, 8282, 8484, 8888 };
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private const int MaxRequestHeaderBytes = 16 * 1024;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Timer? _idleTimer;
    private DateTime _lastRequestUtc;

    public bool IsRunning { get; private set; }
    public string Root { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public string Token { get; private set; } = string.Empty;

    /// Supplies thumbnail PNG bytes for /thumb. Wired to ThumbnailCacheService.GetPngBytesAsync at
    /// app startup; left injectable (and null-safe) so this class stays free of any WinUI dependency
    /// and can be integration-tested on its own.
    public Func<string, DateTimeOffset, bool, Task<byte[]?>>? ThumbnailProvider { get; set; }

    /// The LAN URL to hand out (falls back to localhost if no LAN address can be resolved).
    public string Url => $"http://{HostForUrl()}:{Port}/?k={Token}";

    public string LocalUrl => $"http://localhost:{Port}/?k={Token}";

    public event EventHandler? StateChanged;

    public void Start(string rootPath)
    {
        lock (_gate)
        {
            Stop_NoLock();

            Root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            var listener = BindListener();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            _lastRequestUtc = DateTime.UtcNow;
            _idleTimer = new Timer(_ => CheckIdle(), null, IdleTimeout, IdleTimeout);
            IsRunning = true;

            _ = AcceptLoopAsync(listener, _cts.Token);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        LoggingService.LogInfo("MediaWebServer", $"Started on {Url} serving {Root}");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsRunning)
            {
                return;
            }

            Stop_NoLock();
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Stop_NoLock()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        _idleTimer?.Dispose();
        _idleTimer = null;
        IsRunning = false;
    }

    private void CheckIdle()
    {
        if (IsRunning && DateTime.UtcNow - _lastRequestUtc > IdleTimeout)
        {
            LoggingService.LogInfo("MediaWebServer", "Idle timeout - stopping.");
            Stop();
        }
    }

    private static TcpListener BindListener()
    {
        foreach (var port in PreferredPorts)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                // port in use - try the next
            }
        }

        var fallback = new TcpListener(IPAddress.Any, 0);
        fallback.Start();
        return fallback;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = HandleConnectionAsync(client, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = 15_000;
            stream.WriteTimeout = 60_000;

            try
            {
                var (method, target, headers) = await ReadRequestHeadAsync(stream, ct);
                _lastRequestUtc = DateTime.UtcNow;

                if (method is null || target is null)
                {
                    return;
                }

                await RouteAsync(stream, method, target, headers, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
                // client went away mid-request - nothing to do
            }
            catch (Exception ex)
            {
                LoggingService.LogWarning("MediaWebServer.HandleConnection", ex);
            }
        }
    }

    private static async Task<(string? Method, string? Target, Dictionary<string, string> Headers)> ReadRequestHeadAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestHeaderBytes];
        var total = 0;
        int headerEnd;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
            {
                return (null, null, new());
            }

            total += read;
            headerEnd = FindHeaderEnd(buffer, total);
            if (headerEnd >= 0)
            {
                break;
            }

            if (total == buffer.Length)
            {
                return (null, null, new()); // header block too large
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return (null, null, new());
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return (requestLine[0], requestLine[1], headers);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private async Task RouteAsync(NetworkStream stream, string method, string target,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(stream, 405, "Method Not Allowed", ct);
            return;
        }

        var (path, query) = SplitTarget(target);

        // Static CSS/JS carry no data and are referenced by plain <link>/<script> tags that can't
        // easily append the token - serve them without the gate. Everything else needs ?k=<token>.
        switch (path)
        {
            case "/assets/app.css":
                await WriteBytesAsync(stream, 200, "text/css; charset=utf-8", Encoding.UTF8.GetBytes(WebAssets.Css), ct, cache: true);
                return;
            case "/assets/app.js":
                await WriteBytesAsync(stream, 200, "application/javascript; charset=utf-8", Encoding.UTF8.GetBytes(WebAssets.Js), ct, cache: true);
                return;
        }

        if (!string.Equals(query.GetValueOrDefault("k"), Token, StringComparison.Ordinal))
        {
            await WriteStatusAsync(stream, 403, "Forbidden", ct);
            return;
        }

        switch (path)
        {
            case "/":
                await WriteRedirectAsync(stream, $"/dir?p=&k={Token}", ct);
                return;
            case "/dir":
                await ServeDirectoryAsync(stream, query.GetValueOrDefault("p", ""), ct);
                return;
            case "/slideshow":
                await ServeSlideshowAsync(stream, query.GetValueOrDefault("p", ""), ct);
                return;
            case "/thumb":
                await ServeThumbAsync(stream, query.GetValueOrDefault("p", ""), ct);
                return;
            case "/file":
                await ServeFileAsync(stream, query.GetValueOrDefault("p", ""), headers, ct);
                return;
            default:
                await WriteStatusAsync(stream, 404, "Not Found", ct);
                return;
        }
    }

    // ----- routes -----

    private async Task ServeDirectoryAsync(NetworkStream stream, string rel, CancellationToken ct)
    {
        if (!TryResolveDirectory(rel, out var dir))
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct);
            return;
        }

        var html = WebAssets.BuildDirectoryPage(Root, dir, rel, Token);
        await WriteBytesAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), ct);
    }

    private async Task ServeSlideshowAsync(NetworkStream stream, string rel, CancellationToken ct)
    {
        if (!TryResolveDirectory(rel, out var dir))
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct);
            return;
        }

        var images = EnumerateImages(dir).ToList();
        var html = WebAssets.BuildSlideshowPage(Root, dir, rel, images, Token);
        await WriteBytesAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), ct);
    }

    private async Task ServeThumbAsync(NetworkStream stream, string rel, CancellationToken ct)
    {
        if (!TryResolvePath(rel, out var full))
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct);
            return;
        }

        var isDir = Directory.Exists(full);
        if (!isDir && !File.Exists(full))
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct);
            return;
        }

        if (ThumbnailProvider is null)
        {
            await WriteStatusAsync(stream, 204, "No Content", ct);
            return;
        }

        try
        {
            var modified = isDir ? Directory.GetLastWriteTimeUtc(full) : File.GetLastWriteTimeUtc(full);
            var png = await ThumbnailProvider(full, modified, isDir);
            if (png is null)
            {
                await WriteStatusAsync(stream, 204, "No Content", ct);
                return;
            }

            await WriteBytesAsync(stream, 200, "image/png", png, ct, cache: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct);
        }
    }

    private async Task ServeFileAsync(NetworkStream stream, string rel, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!TryResolvePath(rel, out var full) || !File.Exists(full))
        {
            await WriteStatusAsync(stream, 404, "Not Found", ct);
            return;
        }

        FileStream file;
        try
        {
            file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteStatusAsync(stream, 403, "Forbidden", ct);
            return;
        }

        await using (file)
        {
            var length = file.Length;
            var mime = MimeFor(Path.GetExtension(full));

            long start = 0;
            var end = length - 1;
            var partial = false;

            if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var span = range["bytes=".Length..].Split('-');
                if (long.TryParse(span[0], out var s))
                {
                    start = s;
                    if (span.Length > 1 && long.TryParse(span[1], out var e) && e >= s)
                    {
                        end = Math.Min(e, length - 1);
                    }

                    partial = start < length;
                }
            }

            if (!partial)
            {
                start = 0;
                end = length - 1;
            }

            var contentLength = end - start + 1;
            var sb = new StringBuilder();
            sb.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            sb.Append($"Content-Type: {mime}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append($"Content-Length: {contentLength}\r\n");
            if (partial)
            {
                sb.Append($"Content-Range: bytes {start}-{end}/{length}\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);

            file.Seek(start, SeekOrigin.Begin);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = contentLength;
                while (remaining > 0)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (read == 0)
                    {
                        break;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // ----- helpers -----

    internal IEnumerable<string> EnumerateImages(string dir)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var f in files.Where(f => IconHelper.IsPreviewableImage(Path.GetExtension(f)))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            yield return f;
        }
    }

    private bool TryResolveDirectory(string rel, out string dir)
    {
        if (TryResolvePath(rel, out var full) && Directory.Exists(full))
        {
            dir = full;
            return true;
        }

        dir = string.Empty;
        return false;
    }

    private bool TryResolvePath(string rel, out string full)
    {
        full = string.Empty;
        try
        {
            var decoded = Uri.UnescapeDataString(rel ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var combined = Path.GetFullPath(Path.Combine(Root, decoded));

            if (!string.Equals(combined, Root, StringComparison.OrdinalIgnoreCase) &&
                !combined.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            full = combined;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return false;
        }
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        var qIndex = target.IndexOf('?');
        if (qIndex < 0)
        {
            return (target, q);
        }

        var path = target[..qIndex];
        foreach (var pair in target[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                q[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                q[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return (path, q);
    }

    private static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".avif" => "image/avif",
        ".svg" => "image/svg+xml",
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".ogg" => "audio/ogg",
        ".pdf" => "application/pdf",
        ".txt" or ".md" or ".log" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static async Task WriteStatusAsync(NetworkStream stream, int code, string reason, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"{code} {reason}");
        await WriteBytesAsync(stream, code, "text/plain; charset=utf-8", body, ct, reason: reason);
    }

    private static async Task WriteRedirectAsync(NetworkStream stream, string location, CancellationToken ct)
    {
        var head = $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int code, string contentType, byte[] body,
        CancellationToken ct, bool cache = false, string reason = "OK")
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {code} {reason}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append(cache ? "Cache-Control: max-age=86400\r\n" : "Cache-Control: no-store\r\n");
        sb.Append("Connection: close\r\n\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, ct);
        }
    }

    private static string HostForUrl()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // no packets sent - just resolves the outbound local IP
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch (SocketException)
        {
            return "localhost";
        }
    }
}
