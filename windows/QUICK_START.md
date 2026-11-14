# Agent Meter for Windows 快速使用

1. 解压后保留完整文件夹，运行 `AgentMeter.Windows.exe`。
2. 在 Windows 通知区域找到 Agent Meter 图标；如果没有看到，可展开隐藏图标。
3. 左键显示或隐藏面板，右键可刷新、退出，或在「账户查询」中分别关闭 Codex、Kimi、Claude 额度读取。开关只影响账户查询，不影响本机历史。
4. 右键菜单「界面风格」可选择七种配色；当前 Windows 版的材质和动效仍在完善。
5. 右键菜单「自定义模型价格」可覆盖内置美元价格，单位是每百万 Token。价格估算不是实际账单；损坏的价格文件会导致金额隐藏。

本机会话较多时首次扫描可能较慢。账户额度和本机 Token 分别统计；未提供的数据不会当作零。运行 `AgentMeter.Windows.exe --demo` 可查看虚构演示数据；运行 `AgentMeter.Windows.exe --window` 可用普通任务栏窗口查看真实数据。

账户查询默认开启，开关状态保存在 `%LOCALAPPDATA%\AgentMeter\preferences.json`，不包含凭据。Codex 额度查询使用本机已有的 Codex 安装和登录状态。如果 Claude Code 的本地订阅凭据文件存在，程序会在内存中读取访问令牌并通过 HTTPS 向 Anthropic 用量接口查询额度，不修改或刷新登录；该功能目前仅经模拟测试。Kimi 额度查询只连接已运行的本机 Kimi 服务，目前同样仅经模拟测试。关闭来源时程序会尝试中止当前账户查询，已发出的请求无法撤回。Windows 界面读取支持的本机历史，不通过该界面上传历史记录。详情见项目首页及 `THIRD_PARTY_NOTICES.md`；依赖许可文本位于 `licenses/`。

项目地址：<https://github.com/FoxBai/agent-meter-windows>
