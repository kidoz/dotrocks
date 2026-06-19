using System.Text;
using DotRocks.Data;
using DotRocks.Data.Protocol.Results;
using DotRocks.Data.Protocol.Serialization;
using Xunit;

namespace DotRocks.Protocol.Tests.Results;

public sealed class AuthenticationResultTests
{
    [Fact]
    public void Read_OkPacket_Succeeds()
    {
        AuthenticationResult.Read([0x00], connectionId: 42);
    }

    [Fact]
    public void Read_ErrorPacket_ThrowsSanitizedDotRocksException()
    {
        using var writer = new ProtocolWriter();
        writer.WriteByte(ProtocolConstants.ErrorPacketHeader);
        writer.WriteFixedInteger(1045, 2);
        writer.WriteByte((byte)'#');
        writer.WriteBytes(Encoding.ASCII.GetBytes("28000"));
        writer.WriteBytes(Encoding.UTF8.GetBytes("Access denied"));

        DotRocksException exception = Assert.Throws<DotRocksException>(() =>
            AuthenticationResult.Read(writer.WrittenSpan, connectionId: 99)
        );

        Assert.Equal(1045, exception.ServerErrorCode);
        Assert.Equal("28000", exception.SqlState);
        Assert.Equal(99u, exception.ConnectionId);
        Assert.False(exception.IsTransient);
        Assert.Contains("Access denied", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AuthSwitchRequest_ThrowsExplicitUnsupportedException()
    {
        DotRocksException exception = Assert.Throws<DotRocksException>(() =>
            AuthenticationResult.Read([0xFE], connectionId: 42)
        );

        Assert.Contains("authentication switch", exception.Message, StringComparison.Ordinal);
    }
}
