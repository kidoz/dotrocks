# Security

DotRocks handles network transport, credentials, and untrusted server-provided bytes, so
security is a first-class concern. This article covers the security-relevant settings and
behaviors you should know to configure DotRocks safely.

> For the maintainer-facing threat model and full policy, see
> [`SECURITY.md`](https://github.com/kidoz/dotrocks/blob/main/SECURITY.md). This article is
> the user-facing companion.

## Two transports, two security stories

DotRocks uses **two** separate network paths, each with its own security controls:

| Transport | Port | Purpose | TLS control |
|---|---|---|---|
| SQL query protocol (MySQL wire) | 9030 | Queries, transactions, parameterized commands | `Ssl Mode`, `Trust Server Certificate`, `Ssl Revocation Check` |
| Stream Load (HTTP) | 8030 | Bulk ingestion | HTTPS endpoint URL, `Allow Insecure Stream Load`, redirect vetting |

Secure each one independently. A `Required` SQL TLS mode does **not** protect Stream Load
unless the Stream Load endpoint is also HTTPS.

## SQL protocol TLS

See [Connection strings](connection-strings.md#tls-modes) for the full `DotRocksSslMode`
reference. The essentials:

- **Default is `Preferred`** — opportunistic TLS. Secure against a passive eavesdropper,
  but an active attacker can strip the server's advertised TLS capability to force
  plaintext. Use `Required` for any non-local server:

  ```ini
  Server=starrocks.example.com;User ID=app;Ssl Mode=Required
  ```

- **Certificate validation is on by default.** The server certificate is validated against
  the system trust store and the hostname is checked. Leave `Trust Server Certificate=false`
  in production.
- **`Trust Server Certificate=true`** disables chain and hostname validation. It is only
  valid with `Ssl Mode=Required` (the combination is rejected otherwise). Use it only for
  private/self-signed CAs in trusted environments, and **never** across an untrusted
  network — any certificate an on-path attacker presents would be trusted.
- **`Ssl Revocation Check`** defaults to `Offline` (cached CRLs, no blocking fetch). Set it
  to `Online` for a high-security deployment that must fail on a revoked certificate.

### Private CA / self-signed certificate

The recommended path is to install the private CA in the OS trust store so normal
validation works. If that is not possible:

```csharp
var builder = new DotRocksConnectionStringBuilder(connectionString)
{
    SslMode = DotRocksSslMode.Required,
    TrustServerCertificate = true, // last resort; disables chain + hostname validation
};
```

See the [`SecureConnection` sample](https://github.com/kidoz/dotrocks/blob/main/samples/DotRocks.Samples.SecureConnection/Program.cs).

### Fail-closed enum validation

An unrecognized `Ssl Mode` value — including an out-of-range numeric string such as
`Ssl Mode=3` or an undefined typed-enum value — is **rejected**. It never silently falls
back to plaintext. The same applies to `Ssl Revocation Check`. A security setting cannot
reach negotiation as an unknown value.

## Stream Load transport security

Stream Load sends **Basic authentication** (user + password) over HTTP. To keep
credentials off the wire in cleartext, DotRocks enforces HTTPS by default.

- The Stream Load endpoint must use `https://`, **or** you must explicitly set
  `Allow Insecure Stream Load=true` for a trusted local test server. An HTTP endpoint
  without that opt-in is rejected at construction.
- **Basic credentials are only forwarded over HTTPS** (or when insecure load is opted in).
  An HTTPS→HTTP redirect is refused even when insecure load is allowed, since it would
  forward credentials in cleartext.

```csharp
using var client = new DotRocksStreamLoadClient(
    "Server=starrocks.example.com;User ID=loader;Password=secret;"
    + "Stream Load Endpoint=https://starrocks.example.com:8030"
);
```

See [Stream Load](stream-load.md) for the loading API.

## Stream Load redirect vetting (SSRF defense)

StarRocks may redirect a Stream Load from the FE to a BE node. Because the redirect target
is server-chosen, DotRocks treats it as untrusted and vets it to prevent
**Server-Side Request Forgery** and credential forwarding.

Every outbound connection — the initial request and each redirect hop — is routed through
a connect-time gate that:

- **Resolves the host once and connects the socket to exactly that vetted address.** This
  closes the DNS-rebinding gap: a host cannot resolve to a benign address for validation
  and an internal address for the actual connect.
- **Refuses** loopback, link-local (including the `169.254.169.254` and IPv6
  `fd00:ec2::254` cloud-metadata endpoints), multicast, unspecified, and IPv6
  unique-local targets — unless the configured endpoint is itself loopback (single-node /
  local development).
- **Normalizes IPv4-mapped IPv6** literals (e.g. `::ffff:169.254.169.254`) so the IPv4
  range checks still apply.
- **Fails closed** on DNS resolution failure: the load fails rather than connecting to an
  unvetted target.
- **Rejects** unsupported redirect schemes, embedded user info, and TLS downgrades.

The **configured endpoint host is operator-supplied and trusted**, so it is exempt from
vetting — only server-chosen redirect hosts are checked.

## Credential redaction and secret hygiene

Passwords and connection strings are kept out of every observable surface by design:

| Surface | Behavior |
|---|---|
| `DotRocksConnection.ConnectionString` getter | Password omitted entirely |
| `DotRocksConnectionStringBuilder.ToString()` | Password redacted as `***` |
| Connection-pool key `ToString()` | Password redacted |
| Exception messages | No password, connection string, SQL text, or parameter values |
| Telemetry spans and metrics | No SQL text, parameters, credentials, or usernames (see below) |

Load credentials from configuration, environment variables, or a secret store — never
inline them in source. The [DTR0012 analyzer](analyzers.md) flags a literal password
embedded in a connection string.

## What telemetry never emits

DotRocks emits OpenTelemetry tracing and metrics, but the instrumentation is deliberately
hardened against leaking secrets or inflating cardinality. It **never** emits:

- Raw SQL text, query literals, or parameter values
- Connection strings, passwords, or usernames
- Table identifiers or database-object names beyond the connection's `Database`
- Server hostnames (a configured host may be tenant-bearing)
- Unbounded server-controlled strings (SQLSTATE values are validated to a 5-character ANSI
  form before use as a tag)

See [Observability](observability.md) for the full metric and span reference.

## Reporting a vulnerability

Report security issues **privately** via GitHub's
[private vulnerability reporting](https://github.com/kidoz/dotrocks/security/advisories/new),
not a public issue. Include the affected version, a description, and reproduction steps.

## See also

- [Connection strings](connection-strings.md)
- [Stream Load](stream-load.md)
- [Observability](observability.md)
- [`SECURITY.md`](https://github.com/kidoz/dotrocks/blob/main/SECURITY.md)
