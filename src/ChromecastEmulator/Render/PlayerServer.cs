using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Render;

/// Loopback HTTP for the render window: the player page plus the HLS output ffmpeg is
/// writing. Photino's custom scheme handler can serve neither the initial page nor byte
/// ranges, so the webview needs a real origin to load from.
public sealed class PlayerServer : IDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".ts"] = "video/mp2t",
    };

    private readonly string _mediaDirectory;
    private readonly ILogger<PlayerServer> _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();

    public PlayerServer(int port, string mediaDirectory, ILogger<PlayerServer> logger)
    {
        _mediaDirectory = mediaDirectory;
        _logger = logger;
        Port = port > 0 ? port : FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public void Start()
    {
        _listener.Start();
        _logger.LogDebug("render player served from {BaseUrl}", BaseUrl);
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("render listener stopped: {Reason}", ex.Message);
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/" or "/index.html":
                    await WriteAssetAsync(context, "index.html");
                    break;
                case "/player.js":
                    await WriteAssetAsync(context, "player.js");
                    break;
                case "/hls.js":
                    await WriteAssetAsync(context, "hls.light.min.js");
                    break;
                default:
                    if (path.StartsWith("/media/", StringComparison.Ordinal))
                        await WriteMediaAsync(context, path["/media/".Length..]);
                    else
                        context.Response.StatusCode = 404;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("render request failed: {Reason}", ex.Message);
            try { context.Response.StatusCode = 500; } catch { /* client already gone */ }
        }
        finally
        {
            try { context.Response.Close(); } catch { /* client already gone */ }
        }
    }

    private static async Task WriteAssetAsync(HttpListenerContext context, string name)
    {
        await using var asset = OpenAsset(name);
        if (asset is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        context.Response.ContentType = ContentTypeFor(name);
        await asset.CopyToAsync(context.Response.OutputStream);
    }

    private async Task WriteMediaAsync(HttpListenerContext context, string relativePath)
    {
        var file = ResolveMediaFile(_mediaDirectory, relativePath);
        if (file is null || !File.Exists(file))
        {
            context.Response.StatusCode = 404;
            return;
        }

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        context.Response.ContentType = ContentTypeFor(file);
        // The playlist grows while ffmpeg runs; a cached copy strands the player at the
        // segment list it first saw.
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Accept-Ranges"] = "bytes";

        var (offset, length) = ResolveRange(context, stream.Length);
        if (length < 0)
        {
            context.Response.StatusCode = 416;
            return;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        context.Response.ContentLength64 = length;
        await CopyAsync(stream, context.Response.OutputStream, length);
    }

    /// Maps a request path under /media/ to a file, or null if it would escape the media
    /// directory. Segment names come off the wire, so GetFileName drops any traversal and
    /// the prefix check backstops it — the separator matters, or a sibling directory whose
    /// name merely starts with the root's would pass. Exposed because HttpListener
    /// normalises `..` out of the URL before a request can reach this, which leaves no way
    /// to cover the check through the server itself.
    public static string? ResolveMediaFile(string mediaDirectory, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mediaDirectory));
        var file = Path.GetFullPath(Path.Combine(root, Path.GetFileName(relativePath)));
        return file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? file : null;
    }

    /// Honours a single `bytes=` range; AVFoundation asks for them even when hls.js does not.
    private static (long Offset, long Length) ResolveRange(HttpListenerContext context, long size)
    {
        var header = context.Request.Headers["Range"];
        if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return (0, size);

        var span = header["bytes=".Length..].Split(',')[0].Split('-');
        if (span.Length != 2) return (0, size);

        long offset;
        long last;
        if (span[0].Length == 0)
        {
            if (!long.TryParse(span[1], out var fromEnd) || fromEnd <= 0) return (0, size);
            offset = Math.Max(0, size - fromEnd);
            last = size - 1;
        }
        else
        {
            if (!long.TryParse(span[0], out offset) || offset >= size) return (0, -1);
            last = span[1].Length > 0 && long.TryParse(span[1], out var parsed) ? Math.Min(parsed, size - 1) : size - 1;
        }

        if (last < offset) return (0, -1);

        context.Response.StatusCode = 206;
        context.Response.Headers["Content-Range"] = $"bytes {offset}-{last}/{size}";
        return (offset, last - offset + 1);
    }

    private static async Task CopyAsync(Stream source, Stream destination, long length)
    {
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)));
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read));
            length -= read;
        }
    }

    private static Stream? OpenAsset(string name) =>
        typeof(PlayerServer).GetTypeInfo().Assembly
            .GetManifestResourceStream($"ChromecastEmulator.Render.Assets.{name}");

    private static string ContentTypeFor(string path) =>
        ContentTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";

    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Close(); } catch { /* already torn down */ }
        _stopping.Dispose();
    }
}
