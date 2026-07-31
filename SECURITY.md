# Security Policy

DevWT contains a user-mode Win32 hook runtime, a localhost gateway, a browser
extension, and machine-wide installer scripts. Please report security issues
privately before opening a public issue.

## Supported Versions

The project is currently a preview prototype. Security fixes target the `main` branch until a stable release line exists.

## Reporting A Vulnerability

Use [GitHub private vulnerability reporting](https://github.com/salihozkara/DevWT/security/advisories/new)
or contact the maintainer through the repository owner profile. Include:

- affected commit or release,
- operating system and Windows build,
- whether the issue affects the hook runtime, gateway, browser extension,
  service, or installer,
- reproduction steps,
- expected impact.

## Security Boundaries

- The default Web Console and reverse proxy listeners are localhost-only.
- The hook runtime is intended to rewrite only loopback bind/connect traffic
  and child-process launches.
- Internal DevWT routing headers are removed before requests reach the target
  application.
- TLS inspection is opt-in and requires explicitly trusting the DevWT gateway
  root certificate.
- Installation and machine-wide trust operations require administrator
  approval.
