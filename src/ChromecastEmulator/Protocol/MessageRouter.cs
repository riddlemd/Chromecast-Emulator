using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Protocol;

public sealed class MessageRouter
{
    private readonly Dictionary<string, ICastNamespaceHandler> _handlers = new(StringComparer.Ordinal);
    private ICastNamespaceHandler? _fallback;
    private readonly ILogger<MessageRouter> _logger;

    public MessageRouter(ILogger<MessageRouter> logger)
    {
        _logger = logger;
    }

    public void Register(string ns, ICastNamespaceHandler handler) => _handlers[ns] = handler;

    public void RegisterFallback(ICastNamespaceHandler handler) => _fallback = handler;

    public async Task DispatchAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        var handler = _handlers.GetValueOrDefault(message.Namespace) ?? _fallback;
        if (handler is null)
        {
            _logger.LogWarning("no handler for namespace {Namespace}", message.Namespace);
            return;
        }

        try
        {
            await handler.HandleAsync(connection, message, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ShortNamespace} handler threw", CastNamespaces.Shorten(message.Namespace));
        }
    }
}
