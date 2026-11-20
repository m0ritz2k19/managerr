# Security Policy

## Supported Versions

Security updates are applied to the latest release on the `main` branch.

## Reporting a Vulnerability

Please do not open a public GitHub issue for security vulnerabilities, authentication bypasses, or secret leaks.

Instead, report vulnerabilities privately through GitHub Security Advisories (`Security -> Advisories -> Report a vulnerability` on the repository) and include:

1. A description of the issue and affected endpoint or component.
2. Steps to reproduce or a minimal proof of concept.
3. Any suggested mitigation if known.

## Self-Hosting Security Checklist

* Bind `MANAGERR_BIND_IP=127.0.0.1` when running behind a reverse proxy on the same host.
* Leave `Managerr__Auth__TrustForwardedHeaders=false` unless Managerr sits behind a trusted reverse proxy, and configure a non-empty `Managerr__Auth__TrustedProxyCidrs` allowlist (plus `Managerr__Auth__AllowedHosts` when rewriting hosts via `X-Forwarded-Host`).
* Back up both `/config/*.db` and `/config/master.key` together, and store `master.key` with strict file permissions.
* Prefer least-privilege scoped API keys (`mgr_live_`) for integrations such as Seerr, Prowlarr consumers, and MCP clients.
