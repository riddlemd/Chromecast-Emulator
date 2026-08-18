using System.Security.Cryptography;
using ChromecastEmulator.Proto;
using ChromecastEmulator.Transport;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace ChromecastEmulator.Protocol;

/// The response is signed by our own self-signed key, so it will NOT satisfy a sender
/// that verifies the chain against Google's Eureka CA -- only factory-provisioned devices
/// can. Senders that skip verification (custom clients, SDK developer modes) proceed.
public sealed class DeviceAuthHandler : ICastNamespaceHandler
{
    private readonly EmulatorOptions _options;
    private readonly DeviceIdentity _identity;
    private readonly ILogger<DeviceAuthHandler> _logger;

    public DeviceAuthHandler(EmulatorOptions options, DeviceIdentity identity, ILogger<DeviceAuthHandler> logger)
    {
        _options = options;
        _identity = identity;
        _logger = logger;
    }

    public async Task HandleAsync(CastConnection connection, CastMessage message, CancellationToken ct)
    {
        if (message.PayloadType != CastMessage.Types.PayloadType.Binary)
        {
            _logger.LogWarning("deviceauth message was not binary; ignoring");
            return;
        }

        var request = DeviceAuthMessage.Parser.ParseFrom(message.PayloadBinary);
        if (request.Challenge is null)
        {
            _logger.LogWarning("deviceauth message carried no challenge");
            return;
        }

        if (_options.AuthMode == AuthMode.Ignore)
        {
            _logger.LogDebug("   auth mode 'ignore': not replying to the challenge");
            return;
        }

        var response = _options.AuthMode == AuthMode.Error
            ? new DeviceAuthMessage { Error = new AuthError { ErrorType = AuthError.Types.ErrorType.InternalError } }
            : BuildResponse(request.Challenge, connection.ServerCertificateDer);

        await connection.ReplyBinaryAsync(message, response.ToByteArray(), ct);
    }

    private DeviceAuthMessage BuildResponse(AuthChallenge challenge, byte[] serverCertificateDer)
    {
        var nonce = challenge.SenderNonce.ToByteArray();

        // Signed blob is sender_nonce || our TLS certificate, matching what a real
        // device signs; senders that check it recompute from the TLS session cert.
        var signed = new byte[nonce.Length + serverCertificateDer.Length];
        nonce.CopyTo(signed, 0);
        serverCertificateDer.CopyTo(signed, nonce.Length);

        var hash = challenge.HashAlgorithm == Proto.HashAlgorithm.Sha256
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA1;

        var padding = challenge.SignatureAlgorithm == SignatureAlgorithm.RsassaPss
            ? RSASignaturePadding.Pss
            : RSASignaturePadding.Pkcs1;

        var signature = _identity.PrivateKey.SignData(signed, hash, padding);

        return new DeviceAuthMessage
        {
            Response = new AuthResponse
            {
                Signature = ByteString.CopyFrom(signature),
                ClientAuthCertificate = ByteString.CopyFrom(serverCertificateDer),
                SignatureAlgorithm = challenge.SignatureAlgorithm,
                HashAlgorithm = challenge.HashAlgorithm,
                SenderNonce = challenge.SenderNonce,
            },
        };
    }
}
