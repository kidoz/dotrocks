using System.Text;
using DotRocks.Data;
using DotRocks.Data.Authentication;
using DotRocks.Data.Loading;
using DotRocks.Data.Protocol.Handshake;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Handshake;

public sealed class HandshakeResponseBuilderTests
{
    private static readonly byte[] AuthPart1 = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] AuthPart2 = [9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 0];

    [Fact]
    public void Build_WritesProtocol41NativePasswordResponse()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=localhost;User ID=alice;Password=secret;Database=warehouse"
        );
        ServerHandshake handshake = BuildHandshake(MySqlNativePassword.PluginName);

        byte[] payload = HandshakeResponseBuilder.Build(options, handshake);

        var reader = new ProtocolReader(payload);
        var capabilities = (CapabilityFlags)reader.ReadFixedInteger(4);
        Assert.True(capabilities.HasFlag(CapabilityFlags.Protocol41));
        Assert.True(capabilities.HasFlag(CapabilityFlags.SecureConnection));
        Assert.True(capabilities.HasFlag(CapabilityFlags.PluginAuth));
        Assert.True(capabilities.HasFlag(CapabilityFlags.ConnectWithDb));
        Assert.Equal(0UL, reader.ReadFixedInteger(4));
        Assert.Equal(0x21, reader.ReadByte());
        reader.ReadBytes(23);
        Assert.Equal("alice", reader.ReadNullTerminatedString(Encoding.UTF8));

        ReadOnlySpan<byte> authResponse = reader.ReadLengthEncodedBytes(out bool isNull);
        Assert.False(isNull);
        Assert.Equal(20, authResponse.Length);
        Assert.Equal("warehouse", reader.ReadNullTerminatedString(Encoding.UTF8));
        Assert.Equal(
            MySqlNativePassword.PluginName,
            reader.ReadNullTerminatedString(Encoding.ASCII)
        );
        Assert.True(reader.IsAtEnd);
        Assert.False(ContainsSequence(payload, Encoding.UTF8.GetBytes("secret")));
    }

    [Fact]
    public void Build_RejectsUnsupportedAuthenticationPlugin_WithoutLeakingPassword()
    {
        DotRocksConnectionOptions options = DotRocksConnectionOptions.Parse(
            "Server=localhost;User ID=alice;Password=secret"
        );
        ServerHandshake handshake = BuildHandshake("caching_sha2_password");

        DotRocksException exception = Assert.Throws<DotRocksException>(() =>
            HandshakeResponseBuilder.Build(options, handshake)
        );

        Assert.Contains(
            "Unsupported StarRocks authentication plugin",
            exception.Message,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    private static ServerHandshake BuildHandshake(string authPluginName)
    {
        CapabilityFlags capabilities =
            CapabilityFlags.LongPassword
            | CapabilityFlags.LongFlag
            | CapabilityFlags.Protocol41
            | CapabilityFlags.SecureConnection
            | CapabilityFlags.PluginAuth
            | CapabilityFlags.ConnectWithDb
            | CapabilityFlags.Transactions;

        uint caps = (uint)capabilities;
        using var writer = new ProtocolWriter();
        writer.WriteByte(10);
        writer.WriteNullTerminatedString("8.0.33-StarRocks-3.3", Encoding.ASCII);
        writer.WriteFixedInteger(42, 4);
        writer.WriteBytes(AuthPart1);
        writer.WriteByte(0);
        writer.WriteFixedInteger(caps & 0xFFFF, 2);
        writer.WriteByte(0x21);
        writer.WriteFixedInteger(2, 2);
        writer.WriteFixedInteger((caps >> 16) & 0xFFFF, 2);
        writer.WriteByte(21);
        writer.WriteBytes(new byte[10]);
        writer.WriteBytes(AuthPart2);
        writer.WriteNullTerminatedString(authPluginName, Encoding.ASCII);
        return ServerHandshake.Parse(writer.WrittenSpan);
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return true;
        }

        for (int i = 0; i <= source.Length - value.Length; i++)
        {
            if (source.Slice(i, value.Length).SequenceEqual(value))
            {
                return true;
            }
        }

        return false;
    }
}
