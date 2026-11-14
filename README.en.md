<div align="center">

# Agent Meter for Windows

**AI coding usage, one glance away.**

A Windows system-tray companion for account limits and local agent usage, built on [Gary23333/agent-meter](https://github.com/Gary23333/agent-meter).

[简体中文](README.md) · [Getting started](#getting-started) · [Privacy](#privacy-and-data) · [Contributing](#contributing)

[![Windows build](https://github.com/FoxBai/agent-meter-windows/actions/workflows/windows-port.yml/badge.svg)](https://github.com/FoxBai/agent-meter-windows/actions/workflows/windows-port.yml) [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

</div>

> **Early Windows port.** The app is under active development. The Windows release does not yet contain every provider, feature or interface from the original macOS project.

![Agent Meter panel with fictional demo data](windows/assets/screenshot.png)

The screenshot shows an earlier Alpha interface with fictional demo data; the current source has changed. Actual availability depends on the agent, account, local files and version.

## What it does

- **System tray panel** — left-click the tray icon to show or hide the panel; right-click to open, refresh or quit. Closing the panel keeps the app in the tray.
- **Account switches** — enable or disable Codex, Kimi and Claude account lookups independently from the tray menu. Local history is unaffected. The per-user preferences contain only switches, never credentials.
- **Appearance** — choose from seven named palettes and card styles in the tray menu; the choice is remembered. The Windows port does not yet reproduce all macOS materials or animations, and visual review is ongoing.
- **Codex account** — shows quota windows, reset times, credits and reset cards when the Codex account service returns them. A missing value is not treated as zero.
- **Kimi Code account** — reads an already-running local Kimi service without launching it; validated with a simulated server, not yet with a real Windows account.
- **Claude Code account** — queries Anthropic's usage endpoint when an existing local subscription credential file is present, without refreshing or changing the login. Tested with simulated credentials and responses only.
- **Local usage** — reads supported session ledgers and displays today, 7-day, 30-day and all-time token totals. Qoder CN local credits are shown separately and never added to tokens. Account quota and local usage are separate measures.
- **Model ranking and daily trend** — ranks models over the past 7×24 hours and charts the latest seven calendar days in Beijing time. Incomplete sources are flagged; separate local ledgers can overlap.
- **API-equivalent estimates** — shows today's USD estimate for models matched to the upstream built-in price snapshot. Missing prices, breakdowns or peak/off-peak information are flagged. Prices may be outdated; some CNY prices use the fixed fallback rate of 7.25 CNY per USD. This is neither subscription spend nor a bill. Price editing and live updates are not yet available in the Windows interface.
- **Background refresh** — account data can appear while history is still being scanned. Manual refresh and a 15-minute timer are available. Parsed usage is cached in memory while the app is running; changed files are reread.

## Data sources

The table describes the current `main` branch. The published alpha.1 package predates appearance options, account switches, Claude account support, the newly added Kimi, OpenCode, MiniMax Code, MiMo Code, DevECo and Qoder CN readers, the 7/30-day views, model ranking, trend chart and price estimates.

| Source | Account quota | Local token history |
| --- | --- | --- |
| Codex | Quota and reset time; credits and reset cards when available | Codex sessions and archives |
| Claude Code | Existing local subscription credential (synthetic validation) | Local project sessions |
| Gemini CLI | Not yet | Local chat sessions |
| WorkBuddy | Not yet | Local project sessions |
| ZCode | Not yet | Local CLI usage database |
| DeepSeek Harness | Not yet | Local session logs, including supported compressed logs |
| Kimi Code / legacy Kimi CLI | Existing Kimi Code local server (synthetic validation) | Local session logs |
| OpenCode | Not yet | Local v1 message database; v2 records are flagged as incomplete |
| MiniMax Code, MiMo Code, DevECo | Not yet | Local usage databases |
| Qoder CN | Not yet | Local credits consumed, not token usage or account balance |

The new Kimi, OpenCode, MiniMax Code, MiMo Code, DevECo and Qoder CN readers have synthetic Windows tests; they have not been validated against real installations on this Windows machine. OpenCode's v2 messages are not yet counted. The application reports only data it can read. A provider or account may omit fields or use an unsupported format. Local history is limited to records on this computer; it is not necessarily the same as account-wide usage, invoices, or remaining plan quota. Period boundaries currently use **Asia/Shanghai**. Credits from different services are not combined.

## Getting started

### Requirements

- Windows x64, with the agents you want to inspect installed and configured. This port has been exercised on Windows build 26300; other versions and Windows ARM64 still need validation.
- To build: Python 3.10 and the .NET 8 SDK. The resulting self-contained bundle includes its Python backend and .NET runtime.
- For Codex account quota, a working, signed-in `codex.exe` must be on `PATH` or in the Codex Desktop installation directory. No account login is performed by Agent Meter.

### Download the preview

Download [Windows v0.1.0-alpha.1](https://github.com/FoxBai/agent-meter-windows/releases/tag/windows-v0.1.0-alpha.1), extract `AgentMeter-Windows-x64.zip`, and run `AgentMeter.Windows.exe` from the extracted folder. The portable bundle includes the required runtime. The preview is not code-signed; the release page includes a SHA-256 checksum. The complete tray lifecycle and a separate clean physical installation have not yet been verified.

### Build from source

In PowerShell from the repository root:

```powershell
py -3.10 -m venv .venv-win
.\.venv-win\Scripts\python.exe -m pip install -r windows\requirements.txt
powershell -NoProfile -ExecutionPolicy Bypass -File windows\build.ps1
.\windows\dist\AgentMeter\AgentMeter.Windows.exe
```

Keep the complete `AgentMeter` output folder together. Find the icon in the Windows notification area; Windows may place it under the hidden-icons arrow. The first local scan can take time when there are many sessions. Later refreshes reuse the in-memory cache, subject to its size limit and source changes.

For a synthetic preview without reading accounts or local history:

```powershell
.\windows\dist\AgentMeter\AgentMeter.Windows.exe --demo
```

`--window` opens a normal taskbar window for troubleshooting with real data. It is not required for ordinary tray use. To stop the app, right-click the tray icon and choose **退出** (Quit).

### Tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows\test.ps1
dotnet build windows\AgentMeter.Windows -c Release
```

These are targeted tests, not a claim that every upstream macOS feature is covered on Windows. Some upstream suites require additional tools or contain platform-specific assumptions.

## Privacy and data

The Windows adapter reads local session files and usage databases; it derives usage metrics rather than displaying conversation text. Parsed usage is held in a bounded in-memory cache and is discarded when the app exits. The Windows interface does not upload this history or require an Agent Meter account. Account lookups are enabled by default and can be disabled individually through the tray menu; switch states are saved without credentials in `%LOCALAPPDATA%\AgentMeter\preferences.json`. Codex account refresh starts the locally installed Codex app-server, which uses the existing Codex login and may contact the provider. If a Claude Code subscription credential file exists, the app reads its access token in memory and sends it over HTTPS to Anthropic's usage endpoint; it does not refresh, modify or save that token. Disabling a source attempts to stop an ongoing account lookup; a request already sent cannot be recalled. The app does not ask you to paste credentials and does not initiate model prompts or credit redemption.

This Windows port is independent of the optional reward-claiming features in the upstream macOS application; those controls are not exposed by the Windows interface. Check the code and the provider's own policies if your environment has restrictions on local session access.

## Troubleshooting

- **No tray icon:** open the hidden-icons menu in the notification area. Start with `--window` to inspect the panel.
- **No Codex quota:** confirm `codex.exe` is available and signed in, then refresh. A network or service failure can also make account data unavailable.
- **No local history:** run the agent on this Windows account and check the corresponding local history exists. `CODEX_HOME`, `CLAUDE_CONFIG_DIR`, `DSH_HOME`, `KIMI_CODE_HOME`, `XDG_DATA_HOME` and `OPENCODE_DB` can override the corresponding default paths.
- **First refresh is slow:** the first scan parses existing files. Later refreshes are generally faster while the app remains running; changing files, cache eviction or very large histories can still take time.
- **Values differ from a provider dashboard:** local history and account-wide reporting have different scope and timing. A missing or unsupported field remains unavailable rather than being assumed to be zero.

## Development and attribution

The native Windows interface and adapter are in [`windows/`](windows/). The Python collectors in [`agent_meter/`](agent_meter/) are adapted from [Gary23333/agent-meter](https://github.com/Gary23333/agent-meter), baseline commit [`ac547d0`](https://github.com/Gary23333/agent-meter/commit/ac547d0dd56031cad70c3f0adf6266ce96a69bf9). The original copyright notice and MIT license are retained in [LICENSE](LICENSE); see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for upstream notices. This is an independent port, not an official release of the original project or of any agent provider.

## Contributing

Issues and contributions are welcome. Please report Windows version, app version, the source affected, expected behavior and sanitized steps to reproduce. Never include credentials, session transcripts or private account data in an issue. Run the targeted tests above and describe any platform or provider you could not verify.

## License

[MIT](LICENSE), with original attribution to Gary23333. Third-party components retain their own licenses.
