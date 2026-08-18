using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Templates;

namespace ChromecastEmulator;

public static class CastLogging
{
    private const string ConsoleTemplate = "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}";

    /// Field names here are the --log-file contract; senders' test harnesses parse them.
    private const string JournalTemplate =
        "{ {ts: UtcDateTime(@t), dir: Direction, peer: Peer, source: SourceId, destination: DestinationId, " +
        "namespace: Namespace, short: ShortNamespace, type: MessageType, payload: Payload} }\n";

    public static ILoggerFactory Create(EmulatorOptions options)
    {
        var consoleLevel = options.Quiet ? LogEventLevel.Error
            : options.Verbose ? LogEventLevel.Verbose
            : LogEventLevel.Debug;

        // The pipeline stays at Verbose and each sink filters itself, so --quiet still
        // journals every frame to --log-file.
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(
                outputTemplate: ConsoleTemplate,
                restrictedToMinimumLevel: consoleLevel,
                theme: AnsiConsoleTheme.Code);

        if (options.LogFile is not null)
        {
            configuration.WriteTo.Logger(journal => journal
                .Filter.ByIncludingOnly(e => e.Properties.ContainsKey(CastFrameLog.DirectionProperty))
                .WriteTo.File(new ExpressionTemplate(JournalTemplate), options.LogFile));
        }

        return new SerilogLoggerFactory(configuration.CreateLogger(), dispose: true);
    }
}
