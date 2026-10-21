# 接入调研 · 2026-10-21

官方旧仓库已转到 [dungeonlab-open/dglab-websocket-simple](https://github.com/dungeonlab-open/dglab-websocket-simple)，README 指向新的 TypeScript / Python SDK 与中继。新的 [DGLAB KIT](https://github.com/dungeonlab-open/dglab-kit) 面向 DG-LAB 4 App，建议新接入使用 V4，V3 用于旧控制器兼容。主机代际、App 版本与 Socket 协议版本需要分别核实。

本版 V4 连接预览仅接收 `hello`、`client_attached`、`client_disconnected` 与设备快照，没有实现输出操作。扫描二维码时官方中继会参与配对，前台检测仍在本机运行。设备未到货，实机行为未知。

| 相关项目 | 核对内容 | 与本项目的关系 |
| --- | --- | --- |
| [CodeShock](https://github.com/Laceprndpm/CodeShock) | README 与项目目录 | 算法题评测事件联动，含模拟模式，检测目标不同 |
| [DuolingoXdg-lab](https://github.com/Soul-Charge/DuolingoXdg-lab) | README | 多邻国错误答案联动 |
| [DGLabMouseListener](https://github.com/lxyddice/DGLabMouseListener) | README | Windows 键鼠事件联动，无完整专注规则 |
| [DG-Lab-Coyote-Game-Hub](https://github.com/hyperzlib/DG-Lab-Coyote-Game-Hub) | README 与 docs/api.md | 设备控制面板和第三方 API |

本次检索没有找到完整覆盖前台规则、分心恢复、滚动一小时、单次临时域名和五分钟退出等待的现成 Windows 项目。未运行上述项目，也未复用其代码。

Win32 通过 [GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) 获取前台窗口，通过 [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo) 获取最近输入时间，无需键盘内容钩子。

[Chrome Tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs) 的 active 标签与窗口 focused 不等价。扩展报告窗口焦点，桌面端再结合 Win32 前台进程判断。多个配置文件同时报告焦点时，当前版本选择未知，避免猜测。MV3 的 WebSocket 保活依据 [Chrome 官方说明](https://developer.chrome.com/docs/extensions/how-to/web-platform/websockets)，每 5 秒发送报告并保留重连闹钟。
