using System.Text;
using DotRocks.Data.Protocol.Serialization;

namespace DotRocks.Data.Protocol.Handshake;

/// <summary>
/// The server's initial handshake (protocol version 10 / "HandshakeV10"), as sent by StarRocks on
/// connect. Parsing is bounds-checked; an unsupported protocol version or an error packet in place
/// of a handshake fails explicitly rather than being interpreted as best-effort data.
/// </summary>
internal sealed class ServerHandshake
{
    /// <summary>The only handshake protocol version DotRocks supports.</summary>
    public const int SupportedProtocolVersion = 10;

    private const int AuthPluginDataPart1Length = 8;
    private const int ReservedLength = 10;
    private const int MinimumAuthPluginDataPart2Length = 13;

    private ServerHandshake(
        int protocolVersion,
        string serverVersion,
        uint connectionId,
        CapabilityFlags capabilities,
        byte characterSet,
        ushort statusFlags,
        byte[] authPluginData,
        string? authPluginName
    )
    {
        ProtocolVersion = protocolVersion;
        ServerVersion = serverVersion;
        ConnectionId = connectionId;
        Capabilities = capabilities;
        CharacterSet = characterSet;
        StatusFlags = statusFlags;
        AuthPluginData = authPluginData;
        AuthPluginName = authPluginName;
    }

    public int ProtocolVersion { get; }
    public string ServerVersion { get; }
    public uint ConnectionId { get; }
    public CapabilityFlags Capabilities { get; }
    public byte CharacterSet { get; }
    public ushort StatusFlags { get; }

    /// <summary>The authentication challenge (scramble) bytes. Never log these with a derived response.</summary>
    public byte[] AuthPluginData { get; }

    /// <summary>The authentication plugin name the server requested, when advertised.</summary>
    public string? AuthPluginName { get; }

    /// <summary>Parses a server handshake from a single packet <paramref name="payload"/>.</summary>
    public static ServerHandshake Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new ProtocolReader(payload);

        byte protocolVersion = reader.ReadByte();
        if (protocolVersion == ProtocolConstants.ErrorPacketHeader)
        {
            throw new MalformedPacketException(
                "The server returned an error packet instead of a handshake."
            );
        }

        if (protocolVersion != SupportedProtocolVersion)
        {
            throw new MalformedPacketException(
                $"Unsupported handshake protocol version {protocolVersion}; DotRocks supports version {SupportedProtocolVersion}."
            );
        }

        string serverVersion = reader.ReadNullTerminatedString(Encoding.ASCII);
        uint connectionId = (uint)reader.ReadFixedInteger(4);

        byte[] authPart1 = reader.ReadBytes(AuthPluginDataPart1Length).ToArray();
        _ = reader.ReadByte(); // filler (0x00)

        uint capabilitiesLower = (uint)reader.ReadFixedInteger(2);
        var capabilities = (CapabilityFlags)capabilitiesLower;

        byte characterSet = 0;
        ushort statusFlags = 0;
        byte[] authData = authPart1;
        string? authPluginName = null;

        if (!reader.IsAtEnd)
        {
            characterSet = reader.ReadByte();
            statusFlags = (ushort)reader.ReadFixedInteger(2);
            uint capabilitiesUpper = (uint)reader.ReadFixedInteger(2);
            capabilities = (CapabilityFlags)(capabilitiesLower | (capabilitiesUpper << 16));

            byte authPluginDataLen = reader.ReadByte();
            reader.ReadBytes(ReservedLength);

            if (capabilities.HasFlag(CapabilityFlags.SecureConnection))
            {
                int part2Length = Math.Max(
                    MinimumAuthPluginDataPart2Length,
                    authPluginDataLen - AuthPluginDataPart1Length
                );
                ReadOnlySpan<byte> authPart2 = reader.ReadBytes(part2Length);
                authData = Combine(authPart1, authPart2);
            }

            if (capabilities.HasFlag(CapabilityFlags.PluginAuth))
            {
                authPluginName = reader.ReadNullTerminatedString(Encoding.ASCII);
            }
        }

        return new ServerHandshake(
            protocolVersion,
            serverVersion,
            connectionId,
            capabilities,
            characterSet,
            statusFlags,
            TrimSingleTrailingNul(authData),
            authPluginName
        );
    }

    private static byte[] Combine(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] combined = new byte[first.Length + second.Length];
        first.CopyTo(combined);
        second.CopyTo(combined.AsSpan(first.Length));
        return combined;
    }

    private static byte[] TrimSingleTrailingNul(byte[] data)
    {
        if (data.Length > 0 && data[^1] == 0)
        {
            return data[..^1];
        }

        return data;
    }
}
