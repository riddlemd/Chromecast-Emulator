using ChromecastEmulator.Proto;
using ChromecastEmulator.Protocol;
using ChromecastEmulator.Transport;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace ChromecastEmulator.UnitTests.Protocol;

public class MessageRouterTests
{
    private const string NamespaceA = "urn:x-cast:test.a";
    private const string NamespaceB = "urn:x-cast:test.b";

    private static MessageRouter Router() => new(NullLogger<MessageRouter>.Instance);

    /// DispatchAsync only passes the connection through to the handler, so a null is
    /// safe here and saves standing up a TLS channel.
    private static Task Dispatch(MessageRouter router, string ns, CancellationToken ct = default) =>
        router.DispatchAsync(null!, CastMessages.Text(ns, "{}"), ct);

    private static Task AnyCall(ICastNamespaceHandler handler) =>
        handler.HandleAsync(Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task DispatchAsync_NamespaceRegistered_DispatchesToMatchingHandler()
    {
        var router = Router();
        var handler = Substitute.For<ICastNamespaceHandler>();
        router.Register(NamespaceA, handler);
        var message = CastMessages.Text(NamespaceA, "{}");

        await router.DispatchAsync(null!, message, CancellationToken.None);

        await handler.Received(1).HandleAsync(Arg.Any<CastConnection>(), message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_NamespaceUnregistered_FallsBackToFallbackHandler()
    {
        var router = Router();
        var registered = Substitute.For<ICastNamespaceHandler>();
        var fallback = Substitute.For<ICastNamespaceHandler>();
        router.Register(NamespaceA, registered);
        router.RegisterFallback(fallback);

        await Dispatch(router, NamespaceB);

        await fallback.Received(1).HandleAsync(
            Arg.Any<CastConnection>(), Arg.Is<CastMessage>(m => m.Namespace == NamespaceB), Arg.Any<CancellationToken>());
        await registered.DidNotReceive().HandleAsync(
            Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_SameNamespaceTwice_ReplacesFirstHandler()
    {
        var router = Router();
        var first = Substitute.For<ICastNamespaceHandler>();
        var second = Substitute.For<ICastNamespaceHandler>();
        router.Register(NamespaceA, first);
        router.Register(NamespaceA, second);

        await Dispatch(router, NamespaceA);

        await first.DidNotReceive().HandleAsync(
            Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), Arg.Any<CancellationToken>());
        await second.Received(1).HandleAsync(
            Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_NoMatchAndNoFallback_DropsTheMessage()
    {
        var router = Router();
        var other = Substitute.For<ICastNamespaceHandler>();
        router.Register(NamespaceB, other);

        await Dispatch(router, NamespaceA);

        await other.DidNotReceive().HandleAsync(
            Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrowsGeneralException_IsSwallowed()
    {
        var router = Router();
        var handler = Substitute.For<ICastNamespaceHandler>();
        AnyCall(handler).Throws(new InvalidOperationException("boom"));
        router.Register(NamespaceA, handler);

        await Dispatch(router, NamespaceA);

        await handler.Received(1).HandleAsync(
            Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrowsOperationCanceledException_ReThrows()
    {
        var router = Router();
        var handler = Substitute.For<ICastNamespaceHandler>();
        AnyCall(handler).Throws(new OperationCanceledException());
        router.Register(NamespaceA, handler);

        // Cancellation must escape so the read loop unwinds instead of spinning on a dead socket.
        await Assert.ThrowsAsync<OperationCanceledException>(() => Dispatch(router, NamespaceA));
    }

    [Fact]
    public async Task DispatchAsync_PassesCancellationTokenThroughToHandler()
    {
        var router = Router();
        var handler = Substitute.For<ICastNamespaceHandler>();
        router.Register(NamespaceA, handler);
        using var cts = new CancellationTokenSource();

        await Dispatch(router, NamespaceA, cts.Token);

        await handler.Received(1).HandleAsync(Arg.Any<CastConnection>(), Arg.Any<CastMessage>(), cts.Token);
    }
}
