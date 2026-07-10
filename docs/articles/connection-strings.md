# Connection strings

A reference for DotRocks connection strings: every supported keyword, its aliases,
default, and validation rules, plus guidance on TLS, pooling, retries, and credential
redaction.

> The authoritative keyword set lives in
> [`DotRocksConnectionStringKeywords.cs`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Data/DotRocksConnectionStringKeywords.cs)
> and
> [`DotRocksConnectionOptions.cs`](https://github.com/kidoz/dotrocks/blob/main/src/DotRocks.Data/DotRocksConnectionOptions.cs).
> This article mirrors that source. When the two disagree, the source wins.

## Minimal example

```csharp
await using var connection = new DotRocksConnection(
    "Server=starrocks.example.com;Port=9030;User ID=loader;Password=secret;Database=warehouse;Ssl Mode=Required"
);
await connection.OpenAsync();
```

Keyword names are **case-insensitive** and accept the aliases listed below. Whitespace
inside a keyword name is significant (`User ID`, not `UserID`), but you may use the
single-token alias (`Uid`) instead.

## Keyword reference

| Keyword | Aliases | Type | Default | Validation |
|---|---|---|---|---|
| `Server` | `Host`, `Data Source` | string | `127.0.0.1` | non-empty |
| `Port` | — | int | `9030` | 1–65535 |
| `User ID` | `UserID`, `User`, `Uid`, `Username` | string | `root` | non-empty |
| `Password` | `Pwd` | string | `""` | — |
| `Database` | `Initial Catalog` | string | `""` | — |
| `Connection Timeout` | `Connect Timeout`, `Timeout` | int (seconds) | `15` | > 0 |
| `Pooling` | — | bool | `false` | — |
| `Minimum Pool Size` | `Min Pool Size` | int | `0` | ≥ 0 and ≤ Maximum Pool Size |
| `Maximum Pool Size` | `Max Pool Size` | int | `100` | > 0 and ≤ 65535 |
| `Connection Idle Timeout` | `Idle Timeout` | int (seconds) | `300` | > 0 |
| `Connection Lifetime` | `ConnectionLifetime`, `Lifetime` | int (seconds) | `0` (unlimited) | ≥ 0 |
| `Connection Retries` | `ConnectionRetries`, `Connect Retry Count` | int | `0` | ≥ 0 |
| `Connection Retry Delay` | `ConnectionRetryDelay`, `Retry Delay` | int (ms) | `200` | ≥ 0 |
| `Ssl Mode` | `SSL Mode`, `SslMode` | `DotRocksSslMode` | `Preferred` | defined member |
| `Trust Server Certificate` | `TrustServerCertificate` | bool | `false` | requires `Ssl Mode=Required` |
| `Ssl Revocation Check` | `SSL Revocation Check`, `SslRevocationCheck`, `Revocation` | `X509RevocationMode` | `Offline` | defined member |
| `Stream Load Endpoint` | `StreamLoadEndpoint`, `Stream Load URL`, `Http Endpoint` | absolute Uri | `http://{Server}:8030` | http/https; no embedded user info |
| `Allow Insecure Stream Load` | `AllowInsecureStreamLoad`, `Allow Insecure StreamLoad` | bool | `false` | — |
| `Server Compatibility Level` | `ServerCompatibilityLevel`, `Compatibility Level` | StarRocks version | auto-detect | valid version string |

## TLS modes

The `Ssl Mode` keyword controls TLS on the **SQL query protocol** connection (the MySQL
wire protocol on port 9030). Stream Load uses a separate HTTP transport — see
[Stream Load](stream-load.md) and [Security](security.md).

| `DotRocksSslMode` | Behavior |
|---|---|
| `Disabled` | Never request TLS. Plaintext only. |
| `Required` | Require TLS and **fail** when the server cannot negotiate it. Use this for any non-local server to resist an active downgrade. |
| `Preferred` (default) | Use TLS when the server advertises it, otherwise continue in plaintext. Secure against a passive eavesdropper on a TLS-capable server, compatible with plaintext-only deployments. Does **not** defend against an active attacker who strips the server's TLS capability to force a downgrade. |

```ini
Server=starrocks.example.com;User ID=loader;Password=secret;Ssl Mode=Required
```

### Certificate validation

- By default DotRocks validates the server certificate **and** the hostname against the
  system trust store. Leave `Trust Server Certificate=false` in production.
- `Trust Server Certificate=true` bypasses certificate validation. It is only valid with
  `Ssl Mode=Required` (DotRocks rejects the combination otherwise, since the bypass would
  be a silent no-op on a plaintext fallback). Use it only for private/self-signed CAs in
  trusted environments.
- `Ssl Revocation Check` defaults to `Offline` (uses cached revocation lists, avoiding a
  blocking network fetch at connect time). Set it to `Online` to require a live OCSP/CRL
  fetch, or `NoCheck` to skip revocation checks.

> **Fail-closed enums.** An unrecognized `Ssl Mode` value (including an out-of-range
> numeric string such as `Ssl Mode=3`) is **rejected** rather than falling back to
> plaintext. The same applies to `Ssl Revocation Check`. A security setting can never
> reach negotiation as an unknown value.

## Connection pooling

Pooling is **off by default**. Enable it for any workload that opens connections
frequently:

```ini
Server=starrocks.example.com;User ID=app;Pooling=true;Maximum Pool Size=50
```

| Keyword | Default | Purpose |
|---|---|---|
| `Pooling` | `false` | Enables the per-configuration connection pool. |
| `Minimum Pool Size` | `0` | Idle connections retained below this count are not pruned. |
| `Maximum Pool Size` | `100` | Hard cap on concurrent leased connections (bounded to 65535 to resist resource exhaustion). A lease request blocks (awaiting the pool gate) when the cap is reached, up to `Connection Timeout`. |
| `Connection Idle Timeout` | `300` (s) | Idle connections older than this are pruned, down to `Minimum Pool Size`. |
| `Connection Lifetime` | `0` (unlimited) | A returned connection older than this is discarded instead of reused. A small jitter is applied so connections do not all expire together. |

Each **distinct connection-string configuration** gets its own pool. Pools with no idle
connections and no outstanding leases are reaped from the process-wide registry, so
connection strings that vary per request do not accumulate pool objects over time.

## Retries and lifetime

Opening a connection is retried on **transient** failures only (classified via
`DotRocksException.IsTransient`). A plain server error is never retried.

| Keyword | Default | Purpose |
|---|---|---|
| `Connection Retries` | `0` (no retries) | Extra attempts after the first. `0` means one attempt. |
| `Connection Retry Delay` | `200` (ms) | Delay between retries. `0` retries immediately. |

## Server compatibility level

DotRocks auto-detects the StarRocks version from `SELECT current_version()` and gates
version-specific behavior from it. When the query port is unreachable (for example a
Stream-Load-only deployment), pin the version to avoid the probe:

```ini
Server=starrocks.example.com;Server Compatibility Level=4.0.7
```

## The typed builder

For configuration assembled in code, use `DotRocksConnectionStringBuilder`:

```csharp
var builder = new DotRocksConnectionStringBuilder
{
    Server = "starrocks.example.com",
    Port = 9030,
    UserId = "loader",
    Password = secretFromConfig,
    Database = "warehouse",
    Pooling = true,
    MaximumPoolSize = 50,
    SslMode = DotRocksSslMode.Required,
};

await using var connection = new DotRocksConnection(builder.ConnectionString);
```

The builder validates bounds at the setter (for example `Port` range, `MaximumPoolSize`
ceiling, defined `SslMode`), so a misconfiguration fails immediately rather than at
`Open()`.

## Credential redaction

The `ConnectionString` getter and `DotRocksConnectionStringBuilder.ToString()` never
return the password:

```csharp
var connection = new DotRocksConnection("...;Password=secret;...");
Console.WriteLine(connection.ConnectionString);
// Server=...;User ID=...;Password=... (no password value)
```

The cleartext password is held internally for pool keying and authentication, but it is
omitted from the public getter entirely (the ADO.NET `PersistSecurityInfo=false`
convention), and the record `ToString()` redacts it as `***`. See
[Security](security.md) for the full secret-hygiene policy.

## See also

- [Getting started](getting-started.md)
- [Security](security.md)
- [Stream Load](stream-load.md)
- [EF Core entity mapping](ef-core-entity-mapping.md)
