# MiTVSwitch

给**小米 / 红米电视**和**内置澎湃 OS 的智能显示器**做「一键切信号源」的小工具，**macOS** 和 **Windows** 双平台。

一个快捷键把显示器从电脑切到游戏机，再一个快捷键切回来。不用找遥控器，不用翻 OSD 菜单，不用改接线。

| 平台 | 形态 | 快捷键 |
|---|---|---|
| macOS | 菜单栏 App（Swift / AppKit） | `⌃⌥1` / `⌃⌥2` |
| Windows | 托盘 App（C# / WinForms） | `Ctrl+Alt+1` / `Ctrl+Alt+2` |

> **授权**：**[GPL-3.0](LICENSE)** —— 必须署名 · 衍生作品必须开源且同协议 · **允许商用**。详见 [License](#license)。

---

## 这个项目解决什么问题

典型场景：一台显示器同时接游戏机和电脑。

- 显示器自带的「自动切换信号源」经常判断不准，两个设备都待机时会来回跳
- 电脑休眠 / 关屏时，显示器判定无信号进入待机，通过 HDMI-CEC 广播 System Standby，**把游戏机一起关机了**
- 想手动切，得长按遥控器菜单键 → 进「信号源」→ 选中对应接口，三四步操作
- 有些显示器的遥控器按键能一键切源，但那只遥控器往往还要拿去当电脑的语音输入设备

于是这个工具绕开遥控器和 OSD，直接走**局域网 HTTP 接口**控制显示器切源。按一个键，屏幕就跳过去。

---

## 原理

小米电视以及部分小米生态智能显示器，在 `6095` 端口跑着一个叫 **MiTV Assistant Server** 的 HTTP 服务（小米电视助手 App 用的就是它）。它提供一组简单接口：

| 请求 | 作用 |
|---|---|
| `GET /request?action=isalive` | 设备名、平台、固件版本 |
| `GET /controller?action=getsysteminfo` | 设备名、deviceid、MAC |
| `GET /controller?action=getsources` | **真实输入列表（编号 + 名字）** |
| `GET /controller?action=getvolume` | 当前音量 |
| `GET /controller?action=getinstalledapp` | 已安装应用列表 |
| `GET /controller?action=keyevent&keycode=home` | 模拟按键 |
| `GET /controller?action=changesource&source=HDMI1` | 按名字切换信号源 |
| **`GET /controller?action=changesource2&sourceid=29`** | **按编号切换，DP / USB-C 只能走这条** |

返回都是 JSON，成功时为：

```json
{ "status": 0, "msg": "success", "data": {} }
```

所以这个方案**不需要红外发射器、不需要 DDC/CI、不需要 HDMI-CEC**，只要能访问到设备的 6095 端口就行。

### 为什么按编号切，而不是按名字

`changesource&source=HDMI1` 这种按名字的写法，对 **DP 和 USB-C 会被设备拒绝**（返回 `msg:error`），因为它只认电视那套输入名。

`getsources` 会返回设备实际的口：

```json
{"sourceList":[{"sourceId":23,"sourceName":"HDMI1"},
               {"sourceId":24,"sourceName":"HDMI2"},
               {"sourceId":29,"sourceName":"DP"}]}
```

拿这个编号去调 `changesource2&sourceid=29` 就能切到 DP。编号沿用 MStar 电视输入表：**23 = HDMI1，24 = HDMI2，29 = DP，30 = USB-C**。老固件会把 29 报成 `DVI`，所以本项目按编号纠正名字。

程序启动时会自动拉一次这份列表；`getsources` 不可用的老固件会自动退回按名字切，功能照旧，只是 DP / USB-C 切不了。

### 判断你的设备支不支持

在终端 / PowerShell 里跑一条命令：

```bash
# macOS
curl -s --noproxy '*' "http://<设备IP>:6095/controller?action=getvolume"
```

```powershell
# Windows
$wc = New-Object System.Net.WebClient; $wc.Proxy = $null
$wc.DownloadString("http://<设备IP>:6095/controller?action=getvolume")
```

- 返回 `{"status":0,"msg":"success","data":{...}}` → **支持**
- 连不上 → 设备没开这个服务，或者不在同一网段

> 智能电视基本都支持；显示器则看型号，内置澎湃 OS（小米电视系统）的型号大概率支持。

---

## 快速开始

### 第一步：找到设备的局域网 IP

任选一种：

- 路由器后台的设备列表里找名字带 `mitv-`、`MiTV`、`Redmi` 的设备
- 米家 App 里的设备详情
- 终端里扫一下：macOS `arp -a | grep -i -E "mi|redmi"`，Windows `arp -a | findstr /i "mi redmi"`

### 第二步：装

#### macOS

需要 macOS 13+ 和 Xcode Command Line Tools（`xcode-select --install`）。

```bash
git clone https://github.com/IceNoproblem/MiTVSwitch.git
cd MiTVSwitch/macos
chmod +x build.sh
./build.sh

# 装到 /Applications（开机自启功能需要这个位置）
cp -R MiTVSwitch.app /Applications/
open /Applications/MiTVSwitch.app
```

编译不需要开发者账号，脚本用 ad-hoc 签名，本机自用完全够。

#### Windows

**方式一：直接用现成的 exe**

到 [Releases](https://github.com/IceNoproblem/MiTVSwitch/releases) 下载 `MiTVSwitch-win-x64.exe`，双击运行。自包含单文件，**不需要装 .NET 运行时**。

首次运行 Windows 可能弹 SmartScreen 提示（未知发布者），点「更多信息」→「仍要运行」即可。

**方式二：自己编译**

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)。

```bat
git clone https://github.com/IceNoproblem/MiTVSwitch.git
cd MiTVSwitch\windows
build.bat
```

产物在 `dist\win-x64\MiTVSwitch.exe`。

### 第三步：设置地址

第一次启动会自动弹出设置窗口，填入第一步拿到的 IP。

也可以从菜单里点「**自动搜索局域网设备…**」，工具会扫描本机所在网段，把找到的设备列出来让你选。

地址会保存在：

- macOS：`~/.config/mitvswitch/host`
- Windows：`%APPDATA%\MiTVSwitch\host.txt`

两个平台的命令行工具都读这个文件。

---

## 使用

### 菜单项

两个平台的菜单结构一致：

| 菜单项 | 说明 |
|---|---|
| HDMI 1 / HDMI 2 | 切信号源，带快捷键提示 |
| DisplayPort / USB-C | 其他接口；设备没这个口时会标注「设备无此输入」 |
| **音量 ▸** | 音量 + / 音量 − / 查看当前音量（走局域网遥控，不依赖 DDC） |
| **画面（软件调光 / 色温）▸** | 亮度 100/85/70/55%、色温 6500K/5000K/4000K、恢复原始画面 |
| **切换 320Hz 竞技模式** | 双模显示器在 4K 160Hz 与 1080p 320Hz 之间切换 |
| 睡眠时切到「第一个源」 | 勾选后，电脑休眠自动切到第一个源，唤醒后切回第二个 |
| 设置显示器地址… | 手动填 IP |
| 自动搜索局域网设备… | 扫描同网段，显示器会额外标注「（显示器）」 |
| 测试连接 | 打印设备名、信号源列表及编号，确认地址对不对 |
| 查看日志 | 打开日志文件 |
| 开机自动启动 | macOS 走登录项，Windows 走注册表 Run 键 |
| 退出 | |

macOS 点菜单栏的 `▭▭` 图标；Windows 右键托盘图标。

### 全局快捷键

| 功能 | macOS | Windows |
|---|---|---|
| 切到第 1 个源 | `⌃⌥1` | `Ctrl+Alt+1` |
| 切到第 2 个源 | `⌃⌥2` | `Ctrl+Alt+2` |
| 亮度 + / − | `⌃⌥↑` / `⌃⌥↓` | `Ctrl+Alt+↑` / `Ctrl+Alt+↓` |
| 音量 + / − | `⌃⌥⇧↑` / `⌃⌥⇧↓` | `Ctrl+Alt+Shift+↑` / `Ctrl+Alt+Shift+↓` |

macOS 用 Carbon `RegisterEventHotKey`，Windows 用 `RegisterHotKey`，**都不需要辅助功能权限**，也不会抢焦点。即使显示器当前显示的是游戏机画面、你看不到电脑桌面，在键盘上按下去照样生效。

### 音量

音量走的是设备的局域网遥控按键（`keyevent&keycode=volumeup`），接口只支持 ±1，所以一次按两下。

`getvolume` 读到的音量是 `volume / maxVolume`，例如 `21 / 100`。

### 软件调光与色温

改的是**显卡的 Gamma 查色表**，不动显示器背光，所以任何连接线、任何显示器都能用：

- macOS：`CGSetDisplayTransferByTable`
- Windows：`SetDeviceGammaRamp`

两者都在退出时恢复原始曲线，唤醒后重新套用（系统有时会重置 Gamma）。

代价是压得越暗对比度越低——这和真正的背光调节不是一回事。如果你的显示器支持 DDC/CI，硬件背光效果更好，但那不在本项目范围内。

色温用开尔文近似的 RGB 增益，并归一化到最大通道为 1，所以只改色不改整体亮度。

### 320Hz 竞技模式

REDMI G Pro 27U 这类双模显示器在 4K 160Hz 与 1080p 320Hz 之间切换需要换 EDID，原本只能进显示器菜单操作。**没有对应接口**，所以这里走遥控按键宏：

```
menu → right × 5 → enter → right（开）/ left（关）→ back × 2
```

切换后显示器会黑一下再回来，系统需要几秒重新识别。菜单里点一次，选「切换到 320Hz」或「切回 4K 160Hz」。

### 命令行

#### macOS

仓库里的 `macos/mitsw` 是独立脚本，不依赖 App：

```bash
chmod +x mitsw
./mitsw hdmi1          # 切到 HDMI 1
./mitsw hdmi2          # 切到 HDMI 2
./mitsw dp             # 切到 DisplayPort（按编号，能切）
./mitsw usbc           # 切到 USB-C
./mitsw raw HDMI3      # 发送自定义源名
./mitsw sources        # 列出设备报告的输入及编号
./mitsw id dp          # 查某个源名在设备上的编号
./mitsw status         # isalive + 信号源 + 系统信息
./mitsw vol            # 查看音量
./mitsw vol+ / vol-    # 音量加减
./mitsw comp on|off    # 320Hz 竞技模式
```

#### Windows

`windows/switch.ps1` 同样不依赖编译：

```powershell
.\switch.ps1 hdmi1
.\switch.ps1 dp
.\switch.ps1 sources
.\switch.ps1 id dp
.\switch.ps1 status
.\switch.ps1 vol+
.\switch.ps1 comp on
```

想把它绑成真正的全局快捷键，可以用 AutoHotkey 包一层：

```autohotkey
^!1::Run, powershell -WindowStyle Hidden -File "C:\path\to\switch.ps1" hdmi1
^!2::Run, powershell -WindowStyle Hidden -File "C:\path\to\switch.ps1" hdmi2
```

或者给 `switch.ps1` 建个快捷方式，在「属性 → 快捷键」里设组合键。

---

## 自定义信号源

### macOS

打开 `macos/main.swift`，改顶部的 `allSources`：

```swift
let allSources: [TVSource] = [
    TVSource(key: "HDMI1", title: "游戏机",    hotKeyCode: UInt32(kVK_ANSI_1), hotMods: UInt32(controlKey | optionKey)),
    TVSource(key: "HDMI2", title: "Mac",      hotKeyCode: UInt32(kVK_ANSI_2), hotMods: UInt32(controlKey | optionKey)),
    TVSource(key: "DP",    title: "显示器 DP",  hotKeyCode: 0, hotMods: 0),
]
```

### Windows

打开 `windows/TrayApp.cs`，改顶部的 `Sources`：

```csharp
private static readonly Source[] Sources =
{
    new Source { Key = "HDMI1", Title = "游戏机", Hotkey = Keys.Control | Keys.Alt | Keys.D1 },
    new Source { Key = "HDMI2", Title = "电脑",   Hotkey = Keys.Control | Keys.Alt | Keys.D2 },
    new Source { Key = "DP",    Title = "DisplayPort" },
};
```

两边规则一样：

- `key` / `Key`：源名，**大小写不敏感**。程序启动时会用 `getsources` 把它解析成设备上的编号，再走 `changesource2`
- `title` / `Title`：菜单里显示的名字
- 热键写 `0` / `Keys.None` 表示不绑
- **数组第 1、2 项同时是「睡眠切换」的目标**（睡眠切到第 1 项，唤醒切回第 2 项）

> 加了自定义源名却没生效？先用 `./mitsw id <源名>` 或 `.\switch.ps1 id <源名>` 看设备认不认这个口——认不出会明确告诉你「设备没有这个输入」，并列出真实可用的口。

改完 macOS 重新 `./build.sh`，Windows 重新 `build.bat` 即可。

---

## 排障

### 连接不上，但 `curl` 明明能通

**最常见的坑：系统代理。** 两个平台都会踩。

系统代理的「忽略这些主机 / 例外列表」**往往不支持 CIDR 网段**——写 `192.168.0.0/16` 是无效的，系统会把它当一串普通字符。结果发往 `192.168.1.x` 的请求照样被塞进代理（比如 Clash 的 `127.0.0.1:7890`），然后死在代理里。

表现很有迷惑性：**公网能通、局域网不通**，容易被误判成权限问题。

本项目的处理方式：

- **macOS 版完全不用 `NSURLSession`**，改用裸 BSD socket（`socket` + `connect` + `send/recv`）自己拼 HTTP/1.0 请求，从根上绕开代理层
- **Windows 版**给 `HttpClientHandler` 设 `UseProxy = false`
- 命令行版则是 `curl --noproxy '*'` / `WebClient.Proxy = $null`

### 开机后第一次切换失败（macOS）

冷启动时 ARP 缓存是冷的，`connect()` 会立刻返回 `EHOSTUNREACH (65)`，看起来像设备不在线。工具内置了 3 次递增退避重试，通常一下就过去了。

### 提示「设备没有这个输入」

新版本会先读 `getsources`，所以点了设备上不存在的口会直接告诉你：

```
设备没有提供「USBC」这个输入。
当前可用：HDMI1、HDMI2、DP
```

这通常不是 bug，而是这台设备真的没这个接口（比如 G Pro 27U 只有两个 HDMI 和一个 DP，没有 USB-C）。

想确认设备到底报了什么：

```bash
./mitsw sources        # 或 .\switch.ps1 sources
```

```
信号源列表：
  HDMI1    编号 23
  HDMI2    编号 24
  DP       编号 29
```

`getsources` 本身取不到（老固件）时，会退回按名字切，此时：

- `HDMI1` / `hdmi1` / `HDMI2` 都行
- `HDMI_2` 不行（下划线不被接受）
- DP / USB-C 基本切不动——这正是按编号切换要解决的问题

顺带区分两种失败：`changesource` 对无效源名返回 `{"status":0,"msg":"error"}`，而完全不存在这个 action 时返回空 body 的 404，两者容易混淆。

### 音量键 / 亮度键没反应

- 音量走局域网遥控，**设备必须在线**，离线时按了不会报错也不会生效
- 亮度是软件调光，改的是本机 Gamma，与显示器无关；如果画面没变化，看菜单里「画面」的勾选是否真的变了
- Windows 上有些显卡驱动会拒绝偏离标准曲线太远的 Gamma（最暗大约只到 50%），此时程序会在日志里记「显卡不接受」

### 为什么不用 DDC/CI

DDC/CI 是显示器切源的标准做法，`m1ddc` / BetterDisplay 都走这条。但：

- **Apple Silicon 的 HDMI 口不开放 DDC 的 I²C 通道**。实测接 HDMI 时，`ioreg -c IOAVService` 返回空，任何 VCP 读取（亮度/对比度/音量）都直接失败
- 只有 DisplayPort / USB-C 链路才有机会通
- REDMI 这批澎湃 OS 显示器本身就不支持 DDC/CI

本项目因此没做 DDC/CI：切源和音量走局域网接口，亮度色温走 Gamma 表，两条路都不依赖显示器是否支持 DDC。

如果你本来就是 DP 或 USB-C 连接且显示器支持 DDC/CI，那 DDC 方案更通用（能调硬件背光），值得优先试。

### 为什么不用 HDMI-CEC

方向反了。CEC 是让设备之间互相通知，不是让你从电脑精确指定「切到第几个输入口」。而且 PC 本身不发送 CEC 命令。

顺带一提：如果你的显示器接游戏机，**进显示器设置里关掉「CEC 一键关机 / 设备自动关机联动」，保留「CEC 开机联动」**，就能解决「电脑休眠把游戏机一起关掉」的问题——这跟切源是两件事，建议一起处理。

---

## 已知限制

- **USB-C 只有带这个口的机型才有**，工具会如实报告「设备没有这个输入」，不是切换失败
- **320Hz 竞技模式的按键序列只在 REDMI G Pro 27U 上验证过**，其他双模机型的游戏栏菜单层级可能不同，需要调 `menu` 后的右移次数
- 软件调光压暗后对比度会下降，它替代不了硬件背光
- 不做 DDC/CI：REDMI 这批澎湃 OS 显示器不支持，Apple Silicon 的 HDMI 口也不通 I²C
- 不做屏幕时间统计、分辨率切换、自动更新——这些属于另一个项目（见「致谢」）的范围
- `action=capturescreen`（截屏）在 HDMI 输入下无效，因为此时 TV 系统在后台
- 部分设备的 ADB(5555)、Android TV Remote(6466/6467) 是关的，本工具不依赖它们
- 小米电视助手协议是非公开接口，**系统更新后可能发生变化**。真改了就退回 `keyevent` 模拟按键
- Windows 版与 macOS 版逻辑一致，但尚未在大量机型上长期验证，遇到问题欢迎提 issue
- macOS 版走「菜单栏常驻」形态，没有 Dock 图标

## 从源码构建 / CI

仓库带了 GitHub Actions（`.github/workflows/build.yml`）：

- 推到 `main` 或提 PR 时，自动在 `windows-latest` 和 `macos-latest` 上编译，产物上传为 artifact
- 打 `v*` 标签时，自动创建 Release 并附上两个平台的产物

```
MiTVSwitch/
├── macos/
│   ├── main.swift        菜单栏 App（AppKit + Carbon 热键 + Gamma 调光）
│   ├── Info.plist
│   ├── build.sh          swiftc 一键编译打包
│   └── mitsw             命令行工具
├── windows/
│   ├── Program.cs        入口 + 单实例
│   ├── TrayApp.cs        托盘图标、菜单、热键、动作
│   ├── HotkeyWindow.cs   WM_HOTKEY / WM_POWERBROADCAST 接收窗口
│   ├── TvClient.cs       6095 接口调用 + 局域网发现 + 编号解析
│   ├── Gamma.cs          软件调光 / 色温（SetDeviceGammaRamp）
│   ├── Settings.cs       配置读写 + 开机自启
│   ├── Prompt.cs         两个小对话框
│   ├── MiTVSwitch.csproj
│   ├── build.bat
│   └── switch.ps1        命令行工具
├── NOTICE                第三方成果归属声明
└── .github/workflows/build.yml
```

## 安全性

- 工具只在局域网内发 HTTP GET，不联网、不上报
- 设备端无鉴权：能访问到 6095 端口的人都能控制它。**不要把 6095 端口映射到公网**
- 日志只记请求结果，不含任何隐私数据
  - macOS：`~/Library/Logs/MiTVSwitch.log`
  - Windows：`%APPDATA%\MiTVSwitch\MiTVSwitch.log`

---

## 致谢

- **[MonitorBar](https://github.com/Binaryify/MonitorBar)**（MIT，© 2026 binaryify）—— 本项目从它这里学到了三件关键的事：`getsources` / `changesource2` 这套按编号切源的接口、29/30 的输入编号约定、以及双模显示器切换 320Hz 的按键宏。软件调光与色温的做法也参考了它的设计。本项目用 Swift 与 C# 重新实现，未复制其 Go 源码。完整归属声明见 [NOTICE](NOTICE)。
- 图标与交互沿用 macOS / Windows 原生形态，没有引入第三方 UI 框架。

## 免责声明

非官方项目，与小米公司无任何关联。使用自有设备，风险自负。接口为非公开协议，可能随时变更。

## License

本项目使用 **[GPL-3.0](LICENSE)**（GNU General Public License, Version 3, 29 June 2007）。

| 要求 | GPL-3.0 的规定 |
|---|---|
| **必须署名** | ✅ 分发时必须保留版权声明与作者署名；修改过的版本必须显著标注「已修改」及修改日期 |
| **必须开源** | ✅ 对外分发（含修改版、二进制版）都必须提供**完整对应源代码**，且同样以 GPL-3.0 授权，不允许改成闭源 |
| **允许商用** | ⚠️ GPL-3.0 **不禁止**商业使用。它约束的是「分发时是否开源」，不是你「能不能靠它赚钱」 |
| **专利授权** | ✅ 贡献者被视为向使用者授予专利许可，不能事后拿专利反过来起诉使用者 |
| **无担保** | 作者不提供任何担保，使用风险自负 |

完整条款见 [LICENSE](LICENSE)，官方原文见 [gnu.org/licenses/gpl-3.0](https://www.gnu.org/licenses/gpl-3.0.html)。
