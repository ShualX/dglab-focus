# 接入调研 · 2026-10-21

官方旧仓库已转到 [dungeonlab-open/dglab-websocket-simple](https://github.com/dungeonlab-open/dglab-websocket-simple)，README 指向新的 TypeScript / Python SDK 与中继。新的 [DGLAB KIT](https://github.com/dungeonlab-open/dglab-kit) 面向 DG-LAB 4 App，建议新接入使用 V4，V3 用于旧控制器兼容。主机代际、App 版本与 Socket 协议版本需要分别核实。

2026-10-22 根据 [官方 V4 跨语言协议说明](https://github.com/dungeonlab-open/dglab-kit/blob/main/README_CN.md#v4-协议参考)实现设备控制。使用临时强度、波形、任务清理与绝对归零操作，处理设备快照、状态增量和 RPC 响应。每次任务限定 1000 毫秒，依赖 App 约定的任务到期归零机制。设备尚未到货，当前验证使用本机模拟 App。

支持范围为 DG-LAB 4 App + `COYOTE_030`。操作同时指定 App 的 `clientId` 和主机的 `slotId`，通道使用 0 / 1。控制器匹配来源和请求 ID，并使用 App 的通道上限限制授权。未复用官方 GPL SDK 的实现代码或内置波形资源，均匀帧按公开线协议独立构造。

| 相关项目 | 核对内容 | 与本项目的关系 |
| --- | --- | --- |
| [CodeShock](https://github.com/Laceprndpm/CodeShock) | README 与项目目录 | 算法题评测事件联动，含模拟模式，检测目标不同 |
| [DuolingoXdg-lab](https://github.com/Soul-Charge/DuolingoXdg-lab) | README | 多邻国错误答案联动 |
| [DGLabMouseListener](https://github.com/lxyddice/DGLabMouseListener) | README | Windows 键鼠事件联动，无完整专注规则 |
| [DG-Lab-Coyote-Game-Hub](https://github.com/hyperzlib/DG-Lab-Coyote-Game-Hub) | README 与 docs/api.md | 设备控制面板和第三方 API |

本次检索没有找到完整覆盖前台规则、分心恢复、滚动一小时、单次临时域名和五分钟退出等待的现成 Windows 项目。未运行上述项目，也未复用其代码。

Win32 通过 [GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) 获取前台窗口，通过 [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo) 获取最近输入时间，无需键盘内容钩子。

[Chrome Tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs) 的 active 标签与窗口 focused 不等价。扩展报告窗口焦点，桌面端再结合 Win32 前台进程判断。多个配置文件同时报告焦点时，当前版本选择未知，避免猜测。MV3 的 WebSocket 保活依据 [Chrome 官方说明](https://developer.chrome.com/docs/extensions/how-to/web-platform/websockets)，每 5 秒发送报告并保留重连闹钟。
