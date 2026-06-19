using System.Security.Cryptography;
using System.Text;

namespace DotRocks.Data.Authentication;

internal static class MySqlNativePassword
{
    public const string PluginName = "mysql_native_password";

    public static byte[] CreateAuthenticationResponse(
        string password,
        ReadOnlySpan<byte> authenticationChallenge
    )
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            return [];
        }

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] stage1 = SHA1.HashData(passwordBytes);
            byte[] stage2 = SHA1.HashData(stage1);

            byte[] combined = new byte[authenticationChallenge.Length + stage2.Length];
            authenticationChallenge.CopyTo(combined);
            stage2.CopyTo(combined.AsSpan(authenticationChallenge.Length));

            byte[] stage3 = SHA1.HashData(combined);
            byte[] response = new byte[stage3.Length];
            for (int i = 0; i < response.Length; i++)
            {
                response[i] = (byte)(stage3[i] ^ stage1[i]);
            }

            CryptographicOperations.ZeroMemory(stage1);
            CryptographicOperations.ZeroMemory(stage2);
            CryptographicOperations.ZeroMemory(stage3);
            CryptographicOperations.ZeroMemory(combined);
            return response;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
