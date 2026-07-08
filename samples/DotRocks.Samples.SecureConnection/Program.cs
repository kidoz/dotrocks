using DotRocks.Data;

// Secure TLS sample: require an encrypted SQL-protocol connection with full certificate and
// host-name validation. Ssl Mode=Required refuses to connect unless TLS is negotiated, so an
// active attacker cannot strip the server's advertised TLS support to force plaintext (the
// default Preferred mode allows that opportunistic fallback).
var builder = new DotRocksConnectionStringBuilder(
    Environment.GetEnvironmentVariable("DOTROCKS_CONNECTION_STRING")
        ?? "Server=starrocks.internal;Port=9030;User ID=app;Database=dotrocks_sample"
)
{
    SslMode = DotRocksSslMode.Required,
    // Ssl Revocation Check defaults to Offline (cached CRLs only). For a high-security
    // deployment set it to Online to fail on a revoked certificate:
    //   SslRevocationCheck = X509RevocationMode.Online,
};

await using var connection = new DotRocksConnection(builder.ConnectionString);
await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = "SELECT current_user()";
Console.WriteLine($"Connected over TLS as {await command.ExecuteScalarAsync()}.");

// Using a private CA or a self-signed certificate? Prefer installing the CA in the OS trust
// store. Only as a last resort add Trust Server Certificate=true (which requires Ssl
// Mode=Required): it disables chain AND host-name validation, so any certificate an on-path
// attacker presents is trusted — never use it across an untrusted network.
