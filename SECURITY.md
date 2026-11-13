# Security and privacy

The current Windows port reads local usage records and may start the installed Codex app-server to query account limits. The latter may make network requests using the user's existing Codex login. The Windows interface does not expose the upstream macOS reward-claiming or web-login features.

Session files can contain sensitive conversation text even when only usage fields are displayed. Parsed usage is held in a bounded in-memory cache for refresh performance. Do not include actual session files, credentials, account responses, API tokens, private paths or unsanitized screenshots in reports, tests or commits. Windows or other software may manage memory and diagnostic files independently of this app.

Only the latest Windows development version is currently maintained. Before sharing a report, reproduce the issue with synthetic data if possible.

## Reporting a vulnerability

Use **Report a vulnerability** on this repository's GitHub Security tab when available. If private reporting is not enabled, open an issue requesting a private reporting channel without posting credentials, private data, exploit details or a working exploit. For non-sensitive bugs, provide the Windows and application versions and sanitized reproduction steps.

The original macOS project's security documentation is retained in [docs/UPSTREAM-SECURITY.md](docs/UPSTREAM-SECURITY.md); its feature and storage descriptions do not automatically apply to this Windows interface.
