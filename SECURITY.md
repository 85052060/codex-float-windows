# Security Policy

## Supported versions

Security fixes are provided for the latest published release.

## Reporting a vulnerability

Please do not include access tokens, `auth.json`, private paths, or complete app-server payloads in a public issue.

When reporting a security problem, provide the affected version, expected behavior, observed behavior, and a minimal reproduction with all secrets removed.

## Security design

- The application does not store Codex credentials.
- Authentication is handled by the locally installed Codex CLI.
- No telemetry or usage data is sent to the project maintainers.
- The application does not download or replace Codex.
