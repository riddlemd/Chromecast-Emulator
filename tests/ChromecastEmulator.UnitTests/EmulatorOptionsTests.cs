using System.Net;

namespace ChromecastEmulator.UnitTests;

public class EmulatorOptionsTests
{
    [Fact]
    public void Parse_NoArguments_ReturnsDefaults()
    {
        var (options, error) = EmulatorOptions.Parse([]);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("Chromecast Emulator", options.FriendlyName);
        Assert.Equal("Chromecast", options.ModelName);
        Assert.Null(options.DeviceId);
        Assert.Equal(8009, options.Port);
        Assert.Equal(IPAddress.Any, options.BindAddress);
        Assert.True(options.Advertise);
        Assert.Equal(AuthMode.Respond, options.AuthMode);
        Assert.True(options.AllowAnyApp);
        Assert.Equal(TimeSpan.FromSeconds(5), options.PingInterval);
        Assert.False(options.Verbose);
        Assert.False(options.Quiet);
        Assert.False(options.NoConsole);
        Assert.False(options.Render);
        Assert.Equal(1280, options.RenderWidth);
        Assert.Equal(720, options.RenderHeight);
        Assert.Equal(0, options.RenderPort);
        Assert.Equal("ffmpeg", options.FfmpegPath);
        Assert.Equal("4M", options.FfmpegVideoBitrate);
        Assert.False(string.IsNullOrWhiteSpace(options.FfmpegVideoCodec));
    }

    [Fact]
    public void Parse_AllFlags_PopulatesEveryOption()
    {
        var (options, error) = EmulatorOptions.Parse([
            "-n", "My Cast",
            "--model", "Chromecast Ultra",
            "--device-id", "AA-BB-CC-DD",
            "-p", "9009",
            "--bind", "127.0.0.1",
            "--no-advertise",
            "--auth", "error",
            "--strict-apps",
            "--namespace", "urn:x-cast:com.example.one",
            "--default-duration", "12.5",
            "--ping-interval", "2.5",
            "--echo",
            "--state-dir", "/tmp/some-state",
            "--log-file", "/tmp/some-log.jsonl",
            "-v",
            "-q",
            "--no-console",
        ]);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("My Cast", options.FriendlyName);
        Assert.Equal("Chromecast Ultra", options.ModelName);
        Assert.Equal("aabbccdd", options.DeviceId);
        Assert.Equal(9009, options.Port);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), options.BindAddress);
        Assert.False(options.Advertise);
        Assert.Equal(AuthMode.Error, options.AuthMode);
        Assert.False(options.AllowAnyApp);
        Assert.Equal(["urn:x-cast:com.example.one"], options.ExtraNamespaces);
        Assert.Equal(12.5, options.DefaultDuration);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.PingInterval);
        Assert.True(options.EchoCustom);
        Assert.Equal("/tmp/some-state", options.StateDirectory);
        Assert.Equal("/tmp/some-log.jsonl", options.LogFile);
        Assert.True(options.Verbose);
        Assert.True(options.Quiet);
        Assert.True(options.NoConsole);
    }

    [Theory]
    [InlineData("--name")]
    [InlineData("--port")]
    public void Parse_LongFlagAliases_ParseSameAsShortForm(string longFlag)
    {
        // AllFlags covers the short form; this is the long-form branch of the same case label.
        var value = longFlag == "--name" ? "Alias Name" : "1234";

        var (options, error) = EmulatorOptions.Parse([longFlag, value]);

        Assert.Null(error);
        Assert.NotNull(options);
        if (longFlag == "--name")
            Assert.Equal("Alias Name", options.FriendlyName);
        else
            Assert.Equal(1234, options.Port);
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("--quiet")]
    public void Parse_VerboseAndQuietLongForms_ParseSameAsShortForm(string longFlag)
    {
        var (options, error) = EmulatorOptions.Parse([longFlag]);

        Assert.Null(error);
        Assert.NotNull(options);
        if (longFlag == "--verbose")
            Assert.True(options.Verbose);
        else
            Assert.True(options.Quiet);
    }

    [Fact]
    public void Parse_DeviceId_StripsDashesAndLowercases()
    {
        var (options, error) = EmulatorOptions.Parse(["--device-id", "AB-CD-12-34"]);

        Assert.Null(error);
        Assert.Equal("abcd1234", options!.DeviceId);
    }

    [Fact]
    public void Parse_NamespaceFlag_RepeatableAndAccumulatesInOrder()
    {
        var (options, error) = EmulatorOptions.Parse([
            "--namespace", "urn:x-cast:com.example.a",
            "--namespace", "urn:x-cast:com.example.b",
            "--namespace", "urn:x-cast:com.example.c",
        ]);

        Assert.Null(error);
        Assert.Equal(
            ["urn:x-cast:com.example.a", "urn:x-cast:com.example.b", "urn:x-cast:com.example.c"],
            options!.ExtraNamespaces);
    }

    [Theory]
    [InlineData("respond", AuthMode.Respond)]
    [InlineData("RESPOND", AuthMode.Respond)]
    [InlineData("error", AuthMode.Error)]
    [InlineData("Error", AuthMode.Error)]
    [InlineData("ignore", AuthMode.Ignore)]
    [InlineData("IGNORE", AuthMode.Ignore)]
    public void Parse_Auth_IsCaseInsensitive(string value, AuthMode expected)
    {
        var (options, error) = EmulatorOptions.Parse(["--auth", value]);

        Assert.Null(error);
        Assert.Equal(expected, options!.AuthMode);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_HelpFlag_ReturnsNullOptionsAndNullError(string flag)
    {
        var (options, error) = EmulatorOptions.Parse([flag]);

        Assert.Null(options);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_UnknownArgument_ReturnsErrorMentioningIt()
    {
        var (options, error) = EmulatorOptions.Parse(["--totally-bogus"]);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("--totally-bogus", error);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--bind")]
    [InlineData("--auth")]
    [InlineData("--namespace")]
    [InlineData("--device-id")]
    public void Parse_FlagRequiringValueIsLast_ReturnsRequiresValueError(string flag)
    {
        var (options, error) = EmulatorOptions.Parse([flag]);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("requires a value", error);
    }

    [Theory]
    [InlineData("--port", "abc")]
    [InlineData("--bind", "notanip")]
    [InlineData("--auth", "nope")]
    public void Parse_BadValue_ReturnsErrorStartingWithBadValueFor(string flag, string value)
    {
        var (options, error) = EmulatorOptions.Parse([flag, value]);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.StartsWith($"bad value for {flag}", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void Parse_PortOutOfRange_ReturnsBadValueError(string port)
    {
        var (options, error) = EmulatorOptions.Parse(["--port", port]);

        Assert.Null(options);
        Assert.StartsWith("bad value for --port", error);
    }

    [Fact]
    public void Parse_RenderFlags_PopulatesRenderOptions()
    {
        var (options, error) = EmulatorOptions.Parse([
            "--render",
            "--render-size", "1920x1080",
            "--render-port", "9010",
            "--ffmpeg", "/usr/local/bin/ffmpeg",
            "--video-codec", "libx265",
            "--video-bitrate", "8M",
        ]);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.True(options.Render);
        Assert.Equal(1920, options.RenderWidth);
        Assert.Equal(1080, options.RenderHeight);
        Assert.Equal(9010, options.RenderPort);
        Assert.Equal("/usr/local/bin/ffmpeg", options.FfmpegPath);
        Assert.Equal("libx265", options.FfmpegVideoCodec);
        Assert.Equal("8M", options.FfmpegVideoBitrate);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1280")]
    [InlineData("0x100")]
    [InlineData("100x0")]
    public void Parse_RenderSizeMalformed_ReturnsBadValueError(string size)
    {
        var (options, error) = EmulatorOptions.Parse(["--render-size", size]);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.StartsWith("bad value for --render-size", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void Parse_RenderPortOutOfRange_ReturnsBadValueError(string port)
    {
        var (options, error) = EmulatorOptions.Parse(["--render-port", port]);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.StartsWith("bad value for --render-port", error);
    }

    [Fact]
    public void RandomHex_ReturnsTwiceAsManyLowercaseHexCharsAsBytes()
    {
        var hex = EmulatorOptions.RandomHex(8);

        Assert.Equal(16, hex.Length);
        Assert.Matches("^[0-9a-f]+$", hex);
    }

    [Fact]
    public void RandomHex_TwoCalls_DifferBetweenInvocations()
    {
        var first = EmulatorOptions.RandomHex(16);
        var second = EmulatorOptions.RandomHex(16);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void HelpText_IsNonEmptyAndMentionsProgramName()
    {
        Assert.False(string.IsNullOrWhiteSpace(EmulatorOptions.HelpText));
        Assert.Contains("chromecast-emulator", EmulatorOptions.HelpText);
    }
}
