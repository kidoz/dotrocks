using DotRocks.Data.Authentication;
using Xunit;

namespace DotRocks.Protocol.Tests.Authentication;

public sealed class MySqlNativePasswordTests
{
    [Fact]
    public void EmptyPassword_WritesEmptyAuthenticationResponse()
    {
        byte[] response = MySqlNativePassword.CreateAuthenticationResponse(string.Empty, [1, 2, 3]);

        Assert.Empty(response);
    }

    [Fact]
    public void PasswordResponse_MatchesKnownVector()
    {
        byte[] challenge = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];

        byte[] response = MySqlNativePassword.CreateAuthenticationResponse("secret", challenge);

        Assert.Equal(
            [
                0xB3,
                0x2B,
                0xB3,
                0xA5,
                0x83,
                0xE1,
                0x34,
                0x0C,
                0x0A,
                0x11,
                0x08,
                0xD5,
                0x8B,
                0x1B,
                0xE4,
                0x97,
                0x81,
                0xAD,
                0x8C,
                0x2F,
            ],
            response
        );
        Assert.DoesNotContain((byte)'s', response);
    }
}
