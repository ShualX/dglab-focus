# 接入调研 · 2026-10-21

官方旧仓库已转到 [dungeonlab-open/dglab-websocket-simple](https://github.com/dungeonlab-open/dglab-websocket-simple)，README 指向新的 TypeScript / Python SDK 与中继。新的 [DGLAB KIT](https://github.com/dungeonlab-open/dglab-kit) 面向 DG-LAB 4 App，建议新接入使用 V4，V3 用于旧控制器兼容。主机代际、App 版本与 Socket 协议版本需要分别核实。

2026-10-22 根据 [官方 V4 跨语言协议说明](https://github.com/dungeonlab-open/dglab-kit/blob/main/README_CN.md#v4-协议参考)实现设备控制。使用临时强度、波形、任务清理与绝对归零操作，处理设备快照、状态增量和 RPC 响应。每次任务限定 1000 毫秒，依赖 App 约定的任务到期归零机制。设备尚未到货，当前验证使用本机模拟 App。

支持范围为 DG-LAB 4 App + `COYOTE_030`。操作同时指定 App 的 `clientId` 和主机的 `slotId`，通道使用 0 / 1。控制器匹配来源和请求 ID，并使用 App 的通道上限限制授权。未复用官方 GPL SDK 的实现代码或内置波形资源，均匀帧按公开线协议独立构造。

v0.4.1 按 [官方 V4 中继消息说明](https://github.com/dungeonlab-open/dglab-websocket-server/blob/main/README_CN.md#v4-协议)独立实现内置局域网中继。控制端获得随机 ID，手机通过 `tid` 加入；中继定向转发 `data`，为手机上报附加可信来源 ID，并通知断连。二维码外层保持官方 `https://dungeon-lab.cn/s/?v=1&action=socket&url=...` 格式，内部换成所选电脑地址。没有复用官方中继源代码。

| 相关项目 | 核对内容 | 与本项目的关系 |
| --- | --- | --- |
| [CodeShock](https://github.com/Laceprndpm/CodeShock) | README 与项目目录 | 算法题评测事件联动，含模拟模式，检测目标不同 |
| [DuolingoXdg-lab](https://github.com/Soul-Charge/DuolingoXdg-lab) | README | 多邻国错误答案联动 |
| [DGLabMouseListener](https://github.com/lxyddice/DGLabMouseListener) | README | Windows 键鼠事件联动，无完整专注规则 |
| [DG-Lab-Coyote-Game-Hub](https://github.com/hyperzlib/DG-Lab-Coyote-Game-Hub) | README 与 docs/api.md | 设备控制面板和第三方 API |

本次检索没有找到完整覆盖前台规则、分心恢复、滚动一小时、单次临时域名和五分钟退出等待的现成 Windows 项目。未运行上述项目，也未复用其代码。

Win32 通过 [GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) 获取前台窗口，通过 [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo) 获取最近输入时间，无需键盘内容钩子。

软件选择使用 [EnumWindows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows) 枚举窗口，再用 [QueryFullProcessImageName](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew) 和受限查询权限读取完整程序路径。前台识别与选择共用路径解析，托管 Store 应用仅在能解析到实际应用进程时返回结果，避免把宿主程序作为通用放行项。窗口捕获通过 [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook) 监听前台激活事件，使用进程外回调，取消后解除监听。没有键盘钩子或输入内容采集。

[Chrome Tabs API](https://developer.chrome.com/docs/extensions/reference/api/tabs) 的 active 标签与窗口 focused 不等价。扩展报告窗口焦点，桌面端再结合 Win32 前台进程判断。多个配置文件同时报告焦点时，当前版本选择未知，避免猜测。MV3 的 WebSocket 保活依据 [Chrome 官方说明](https://developer.chrome.com/docs/extensions/how-to/web-platform/websockets)，每 5 秒发送报告并保留重连闹钟。

v0.4.3 按 [Chrome 的替代安装方式](https://developer.chrome.com/docs/extensions/how-to/distribute/install-extensions)与 [Edge 的分发说明](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/alternate-distribution-options)设计软件内安装辅助。扩展尚未上架商店，采用普通用户的开发者模式加载，由用户在浏览器确认。软件只准备自己固定目录中的文件和自动配对配置，生成常规管理页启动参数。没有注册强制安装策略、修改浏览器配置文件或调用移除的扩展加载开关。测试不会打开用户浏览器的扩展管理页。
