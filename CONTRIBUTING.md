# Contributing

Thanks for helping improve the Windows port. Please open an issue describing significant changes before starting a large redesign, so that data semantics and platform constraints can be discussed.

## Development setup

Use Windows x64, Python 3.10 and the .NET 8 SDK. From the repository root:

```powershell
py -3.10 -m venv .venv-win
.\.venv-win\Scripts\python.exe -m pip install -r windows\requirements.txt
powershell -NoProfile -ExecutionPolicy Bypass -File windows\test.ps1
dotnet build windows\AgentMeter.Windows -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File windows\build.ps1
```

The build output is in `windows/dist/AgentMeter/`. `--demo` uses synthetic display data; `--window` shows the live-data panel as a normal taskbar window. Document any platform, account or feature that was not actually tested.

Tests must use synthetic data and temporary paths. Preserve the distinction between account quota and local history, and do not turn absent or unsupported values into zero. Never commit credentials, transcripts, real account snapshots or private runtime data. Run `git diff --check` before submitting a change.

For changes to the original macOS interface or other upstream components, see the retained [upstream contribution guide](docs/UPSTREAM-CONTRIBUTING.md) and coordinate with [Gary23333/agent-meter](https://github.com/Gary23333/agent-meter) as appropriate. This Windows port is an independent fork.
