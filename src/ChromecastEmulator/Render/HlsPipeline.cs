using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Render;

/// Turns whatever a sender put in `contentId` into HLS on disk for the render window.
/// One ffmpeg process at a time; starting a new item kills the previous one.
public sealed class HlsPipeline : IDisposable
{
    public const string PlaylistName = "index.m3u8";

    private readonly EmulatorOptions _options;
    private readonly ILogger<HlsPipeline> _logger;
    private readonly Lock _gate = new();

    private Process? _ffmpeg;
    private bool _disposed;

    public HlsPipeline(EmulatorOptions options, string outputDirectory, ILogger<HlsPipeline> logger)
    {
        _options = options;
        _logger = logger;
        OutputDirectory = outputDirectory;
    }

    public string OutputDirectory { get; }

    /// Bumped per load so the page can bypass the webview's playlist cache.
    public int Generation { get; private set; }

    /// Waits until ffmpeg has written a playlist with at least one segment in it.
    /// Handing the page a URL any earlier just 404s, which hls.js treats as fatal.
    public async Task<bool> WaitForFirstSegmentAsync(int generation, TimeSpan timeout, CancellationToken ct)
    {
        var playlist = Path.Combine(OutputDirectory, PlaylistName);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;

            bool exited;
            lock (_gate)
            {
                // A newer load already replaced this one.
                if (generation != Generation) return false;
                exited = _ffmpeg is null or { HasExited: true };
            }

            if (HasFirstSegment(playlist)) return true;

            // A short clip can finish before the first poll, so the playlist is checked
            // once more above before giving up on an exited process.
            if (exited) return false;

            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool HasFirstSegment(string playlist)
    {
        try
        {
            return File.Exists(playlist)
                && File.ReadAllText(playlist).Contains("#EXTINF", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // ffmpeg is mid-write; the caller tries again.
            return false;
        }
    }

    /// Starts transcoding <paramref name="contentId"/>. Returns the playlist URL path the
    /// page should load, or null if ffmpeg could not be started.
    public string? Start(string contentId)
    {
        lock (_gate)
        {
            if (_disposed) return null;

            StopLocked();
            Generation++;
            ResetOutputDirectory();

            var playlist = Path.Combine(OutputDirectory, PlaylistName);
            var startInfo = new ProcessStartInfo(_options.FfmpegPath)
            {
                RedirectStandardError = true,
                // ffmpeg inherits and consumes stdin otherwise, swallowing the emulator's
                // own console keystrokes.
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (var argument in BuildArguments(contentId, playlist))
                startInfo.ArgumentList.Add(argument);

            try
            {
                _ffmpeg = Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError("ffmpeg failed to start: {Reason}", ex.Message);
                return null;
            }

            if (_ffmpeg is null) return null;

            _ffmpeg.StandardInput.Close();
            DrainStandardError(_ffmpeg, Generation);

            _logger.LogInformation("transcoding {ContentId} to HLS (generation {Generation})", contentId, Generation);
            return $"/media/{PlaylistName}?g={Generation}";
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private IEnumerable<string> BuildArguments(string contentId, string playlist)
    {
        yield return "-nostdin";
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "warning";

        // Senders hand us arbitrary URLs; without this ffmpeg refuses redirects to a
        // different protocol, which most CDN media URLs use.
        yield return "-protocol_whitelist";
        yield return "file,http,https,tcp,tls,crypto,data";

        yield return "-i";
        yield return contentId;

        yield return "-c:v";
        yield return _options.FfmpegVideoCodec;
        yield return "-b:v";
        yield return _options.FfmpegVideoBitrate;
        // Segment boundaries need a keyframe; without a fixed GOP ffmpeg emits ragged
        // segments that seek badly.
        yield return "-g";
        yield return "60";
        yield return "-sc_threshold";
        yield return "0";

        yield return "-c:a";
        yield return "aac";
        yield return "-b:a";
        yield return "128k";
        yield return "-ac";
        yield return "2";

        yield return "-f";
        yield return "hls";
        yield return "-hls_time";
        yield return "2";
        // Keep every segment: the player seeks within what has been produced so far, so
        // discarding old ones would break backwards seeks.
        yield return "-hls_list_size";
        yield return "0";
        yield return "-hls_flags";
        yield return "independent_segments";
        yield return "-hls_playlist_type";
        yield return "event";
        yield return "-hls_segment_filename";
        yield return Path.Combine(OutputDirectory, "seg%05d.ts");
        yield return playlist;
    }

    private void ResetOutputDirectory()
    {
        try
        {
            if (Directory.Exists(OutputDirectory)) Directory.Delete(OutputDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not clear {Directory}: {Reason}", OutputDirectory, ex.Message);
        }

        Directory.CreateDirectory(OutputDirectory);
    }

    /// ffmpeg blocks once the stderr pipe fills, so it has to be read even though the
    /// output is only ever logged.
    private void DrainStandardError(Process process, int generation)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (line.Length > 0) _logger.LogDebug("ffmpeg[{Generation}] {Line}", generation, line);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("ffmpeg stderr reader stopped: {Reason}", ex.Message);
            }
        });
    }

    private void StopLocked()
    {
        var process = _ffmpeg;
        _ffmpeg = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not stop ffmpeg: {Reason}", ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }

        try
        {
            if (Directory.Exists(OutputDirectory)) Directory.Delete(OutputDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not remove {Directory}: {Reason}", OutputDirectory, ex.Message);
        }
    }
}
