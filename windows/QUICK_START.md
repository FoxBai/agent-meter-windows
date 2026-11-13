# Agent Meter for Windows

1. Keep the extracted folder intact and start `AgentMeter.Windows.exe`.
2. Find the Agent Meter icon in the notification area. Expand the hidden-icons arrow if necessary.
3. Left-click to open or hide the panel. Right-click for refresh and quit.

The first scan can take a while when this computer has many agent sessions. The panel shows account limits separately from local token history. Missing information is not zero. For a fictional preview, run `AgentMeter.Windows.exe --demo`; for a standard taskbar window with live data, run `AgentMeter.Windows.exe --window`.

The app uses the existing Codex installation and login to query Codex account quota. It reads supported local agent histories without uploading them through the Windows interface. See the project README and `THIRD_PARTY_NOTICES.md` for details. License texts are in `licenses/`.

项目说明与源码：<https://github.com/FoxBai/agent-meter-windows>
