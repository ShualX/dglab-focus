# 郊狼自习室 · dglab-focus

Windows 专注工具。开始前配置允许的软件和网页，复习时检查前台窗口，累计分心时间，并在持续专注后逐渐消除。界面参考 DG-LAB 官网的黑色、浅金色和圆形控制盘，使用原生 Windows 窗口。

**当前发布为模拟预览版。** 计时、检测、悬浮球和浏览器桥接可以使用。DG-LAB 页面只提供 V4 App 配对与设备发现，不发送电刺激指令，也不会根据分心次数自动增加电刺激强度。实际主机与手机 App 尚未到货，未进行实机验证。

本项目由个人独立开发，与 DG-LAB / Dungeon Lab 无隶属关系。

![专注控制台](docs/images/focus.png)

截图使用示例学习域名。

## 下载

在 [Releases](https://github.com/ShualX/dglab-focus/releases) 下载 Windows x64 压缩包，完整解压后运行 `DglabFocus.exe`。包内带 .NET 运行时，无需单独安装 .NET。

当前支持 Windows 10 / 11 x64，开发与渲染检查在 Windows 11 上完成。浏览器扩展需要 Chrome / Edge 116 或以上版本。尚未上架扩展商店，需手动加载。

## 使用

1. 打开「允许使用」，选择软件的 `.exe`，或添加域名、具体页面、页面路径规则。
2. 在「会话设置」选择电脑复习、阅读 / 网课、纸笔复习。专注时长设为 `0` 表示无限。
3. 点击「开始专注」。悬浮球自动出现，点击展开可申请退出、取消退出、确认学习或立即暂停，也可拖动移动。
4. 普通退出需等待 5 分钟，期间继续复习。`Ctrl+Alt+F10`、主窗口、悬浮球和托盘菜单都可以立即暂停。若全局热键注册失败，界面会提示使用按钮。

固定专注默认 50 分钟，休息默认 10 分钟。默认完成一次专注与休息后结束，勾选循环则进入下一轮。循环、休息和取消退出都属于同一会话。

### Chrome / Edge

1. 打开 `chrome://extensions` 或 `edge://extensions`，启用开发者模式。
2. 点击「加载已解压的扩展」，选择下载包内的 `extension` 文件夹。
3. 在 Windows 软件的「设备与浏览器」复制本机配对码，粘贴到浏览器扩展弹窗，点击保存并连接。

Chrome 和 Edge 各加载一次。扩展只报告当前窗口的活动标签页地址与窗口焦点，不读取页面正文、密码或键入内容，不支持无痕窗口。

配置了任何网页规则后，浏览器 `.exe` 放行不会绕过网页规则。扩展断连、报告超过 12 秒、浏览器内部页面或多个浏览器配置文件同时报告前台时，状态视为未知，暂停累计与恢复。

### 临时查询

每个专注会话只能新增 **一个域名**，仅本次会话有效，不自动包含子域名。添加后不能替换。已经放行的域名不会占用名额。

休息、循环、取消退出、暂停和重启软件均不重置名额。重启后恢复上次会话时先暂停，确认继续后保留原规则和名额。普通规则在会话中锁定，下一次会话才能修改。

### 分心与无操作

离开允许内容后有 10 秒切换宽限，宽限结束后逐秒累计，前 10 秒不补计。持续分心只计一次，连续回到学习内容 5 秒后才可形成下一次分心。最近一小时次数按实际时间滚动统计，换会话也不会立即清零。

默认累计 60 秒触发屏幕提醒，可选提示音。持续专注 120 秒后，每专注 1 秒消除 0.5 秒累计时间。参数可在会话开始前调整。

电脑模式默认 180 秒无操作后请求学习确认。阅读 / 网课和纸笔模式默认每 15 分钟确认一次。未确认时只作屏幕提醒，不将缺少键鼠操作当作电刺激依据。纸笔模式暂停前台窗口判断。

锁屏、睡眠或检测线程停顿超过 5 秒会暂停会话，恢复后需本人确认，不补计停顿时间，也不补发反馈。当前工具不阻止任务管理器结束进程，不以修改系统权限阻止退出。

## 数据与连接

本机设置、当前会话与最近 200 条事件保存在 `%LOCALAPPDATA%\DglabFocus\settings.json`，采用临时文件替换方式保存。前台窗口每秒检查一次，状态每 5 秒保存一次，名额变化等关键操作立即保存。

完整网页地址只用于内存匹配，事件不保存浏览历史。记录会保存你主动配置的规则和临时域名。本机配对码也保存在该文件与浏览器扩展本地存储中，请勿公开配置文件。

浏览器桥接只监听 `127.0.0.1:17839`，校验扩展来源和随机配对码。不连接云端分析服务，不上传专注记录。

点击 DG-LAB 连接预览时才连接官方中继 `wss://trex.dungeon-lab.cn/v4`，交换配对和设备状态。网页地址和专注记录不进入该连接。该功能没有实机验证，旧版 App 兼容待确认。

## 构建和验证

需要 Windows、.NET 8 SDK 和 Node.js 22。运行时与二维码依赖来自 NuGet，首次构建需要联网。

```powershell
dotnet build src/Focus.Windows/Focus.Windows.csproj -c Release
dotnet run --project tests/Focus.Core.Tests -c Release
dotnet run --project tests/Focus.Windows.Tests -c Release
node --test tests/extension.test.js
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

运行界面渲染检查，不读取或写入个人设置、不连接设备：

```powershell
.\src\Focus.Windows\bin\Release\net8.0-windows\DglabFocus.exe --smoke-test --capture-dir D:\temp\dglab-focus-ui
```

当前验证覆盖 32 项计时与规则测试、10 项真实本机 WebSocket 桥接检查和 6 项扩展数据测试。另有五个页面、退出等待、悬浮球与紧凑窗口的 WPF 渲染检查。浏览器扩展尚未在用户的 Chrome / Edge 中手动安装测试，设备输出不属于本版验证范围。

## 结构

`src/Focus.Core` 保存纯规则与会话状态机。`src/Focus.Windows` 提供 WPF 界面、Win32 前台 / 空闲检测、本机 WebSocket 桥接和只读设备预览。`extension` 是 Manifest V3 浏览器扩展。后续实机接入应独立于检测引擎，未知状态、恢复与急停行为需重新验证。

## 参考与许可

[官方 DGLAB KIT 文档](https://github.com/dungeonlab-open/dglab-kit)与 [Win32 前台窗口 API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow)用于核对接入方式。类似项目的调研记录见 [docs/research.md](docs/research.md)。本项目代码采用 MIT 许可，第三方组件说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
