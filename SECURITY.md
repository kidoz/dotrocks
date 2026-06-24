# Security Policy

## Supported versions

DotRocks is in active pre-1.0 development. Security fixes target the latest released version
and `main`.

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
- HTTP Stream Load rejects plaintext credentials and refuses HTTPS→HTTP redirect downgrades.
- CI runs CodeQL and NuGet vulnerability auditing; dependencies are pinned with lock files.
- Assemblies are unsigned (no strong-name/Authenticode); packages are published with build
  provenance attestation.
