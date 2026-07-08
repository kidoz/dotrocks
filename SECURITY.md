# Security Policy

## Supported versions

Security fixes target the latest released version and `main`. The current latest tagged
release is 1.3.0.

## Reporting a vulnerability

Please report security issues privately via GitHub's
[private vulnerability reporting](https://github.com/kidoz/dotrocks/security/advisories/new)
rather than opening a public issue. Include affected version, a description, and reproduction
steps. We aim to acknowledge reports within a few business days.

## Security posture

- DotRocks parses untrusted server bytes with bounds-checked readers and a logical-payload
  cap to resist malformed/oversized packets.
- Credentials and connection strings are redacted from exceptions and diagnostics.
- SQL protocol TLS uses platform certificate and host validation; `Ssl Mode` defaults to
  `Preferred` (opportunistic: TLS when the server advertises it, plaintext otherwise). Use
  `Required` for non-local servers to reject a connection that cannot negotiate TLS and resist
  active downgrade.
- HTTP Stream Load rejects plaintext credentials and refuses HTTPS→HTTP redirect downgrades. Every
  outbound connection (initial and each redirect hop) is vetted at connect time: the host is
  resolved once and the socket connects to exactly that vetted address, refusing loopback/link-local
  (including the `169.254.169.254` cloud-metadata address)/multicast/unspecified targets unless the
  configured endpoint is itself loopback. Resolving and connecting in one step (rather than
  validating a host name and letting the HTTP client re-resolve) closes the DNS-rebinding gap and
  fails closed on resolution failure, so a malicious redirect cannot forward credentials to
  internal-only services.
- Connection-string values are validated: unrecognized `Ssl Mode` (and other enum) values fail
  closed rather than silently falling back to plaintext, and `Maximum Pool Size` is bounded to
  resist resource-exhaustion via an oversized pool.
- CI runs CodeQL and NuGet vulnerability auditing; dependencies are pinned with lock files.
- Assemblies are unsigned (no strong-name/Authenticode); packages are published with build
  provenance attestation.
