using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;

namespace ChromecastEmulator.Protocol;

public interface ICastNamespaceHandler
{
    Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct);
}

public interface IConnectionRegistry
{
    IReadOnlyList<CastConnection> Connections { get; }
}
