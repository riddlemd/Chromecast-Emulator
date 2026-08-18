using ChromecastEmulator.Proto;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace ChromecastEmulator.UnitTests;

public sealed class CastFrameLogTests : IDisposable
{
    private readonly ILoggerFactory _factory;
    private readonly CollectingSink _sink;

    public CastFrameLogTests() => (_factory, _sink) = CollectingSink.CreateFactory();

    public void Dispose() => _factory.Dispose();

    private CastFrameLog Log(bool verbose = false) =>
        new(_factory.CreateLogger<CastFrameLog>(), new EmulatorOptions { Verbose = verbose });

    private static CastMessage Text(string ns, string payload) => CastMessages.Text(ns, payload);

    private static CastMessage Binary(string ns, byte[] payload) => CastMessages.Binary(ns, payload);

    private LogEvent Single()
    {
        var single = Assert.Single(_sink.Events);
        return single;
    }

    [Fact]
    public void Inbound_RecordsDirectionIn()
    {
        Log().Inbound("1.2.3.4:5", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));

        Assert.Equal("in", CollectingSink.Text(Single(), "Direction"));
    }

    [Fact]
    public void Outbound_RecordsDirectionOut()
    {
        Log().Outbound("1.2.3.4:5", Text(CastNamespaces.Receiver, """{"type":"RECEIVER_STATUS"}"""));

        Assert.Equal("out", CollectingSink.Text(Single(), "Direction"));
    }

    [Fact]
    public void Frame_NonHeartbeat_LogsAtInformation()
    {
        Log().Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));

        Assert.Equal(LogEventLevel.Information, Single().Level);
    }

    [Fact]
    public void Frame_Heartbeat_LogsBelowTheDefaultConsoleLevel()
    {
        Log().Inbound("peer", Text(CastNamespaces.Heartbeat, """{"type":"PING"}"""));

        // Heartbeats must stay under Debug or the default console view drowns in PINGs.
        Assert.Equal(LogEventLevel.Verbose, Single().Level);
    }

    [Fact]
    public void Frame_CarriesTheRoutingFieldsTheJournalNeeds()
    {
        Log().Inbound("1.2.3.4:5", Text(CastNamespaces.Media, """{"type":"LOAD"}"""));

        var e = Single();
        Assert.Equal("1.2.3.4:5", CollectingSink.Text(e, "Peer"));
        Assert.Equal("sender-0", CollectingSink.Text(e, "SourceId"));
        Assert.Equal("receiver-0", CollectingSink.Text(e, "DestinationId"));
        Assert.Equal(CastNamespaces.Media, CollectingSink.Text(e, "Namespace"));
        Assert.Equal("media", CollectingSink.Text(e, "ShortNamespace"));
        Assert.Equal("LOAD", CollectingSink.Text(e, "MessageType"));
    }

    [Fact]
    public void Frame_LongPayload_KeepsTheFullTextButPreviewsATruncation()
    {
        var payload = $$"""{"type":"LOAD","blob":"{{new string('x', 500)}}"}""";

        Log().Inbound("peer", Text(CastNamespaces.Media, payload));

        var e = Single();
        Assert.Equal(payload, CollectingSink.Text(e, "Payload"));

        var preview = CollectingSink.Text(e, "PayloadPreview")!;
        Assert.EndsWith("...", preview);
        Assert.Equal(183, preview.Length);
        Assert.Equal(payload[..180], preview[..180]);
    }

    [Fact]
    public void Frame_Verbose_DoesNotTruncateThePreview()
    {
        var payload = $$"""{"type":"LOAD","blob":"{{new string('x', 500)}}"}""";

        Log(verbose: true).Inbound("peer", Text(CastNamespaces.Media, payload));

        Assert.Equal(payload, CollectingSink.Text(Single(), "PayloadPreview"));
    }

    [Fact]
    public void Frame_ShortPayload_IsNotTruncated()
    {
        Log().Inbound("peer", Text(CastNamespaces.Media, """{"type":"PLAY"}"""));

        var e = Single();
        Assert.Equal("""{"type":"PLAY"}""", CollectingSink.Text(e, "PayloadPreview"));
        Assert.Equal("""{"type":"PLAY"}""", CollectingSink.Text(e, "Payload"));
    }

    [Fact]
    public void Frame_BinaryPayload_JournalsBase64ButPreviewsAByteCount()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };

        Log().Inbound("peer", Binary(CastNamespaces.DeviceAuth, bytes));

        var e = Single();
        Assert.Equal(Convert.ToBase64String(bytes), CollectingSink.Text(e, "Payload"));
        Assert.Equal("<4 bytes>", CollectingSink.Text(e, "PayloadPreview"));
        Assert.Equal("BINARY", CollectingSink.Text(e, "MessageType"));
    }

    [Fact]
    public void MessageType_FallsBackToResponseType()
    {
        // GET_APP_AVAILABILITY answers carry responseType instead of type.
        Log().Outbound("peer", Text(CastNamespaces.Receiver, """{"responseType":"GET_APP_AVAILABILITY"}"""));

        Assert.Equal("GET_APP_AVAILABILITY", CollectingSink.Text(Single(), "MessageType"));
    }

    [Theory]
    [InlineData("""{"requestId":1}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void MessageType_WithoutATypeKey_IsADash(string payload)
    {
        Log().Inbound("peer", Text(CastNamespaces.Receiver, payload));

        Assert.Equal("-", CollectingSink.Text(Single(), "MessageType"));
    }

    [Fact]
    public void Frame_ConsoleLine_StartsWithTheDirectionArrow()
    {
        Log().Inbound("peer", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));
        Log().Outbound("peer", Text(CastNamespaces.Receiver, """{"type":"RECEIVER_STATUS"}"""));

        var rendered = _sink.Events.Select(ConsoleLine).ToArray();
        Assert.StartsWith("<-", rendered[0]);
        Assert.StartsWith("->", rendered[1]);
    }

    [Fact]
    public void Frame_ConsoleLine_IsColumnAlignedAndUnquoted()
    {
        Log().Inbound("1.2.3.4:5", Text(CastNamespaces.Receiver, """{"type":"GET_STATUS"}"""));

        var line = ConsoleLine(Single());
        Assert.DoesNotContain("\"1.2.3.4:5\"", line);
        Assert.Contains("sender-0     -> receiver-0", line);
        Assert.EndsWith("""{"type":"GET_STATUS"}""", line);
    }

    /// Renders the way the console sink does — {Message:lj}, so strings arrive unquoted.
    private static string ConsoleLine(LogEvent logEvent)
    {
        var writer = new StringWriter();
        new MessageTemplateTextFormatter("{Message:lj}").Format(logEvent, writer);
        return writer.ToString();
    }
}
