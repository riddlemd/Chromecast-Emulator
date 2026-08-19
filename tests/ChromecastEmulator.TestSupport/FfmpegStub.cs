namespace ChromecastEmulator.TestSupport;

/// A fake ffmpeg for HlsPipeline/RenderController tests, so they never depend on a real
/// ffmpeg build. It writes its own pid and its argv next to the playlist (so tests can
/// verify a kill, and check the flags the pipeline passes), then behaves according to the
/// content id (the argument following `-i`):
///
///   "fast"       writes a playlist with one segment after a short delay
///   "headeronly" writes a playlist header with no segment in it, ever — what real ffmpeg
///                leaves on disk between creating the playlist and finishing segment one
///   anything else never writes a playlist at all
public static class FfmpegStub
{
    public static string Create(string directory)
    {
        var path = Path.Combine(directory, "ffmpeg-stub.sh");
        File.WriteAllText(path, Script);
        // A shell script stub only makes sense where the exec bit does; the tests that use
        // it are Unix-only for the same reason.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return path;
    }

    // POSIX sh only (no bashisms): macOS ships an old /bin/bash, but this runs fine under
    // either. `for last; do :; done` is the portable way to grab the final positional arg.
    private const string Script = """
        #!/bin/sh
        prev=
        mode=
        last=
        for arg in "$@"; do
          if [ "$prev" = "-i" ]; then
            mode="$arg"
          fi
          prev="$arg"
          last="$arg"
        done
        dir=$(dirname "$last")
        echo $$ > "$dir/ffmpeg.pid"
        for arg in "$@"; do
          echo "$arg" >> "$dir/ffmpeg-args.txt"
        done
        if [ "$mode" = "fast" ]; then
          sleep 0.2
          printf '#EXTM3U\n#EXTINF:2.0,\nseg00000.ts\n' > "$last"
        elif [ "$mode" = "headeronly" ]; then
          sleep 0.2
          printf '#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:2\n' > "$last"
        fi
        sleep 30
        """;
}
