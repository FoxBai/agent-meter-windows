# Windows port plan

Source baseline: Gary23333/agent-meter v0.11.0, commit ac547d0dd56031cad70c3f0adf6266ce96a69bf9, MIT license. Retain the original copyright and license, document modifications and third-party notices.

## Objective and acceptance

Build a usable Windows application inspired by the original macOS app, with a native system tray icon and a clear, responsive panel. Support practical account quota, reset times, local token history and refresh status where the Windows data sources have actually been adapted and verified. Preserve the distinction between account quota and per-device history; missing values must not become zero. Any unsupported original feature or provider must be explicitly identified, not represented as working.

Provide explicit refresh, background refresh with non-blocking UI, clear offline and error states, clean shutdown, Windows-appropriate data storage and a convenient distribution package. Avoid exposing secrets or session contents, and ensure the release repository contains no private runtime data. Test Windows paths, subprocess behavior, accounting, tray lifecycle, fresh-install startup and packaging; check actual UI and document the precise limits of any testing. Account access and network behavior should be explained clearly to users.

Create a public GitHub repository under the authenticated account after implementation and checks. Write a polished Chinese and English README covering purpose, screenshots from the actual product, supported platforms and providers, install and build instructions, permissions and privacy, data limitations, troubleshooting, attribution, license and contribution information. Do not publish claims or badges unsupported by evidence. Include a useful license, third-party notices, build configuration, and appropriate automated checks.

## Initial findings

- The original GUI uses SwiftUI/AppKit and cannot be directly used on Windows. Its Python collector contains macOS-specific application paths and Keychain integration; providers must be evaluated independently.
- Windows Python 3.10 requires the `tzdata` package for `zoneinfo`. Compressed session logs require an available zstd implementation.
- Initial upstream test run on Windows did not pass: missing zstd causes several errors, an upstream test assumes a missing test module, and other tests reveal Windows-specific file locking or path issues. Investigate each before claiming compatibility.
- GitHub CLI is authenticated as FoxBai. Upstream remains the `origin` remote in this local clone; never push this work to the upstream repository.

## Working prototype checkpoint

- `windows/AgentMeter.Windows/` contains a WinForms tray/panel prototype; `windows/backend.py` wraps upstream read-only collectors. Codex account quota and reset time are read through `codex app-server`; Codex, Claude Code, Gemini CLI, WorkBuddy, ZCode and DeepSeek Harness local histories have adapters, with platform-specific validation still needed. Treat this as a prototype, not a complete port of upstream's full provider and feature set.
- The panel has separate account and history sections. Account and history run in separate child processes so the account renders while slow local history scans run; closing the app cancels child processes. Demo data (`--demo`) and an actual-data window (`--window`) are available for inspection.
- Windows dependencies include `tzdata` and `zstandard`. `windows/build.ps1` builds a self-contained Windows x64 bundle. The frozen backend's account-only path returned an available quota on this computer and its empty-home path reported missing histories. Targeted synthetic tests and upstream source-normalization tests passed. A demo UI refresh, actual account-first display and eventual Codex local history were inspected; full tray operation, shutdown, independent accounting reconciliation, packaging on a clean machine, and broader provider coverage still need validation. Direct source collectors also reported available WorkBuddy, ZCode and DeepSeek Harness histories on this machine, but no totals were independently verified.
- A real local history scan took more than a minute on this machine. Each refresh starts a new backend and repeats the scan; improve performance and verify accuracy, particularly for large histories. Python logs and metrics must not be included in Git commits or public assets.
- Remaining work includes extending and documenting supported sources and original features, usability/accessibility and UI polish, custom icon, localization, robust failure handling, packaging and third-party license notices, comprehensive tests, actual screenshots, both READMEs, CI, secret audit, and creating and publishing the public repository.
