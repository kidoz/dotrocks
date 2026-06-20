using System.Text;
using DotRocks.Data;
using DotRocks.Data.Authentication;
using DotRocks.Data.Loading;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Handshake;

internal static class HandshakeResponseBuilder
{
    private const byte DefaultCharacterSet = 0x21;
    private const int ReservedLength = 23;

    public static byte[] BuildSslRequest(
        DotRocksConnectionOptions options,
        ServerHandshake handshake
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handshake);

        if (options.SslMode == DotRocksSslMode.Disabled)
        {
            throw new DotRocksException("Ssl Mode is disabled for this connection.");
        }

        if (!handshake.Capabilities.HasFlag(CapabilityFlags.Ssl))
        {
            throw new DotRocksException(
                "The StarRocks server does not advertise SQL protocol TLS support."
            );
        }

        CapabilityFlags capabilities =
            BuildCapabilityFlags(options, handshake) | CapabilityFlags.Ssl;
        using var writer = new ProtocolWriter();
        writer.WriteFixedInteger((uint)capabilities, 4);
        writer.WriteFixedInteger(0, 4);
        writer.WriteByte(DefaultCharacterSet);
        writer.WriteBytes(new byte[ReservedLength]);
        return writer.ToArray();
    }

    public static byte[] Build(DotRocksConnectionOptions options, ServerHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handshake);

        if (!handshake.Capabilities.HasFlag(CapabilityFlags.Protocol41))
        {
            throw new DotRocksException(
                "The StarRocks server does not support Protocol 4.1 handshakes."
            );
        }

        if (!handshake.Capabilities.HasFlag(CapabilityFlags.SecureConnection))
        {
            throw new DotRocksException(
                "The StarRocks server does not support secure authentication data."
            );
        }

        if (!handshake.Capabilities.HasFlag(CapabilityFlags.PluginAuth))
        {
            throw new DotRocksException(
                "The StarRocks server did not advertise an authentication plugin."
            );
        }

        if (
            !StringComparer.Ordinal.Equals(handshake.AuthPluginName, MySqlNativePassword.PluginName)
        )
        {
            throw new DotRocksException(
                $"Unsupported StarRocks authentication plugin '{handshake.AuthPluginName ?? "(none)"}'."
            );
        }

        CapabilityFlags capabilities = BuildCapabilityFlags(options, handshake);
        byte[] authResponse = MySqlNativePassword.CreateAuthenticationResponse(
            options.Password,
            handshake.AuthPluginData
        );

        using var writer = new ProtocolWriter();
        writer.WriteFixedInteger((uint)capabilities, 4);
        writer.WriteFixedInteger(0, 4);
        writer.WriteByte(DefaultCharacterSet);
        writer.WriteBytes(new byte[ReservedLength]);
        writer.WriteNullTerminatedString(options.UserId, Encoding.UTF8);
        writer.WriteLengthEncodedBytes(authResponse);
        if (options.Database.Length > 0)
        {
            writer.WriteNullTerminatedString(options.Database, Encoding.UTF8);
        }

        writer.WriteNullTerminatedString(MySqlNativePassword.PluginName, Encoding.ASCII);
        Array.Clear(authResponse);
        return writer.ToArray();
    }

    private static CapabilityFlags BuildCapabilityFlags(
        DotRocksConnectionOptions options,
        ServerHandshake handshake
    )
    {
        CapabilityFlags capabilities =
            CapabilityFlags.LongPassword
            | CapabilityFlags.LongFlag
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth
            | CapabilityFlags.Transactions;

        if (options.Database.Length > 0)
        {
            capabilities |= CapabilityFlags.ConnectWithDb;
        }

        if (options.SslMode != DotRocksSslMode.Disabled)
        {
            capabilities |= CapabilityFlags.Ssl;
        }

        return capabilities & handshake.Capabilities;
    }
}
