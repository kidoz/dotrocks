using System.Text;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Serialization;
using DotRocks.Protocol.Tests.TestInfrastructure;
using Xunit;

namespace DotRocks.Protocol.Tests.Handshake;

public sealed class ServerHandshakeTests
{
    private static readonly byte[] AuthPart1 = StarRocksPacketFactory.AuthPart1;

    [Fact]
    public void Parses_FullProtocol41Handshake()
    {
        CapabilityFlags caps =
            CapabilityFlags.LongPassword
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth
            | CapabilityFlags.ConnectWithDb;

        // 13-byte part 2 = 12 scramble bytes + trailing NUL.
        byte[] authPart2 = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0];

        byte[] payload = StarRocksPacketFactory.Handshake(
            protocolVersion: 10,
            serverVersion: "8.0.33-StarRocks-3.3",
            connectionId: 42,
            capabilities: caps,
            characterSet: 0x21,
            statusFlags: 2,
            authPluginDataLength: 21,
            authPart2: authPart2,
            authPluginName: "mysql_native_password"
        );

        ServerHandshake handshake = ServerHandshake.Parse(payload);

        Assert.Equal(10, handshake.ProtocolVersion);
        Assert.Equal("8.0.33-StarRocks-3.3", handshake.ServerVersion);
        Assert.Equal(42u, handshake.ConnectionId);
        Assert.Equal(0x21, handshake.CharacterSet);
        Assert.Equal(2, handshake.StatusFlags);
        Assert.Equal("mysql_native_password", handshake.AuthPluginName);
        Assert.True(handshake.Capabilities.HasFlag(CapabilityFlags.Protocol41));
        Assert.True(handshake.Capabilities.HasFlag(CapabilityFlags.PluginAuth));
        Assert.True(handshake.Capabilities.HasFlag(CapabilityFlags.SecureConnection));
        Assert.True(handshake.Capabilities.HasFlag(CapabilityFlags.ConnectWithDb));

        // part1 (8) + part2 (13) = 21, with the single trailing NUL trimmed -> 20 scramble bytes.
        Assert.Equal(20, handshake.AuthPluginData.Length);
        Assert.Equal(AuthPart1, handshake.AuthPluginData[..8]);
    }

    [Fact]
    public void Parses_ShortPre41Handshake()
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(10);
        writer.WriteNullTerminatedString("5.1.0", Encoding.ASCII);
        writer.WriteFixedInteger(7, 4);
        writer.WriteBytes(AuthPart1);
        writer.WriteByte(0); // filler
        writer.WriteFixedInteger((uint)CapabilityFlags.LongPassword, 2);

        ServerHandshake handshake = ServerHandshake.Parse(writer.WrittenSpan);

        Assert.Equal("5.1.0", handshake.ServerVersion);
        Assert.Equal(CapabilityFlags.LongPassword, handshake.Capabilities);
        Assert.Null(handshake.AuthPluginName);
        Assert.Equal(AuthPart1, handshake.AuthPluginData);
    }

    [Fact]
    public void Rejects_UnsupportedProtocolVersion()
    {
        byte[] payload = [9, 0x00];
        Assert.Throws<MalformedPacketException>(() => ServerHandshake.Parse(payload));
    }

    [Fact]
    public void Rejects_ErrorPacketInPlaceOfHandshake()
    {
        byte[] payload = [ProtocolConstants.ErrorPacketHeader, 0x15, 0x04];
        Assert.Throws<MalformedPacketException>(() => ServerHandshake.Parse(payload));
    }
}
