using System.Text.Json;
using System.Text.Json.Nodes;
using ChromecastEmulator.Proto;
using ChromecastEmulator.TestSupport;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.IntegrationTests;

/// Guards the --log-file contract: JSON Lines, one object per frame, fixed field names.
public sealed class CastLoggingTests : IDisposable
{
    private readonly TempState _state = new();

    public void Dispose() => _state.Dispose();

    private string LogPath => Path.Combine(_state.Directory, "frames.jsonl");

    private static CastMessage Text(string ns, string payload) => CastMessages.Text(ns, payload);

    /// The file sink only flushes on dispose, so every scenario runs inside the factory's lifetime.
    private JsonObject[] Journal(EmulatorOptions options, Action<ILoggerFactory, CastFrameLog> scenario)
    {
        using (var factory = CastLogging.Create(options))
        {
            scenario(factory, new CastFrameLog(factory.CreateLogger<CastFrameLog>(), options));
        }

        return File.ReadAllLines(LogPath)
            .Where(line => line.Length > 0)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToArray();
    }

    private EmulatorOptions Options(bool quiet = true, bool verbose = false) =>
        _state.Options(o =>
        {
            o.LogFile = LogPath;
            o.Quiet = quiet;
            o.Verbose = verbose;
        });

    [Fact]
    public void LogFile_WritesOneJsonObjectPerFrameWithTheDocumentedFields()
    {
        var journal = Journal(Options(), (_, frames) =>
            frames.Inbound("1.2.3.4:5", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS","requestId":1}""")));

        var record = Assert.Single(journal);
        Assert.Equal(
            ["ts", "dir", "peer", "source", "destination", "namespace", "short", "type", "payload"],
            record.Select(p => p.Key).ToArray());

        Assert.Equal("in", (string?)record["dir"]);
        Assert.Equal("1.2.3.4:5", (string?)record["peer"]);
        Assert.Equal("sender-0", (string?)record["source"]);
        Assert.Equal("receiver-0", (string?)record["destination"]);
        Assert.Equal(CastNamespaces.Receiver, (string?)record["namespace"]);
        Assert.Equal("receiver", (string?)record["short"]);
        Assert.Equal("GET_STATUS", (string?)record["type"]);
        Assert.Equal("""{"type":"GET_STATUS","requestId":1}""", (string?)record["payload"]);
    }

    [Fact]
    public void LogFile_TimestampIsUtcIso8601()
    {
        var journal = Journal(Options(), (_, frames) =>
            frames.Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}""")));

        var ts = (string?)Assert.Single(journal)["ts"];
        Assert.NotNull(ts);
        var parsed = DateTimeOffset.Parse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.InRange(parsed, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void LogFile_RecordsDirectionForBothWays()
    {
        var journal = Journal(Options(), (_, frames) =>
        {
            frames.Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));
            frames.Outbound("peer", Text(CastNamespaces.Receiver, """{"type":"RECEIVER_STATUS"}"""));
        });

        Assert.Equal(["in", "out"], journal.Select(r => (string)r["dir"]!).ToArray());
    }

    [Fact]
    public void LogFile_UnderQuiet_StillJournalsEveryFrame()
    {
        // --quiet silences the console only; the journal is the machine-readable record.
        var journal = Journal(Options(quiet: true), (_, frames) =>
        {
            frames.Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));
            frames.Inbound("peer", Text(CastNamespaces.Heartbeat, """{"type":"PING"}"""));
        });

        Assert.Equal(2, journal.Length);
    }

    [Fact]
    public void LogFile_JournalsHeartbeatsEvenWithoutVerbose()
    {
        var journal = Journal(Options(verbose: false), (_, frames) =>
            frames.Inbound("peer", Text(CastNamespaces.Heartbeat, """{"type":"PING"}""")));

        Assert.Equal("PING", (string?)Assert.Single(journal)["type"]);
    }

    [Fact]
    public void LogFile_KeepsFullPayloadsEvenThoughTheConsoleTruncates()
    {
        var payload = $$"""{"type":"LOAD","blob":"{{new string('x', 500)}}"}""";

        var journal = Journal(Options(verbose: false), (_, frames) =>
            frames.Inbound("peer", Text(CastNamespaces.Media, payload)));

        Assert.Equal(payload, (string?)Assert.Single(journal)["payload"]);
    }

    [Fact]
    public void LogFile_BinaryPayloadIsBase64()
    {
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var message = new CastMessage
        {
            ProtocolVersion = CastMessage.Types.ProtocolVersion.Castv210,
            SourceId = "sender-0",
            DestinationId = "receiver-0",
            Namespace = CastNamespaces.DeviceAuth,
            PayloadType = CastMessage.Types.PayloadType.Binary,
            PayloadBinary = ByteString.CopyFrom(bytes),
        };

        var journal = Journal(Options(), (_, frames) => frames.Inbound("peer", message));

        var record = Assert.Single(journal);
        Assert.Equal(Convert.ToBase64String(bytes), (string?)record["payload"]);
        Assert.Equal("BINARY", (string?)record["type"]);
    }

    [Fact]
    public void LogFile_IgnoresNonFrameLogEntries()
    {
        var journal = Journal(Options(), (factory, frames) =>
        {
            factory.CreateLogger("test").LogInformation("cast channel listening on {Port}", 8009);
            factory.CreateLogger("test").LogError("something broke");
            frames.Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));
        });

        Assert.Equal("GET_STATUS", (string?)Assert.Single(journal)["type"]);
    }

    [Fact]
    public void LogFile_AppendsAcrossRestarts()
    {
        Journal(Options(), (_, frames) =>
            frames.Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}""")));

        var journal = Journal(Options(), (_, frames) =>
            frames.Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"LAUNCH"}""")));

        Assert.Equal(["GET_STATUS", "LAUNCH"], journal.Select(r => (string)r["type"]!).ToArray());
    }

    [Fact]
    public void LogFile_EscapesPayloadsSoEachRecordStaysOnOneLine()
    {
        var journal = Journal(Options(), (_, frames) =>
            frames.Inbound("peer", Text(CastNamespaces.Media, "{\"type\":\"LOAD\",\"title\":\"a\nb\\\"c\"}")));

        Assert.Single(journal);
        Assert.Equal("{\"type\":\"LOAD\",\"title\":\"a\nb\\\"c\"}", (string?)journal[0]["payload"]);
    }

    [Fact]
    public void Create_WithoutALogFile_DoesNotWriteAnything()
    {
        using (var factory = CastLogging.Create(_state.Options(o => o.Quiet = true)))
        {
            new CastFrameLog(factory.CreateLogger<CastFrameLog>(), new EmulatorOptions())
                .Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));
        }

        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void Create_ReturnsAFactoryThatBuildsWorkingLoggers()
    {
        using var factory = CastLogging.Create(_state.Options(o => o.Quiet = true));

        var logger = factory.CreateLogger<CastLoggingTests>();

        Assert.True(logger.IsEnabled(LogLevel.Error));
        // The pipeline stays at Verbose so sink-level filtering, not the logger, decides visibility.
        Assert.True(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public void LogFile_ProducesParseableJsonLines()
    {
        var journal = Journal(Options(), (_, frames) =>
        {
            for (var i = 0; i < 20; i++)
                frames.Inbound("peer", Text(CastNamespaces.Media, $$"""{"type":"LOAD","requestId":{{i}}}"""));
        });

        Assert.Equal(20, journal.Length);
        foreach (var record in journal)
            Assert.Equal(JsonValueKind.String, JsonDocument.Parse(record["payload"]!.ToJsonString()).RootElement.ValueKind);
    }
}
