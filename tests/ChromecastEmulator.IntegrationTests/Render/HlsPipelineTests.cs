using System.Diagnostics;
using ChromecastEmulator.Render;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Render;

public class HlsPipelineTests
{
    private static HlsPipeline CreatePipeline(TempState temp, out string outputDirectory)
    {
        var stub = FfmpegStub.Create(temp.Directory);
        var options = temp.Options(o => o.FfmpegPath = stub);
        outputDirectory = Path.Combine(temp.Directory, "output");
        return new HlsPipeline(options, outputDirectory, NullLogger<HlsPipeline>.Instance);
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id: either it never existed or it has already exited
            // and been reaped.
            return false;
        }
    }

    [Fact]
    public void Start_ValidFfmpeg_ReturnsUrlWithPlaylistAndGeneration()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out _);

        var url = pipeline.Start("silent");

        Assert.NotNull(url);
        Assert.Contains(HlsPipeline.PlaylistName, url);
        Assert.Contains("g=1", url);
        Assert.Equal(1, pipeline.Generation);
    }

    [Fact]
    public void Start_CalledTwice_GenerationIncrementsAndUrlReflectsIt()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out _);

        pipeline.Start("silent");
        var url = pipeline.Start("silent");

        Assert.Equal(2, pipeline.Generation);
        Assert.NotNull(url);
        Assert.Contains("g=2", url);
    }

    [Fact]
    public async Task Start_SecondCall_KillsFirstProcess()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out var outputDirectory);
        var pidFile = Path.Combine(outputDirectory, "ffmpeg.pid");

        pipeline.Start("silent");
        Assert.True(await Poll.UntilAsync(() => File.Exists(pidFile), TimeSpan.FromSeconds(2)));
        var firstPid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(IsProcessRunning(firstPid));

        pipeline.Start("silent");

        Assert.True(await Poll.UntilAsync(() => !IsProcessRunning(firstPid), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Start_PassesNostdinSoFfmpegCannotStealTheConsole()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out var outputDirectory);
        var argsFile = Path.Combine(outputDirectory, "ffmpeg-args.txt");

        pipeline.Start("silent");

        Assert.True(await Poll.UntilAsync(() => File.Exists(argsFile), TimeSpan.FromSeconds(2)));
        // Without it ffmpeg inherits stdin and swallows the emulator's own console input.
        Assert.Contains("-nostdin", await File.ReadAllLinesAsync(argsFile));
    }

    [Fact]
    public async Task WaitForFirstSegmentAsync_StubWritesExtinf_ReturnsTrue()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out _);
        pipeline.Start("fast");

        var ready = await pipeline.WaitForFirstSegmentAsync(pipeline.Generation, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(ready);
    }

    [Fact]
    public async Task WaitForFirstSegmentAsync_StubWritesNothing_ReturnsFalseOnTimeout()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out _);
        pipeline.Start("silent");

        var ready = await pipeline.WaitForFirstSegmentAsync(pipeline.Generation, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(ready);
    }

    [Fact]
    public async Task WaitForFirstSegmentAsync_PlaylistWrittenButNoSegmentYet_ReturnsFalse()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out _);
        pipeline.Start("headeronly");

        var ready = await pipeline.WaitForFirstSegmentAsync(pipeline.Generation, TimeSpan.FromSeconds(2), CancellationToken.None);

        // The playlist exists the moment ffmpeg starts. Reporting ready on the file alone
        // hands hls.js a manifest with nothing in it, which it treats as a fatal error.
        Assert.False(ready);
    }

    [Fact]
    public async Task WaitForFirstSegmentAsync_StaleGeneration_ReturnsFalseWithoutWaitingForTheNewOne()
    {
        using var temp = new TempState();
        using var pipeline = CreatePipeline(temp, out _);
        pipeline.Start("silent");
        var staleGeneration = pipeline.Generation;
        // The replacement DOES produce a playlist, so a missing generation check would see
        // it and answer true for a load that has already been superseded.
        pipeline.Start("fast");

        var elapsed = Stopwatch.StartNew();
        var ready = await pipeline.WaitForFirstSegmentAsync(staleGeneration, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(ready);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"should short-circuit, took {elapsed.Elapsed}");
    }

    [Fact]
    public void Start_NonexistentFfmpegPath_ReturnsNullInsteadOfThrowing()
    {
        using var temp = new TempState();
        var options = temp.Options(o => o.FfmpegPath = Path.Combine(temp.Directory, "no-such-ffmpeg"));
        using var pipeline = new HlsPipeline(options, Path.Combine(temp.Directory, "output"), NullLogger<HlsPipeline>.Instance);

        var url = pipeline.Start("x");

        Assert.Null(url);
    }

    [Fact]
    public async Task Dispose_RemovesOutputDirectoryAndKillsProcess()
    {
        using var temp = new TempState();
        var pipeline = CreatePipeline(temp, out var outputDirectory);
        var pidFile = Path.Combine(outputDirectory, "ffmpeg.pid");
        pipeline.Start("silent");
        Assert.True(await Poll.UntilAsync(() => File.Exists(pidFile), TimeSpan.FromSeconds(2)));
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(Directory.Exists(outputDirectory));

        pipeline.Dispose();

        Assert.False(Directory.Exists(outputDirectory));
        Assert.True(await Poll.UntilAsync(() => !IsProcessRunning(pid), TimeSpan.FromSeconds(2)));
    }
}
