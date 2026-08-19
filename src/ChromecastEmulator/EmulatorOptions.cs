using System.Net;
using System.Security.Cryptography;

namespace ChromecastEmulator;

public enum AuthMode
{
    /// Reply with a well-formed AuthResponse signed by our self-signed key.
    Respond,

    /// Reply with AuthError INTERNAL_ERROR.
    Error,

    /// Never reply. Senders that require auth will stall here.
    Ignore,
}

public sealed class EmulatorOptions
{
    public string FriendlyName { get; set; } = "Chromecast Emulator";
    public string ModelName { get; set; } = "Chromecast";
    public string? DeviceId { get; set; }
    public int Port { get; set; } = 8009;
    public IPAddress BindAddress { get; set; } = IPAddress.Any;
    public bool Advertise { get; set; } = true;
    public AuthMode AuthMode { get; set; } = AuthMode.Respond;
    public bool AllowAnyApp { get; set; } = true;
    public List<string> ExtraNamespaces { get; } = [];
    public double DefaultDuration { get; set; }
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public bool EchoCustom { get; set; }
    public bool Verbose { get; set; }
    public bool Quiet { get; set; }
    public bool NoConsole { get; set; }
    public string? LogFile { get; set; }

    public bool Render { get; set; }
    public int RenderWidth { get; set; } = 1280;
    public int RenderHeight { get; set; } = 720;
    public int RenderPort { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfmpegVideoBitrate { get; set; } = "4M";

    /// VideoToolbox is hardware-backed on macOS; everywhere else libx264 is the only
    /// encoder ffmpeg is guaranteed to have been built with.
    public string FfmpegVideoCodec { get; set; } =
        OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";

    public string StateDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chromecast-emulator");

    public static (EmulatorOptions? Options, string? Error) Parse(string[] args)
    {
        var options = new EmulatorOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            string Next(string name)
            {
                if (i + 1 >= args.Length) throw new FormatException($"{name} requires a value");
                return args[++i];
            }

            try
            {
                switch (arg)
                {
                    case "-n" or "--name":
                        options.FriendlyName = Next(arg);
                        break;
                    case "--model":
                        options.ModelName = Next(arg);
                        break;
                    case "--device-id":
                        options.DeviceId = Next(arg).Replace("-", "").ToLowerInvariant();
                        break;
                    case "-p" or "--port":
                        options.Port = int.Parse(Next(arg));
                        // Unvalidated, an out-of-range port crashes TcpListener with a stack
                        // trace and silently truncates through the mDNS ushort cast.
                        if (options.Port is < 1 or > 65535)
                            throw new FormatException("port must be between 1 and 65535");
                        break;
                    case "--bind":
                        options.BindAddress = IPAddress.Parse(Next(arg));
                        break;
                    case "--no-advertise":
                        options.Advertise = false;
                        break;
                    case "--auth":
                        options.AuthMode = Enum.Parse<AuthMode>(Next(arg), ignoreCase: true);
                        break;
                    case "--strict-apps":
                        options.AllowAnyApp = false;
                        break;
                    case "--namespace":
                        options.ExtraNamespaces.Add(Next(arg));
                        break;
                    case "--default-duration":
                        options.DefaultDuration = double.Parse(Next(arg));
                        break;
                    case "--ping-interval":
                        options.PingInterval = TimeSpan.FromSeconds(double.Parse(Next(arg)));
                        break;
                    case "--echo":
                        options.EchoCustom = true;
                        break;
                    case "--render":
                        options.Render = true;
                        break;
                    case "--render-size":
                    {
                        var size = Next(arg).Split('x', 2);
                        if (size.Length != 2) throw new FormatException("size must look like 1280x720");
                        options.RenderWidth = int.Parse(size[0]);
                        options.RenderHeight = int.Parse(size[1]);
                        if (options.RenderWidth < 1 || options.RenderHeight < 1)
                            throw new FormatException("size must be positive");
                        break;
                    }
                    case "--render-port":
                        options.RenderPort = int.Parse(Next(arg));
                        if (options.RenderPort is < 1 or > 65535)
                            throw new FormatException("port must be between 1 and 65535");
                        break;
                    case "--ffmpeg":
                        options.FfmpegPath = Next(arg);
                        break;
                    case "--video-codec":
                        options.FfmpegVideoCodec = Next(arg);
                        break;
                    case "--video-bitrate":
                        options.FfmpegVideoBitrate = Next(arg);
                        break;
                    case "--state-dir":
                        options.StateDirectory = Next(arg);
                        break;
                    case "--log-file":
                        options.LogFile = Next(arg);
                        break;
                    case "-v" or "--verbose":
                        options.Verbose = true;
                        break;
                    case "-q" or "--quiet":
                        options.Quiet = true;
                        break;
                    case "--no-console":
                        options.NoConsole = true;
                        break;
                    case "-h" or "--help":
                        return (null, null);
                    default:
                        return (null, $"unknown argument '{arg}'");
                }
            }
            catch (Exception ex)
            {
                return (null, $"bad value for {arg}: {ex.Message}");
            }
        }

        // DeviceId deliberately stays null here; DeviceIdentity.Load falls back to the
        // persisted id so the emulator keeps its identity across restarts.
        return (options, null);
    }

    public static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public static string HelpText => """
        chromecast-emulator - a fake Chromecast receiver for testing Cast senders

        USAGE
          chromecast-emulator [options]

        DEVICE IDENTITY
          -n, --name <text>          Friendly name shown to senders (default: "Chromecast Emulator")
              --model <text>         Model name in the mDNS md= record (default: "Chromecast")
              --device-id <hex>      32-char device id; persisted in the state dir if omitted
              --state-dir <path>     Where identity + TLS key live (default: ~/.chromecast-emulator)

        NETWORK
          -p, --port <n>             Cast channel port (default: 8009)
              --bind <ip>            Listen address (default: 0.0.0.0)
              --no-advertise         Skip mDNS; senders must connect by IP

        PROTOCOL
              --auth <mode>          respond | error | ignore (default: respond)
              --strict-apps          Only launch known app ids; otherwise LAUNCH_ERROR
              --namespace <urn>      Extra namespace to declare on launched apps (repeatable)
              --default-duration <s> Duration applied to LOAD requests that omit one (default: off)
              --ping-interval <s>    Heartbeat PING interval, 0 disables (default: 5)
              --echo                 Echo custom-namespace messages back to the sender

        RENDER
              --render               Open a window that plays loaded media (needs ffmpeg)
              --render-size <WxH>    Window size (default: 1280x720)
              --render-port <n>      Loopback port for the player page (default: an unused one)
              --ffmpeg <path>        ffmpeg executable (default: ffmpeg)
              --video-codec <name>   ffmpeg encoder (default: h264_videotoolbox on macOS, else libx264)
              --video-bitrate <rate> Target video bitrate (default: 4M)

        OUTPUT
          -v, --verbose              Log full payloads including heartbeats
          -q, --quiet                Errors only
              --log-file <path>      Append every frame as JSON Lines
              --no-console           Disable the interactive command console
          -h, --help                 Show this help

        CONSOLE COMMANDS (type 'help' once running)
          status  conns  launch <appId>  stop <sessionId>  volume <0-1>  mute  unmute
          play  pause  seek <seconds>  stopmedia  send <ns> <json>  quit
        """;
}
