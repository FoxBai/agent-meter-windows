# Agent Meter for Windows 快速使用

1. 解压后保留完整文件夹，运行 `AgentMeter.Windows.exe`。
2. 在 Windows 通知区域找到 Agent Meter 图标；如果没有看到，可展开隐藏图标。
3. 左键显示或隐藏面板，右键可刷新或退出。

本机会话较多时首次扫描可能较慢。账户额度和本机 Token 分别统计；未提供的数据不会当作零。运行 `AgentMeter.Windows.exe --demo` 可查看虚构演示数据；运行 `AgentMeter.Windows.exe --window` 可用普通任务栏窗口查看真实数据。

Codex 额度查询使用本机已有的 Codex 安装和登录状态。Windows 界面读取支持的本机历史，不通过该界面上传历史记录。详情见项目首页及 `THIRD_PARTY_NOTICES.md`；依赖许可文本位于 `licenses/`。

项目地址：<https://github.com/FoxBai/agent-meter-windows>
