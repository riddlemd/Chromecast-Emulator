using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ChromecastEmulator.TestSupport;

/// Captures LogEvents after they have been through the real Serilog pipeline, so
/// tests see the properties the MEL-to-Serilog adapter actually produced.
public sealed class CollectingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<LogEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_gate) _events.Add(logEvent);
    }

    public static (ILoggerFactory Factory, CollectingSink Sink) CreateFactory()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (new SerilogLoggerFactory(logger, dispose: true), sink);
    }

    public static string? Text(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: string text }
            ? text
            : null;

    public static object? Value(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue scalar
            ? scalar.Value
            : null;
}
