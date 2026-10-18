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
| `GET /controller?action=getsysteminfo` | 设备名、deviceid、MAC |
| `GET /controller?action=getvolume` | 当前音量 |
| `GET /controller?action=getinstalledapp` | 已安装应用列表 |
| `GET /controller?action=keyevent&keycode=home` | 模拟按键 |
| **`GET /controller?action=changesource&source=HDMI1`** | **切换信号源** |

返回都是 JSON，成功时为：

```json
{ "status": 0, "msg": "success", "data": {} }
```

所以这个方案**不需要红外发射器、不需要 DDC/CI、不需要 HDMI-CEC**，只要能访问到设备的 6095 端口就行。

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
| DisplayPort / USB-C | 其他接口（源名未在所有机型验证，见下） |
| 睡眠时切到「第一个源」 | 勾选后，电脑休眠自动切到第一个源，唤醒后切回第二个 |
| 设置显示器地址… | 手动填 IP |
| 自动搜索局域网设备… | 扫描同网段 |
| 测试连接 | 查看设备信息，确认地址对不对 |
| 查看日志 | 打开日志文件 |
| 开机自动启动 | macOS 走登录项，Windows 走注册表 Run 键 |
| 退出 | |

macOS 点菜单栏的 `▭▭` 图标；Windows 右键托盘图标。

### 全局快捷键

| 平台 | 切到第 1 个源 | 切到第 2 个源 |
|---|---|---|
| macOS | `⌃⌥1` | `⌃⌥2` |
| Windows | `Ctrl+Alt+1` | `Ctrl+Alt+2` |

macOS 用 Carbon `RegisterEventHotKey`，Windows 用 `RegisterHotKey`，**都不需要辅助功能权限**，也不会抢焦点。即使显示器当前显示的是游戏机画面、你看不到电脑桌面，在键盘上按下去照样生效。

### 命令行

#### macOS

仓库里的 `macos/mitsw` 是独立脚本，不依赖 App：

```bash
chmod +x mitsw
./mitsw hdmi1          # 切到 HDMI 1
./mitsw hdmi2          # 切到 HDMI 2
./mitsw dp             # 切到 DisplayPort
./mitsw usbc           # 切到 USB-C
./mitsw raw HDMI3      # 发送自定义源名
./mitsw status         # 查看设备信息
./mitsw vol            # 查看音量
```

#### Windows

`windows/switch.ps1` 同样不依赖编译：

```powershell
.\switch.ps1 hdmi1
.\switch.ps1 raw HDMI3
.\switch.ps1 status
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

- `key` / `Key`：传给 `changesource` 的源名，**大小写不敏感**
- `title` / `Title`：菜单里显示的名字
- 热键写 `0` / `Keys.None` 表示不绑
- **数组第 1、2 项同时是「睡眠切换」的目标**（睡眠切到第 1 项，唤醒切回第 2 项）

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

### 提示「源名不被设备接受」

`changesource` 对无效源名会返回 `{"status":0,"msg":"error"}`，而完全不存在这个 action 时返回空 body 的 404——两者容易混淆。

- `HDMI1` / `hdmi1` / `HDMI2` 都行
- `HDMI_2` 不行
- DP / USB-C 的源名各机型可能不同，逐个试：
  ```bash
  ./mitsw raw DP        # 或 .\switch.ps1 raw DP
  ./mitsw raw USBC
  ./mitsw raw DP1
  ```

### 为什么不用 DDC/CI

DDC/CI 是显示器切源的标准做法，`m1ddc` / BetterDisplay 都走这条。但：

- **Apple Silicon 的 HDMI 口不开放 DDC 的 I²C 通道**。实测接 HDMI 时，`ioreg -c IOAVService` 返回空，任何 VCP 读取（亮度/对比度/音量）都直接失败
- 只有 DisplayPort / USB-C 链路才有机会通

如果你本来就是 DP 或 USB-C 连接，DDC 方案更通用（不依赖厂商协议），值得优先试。

### 为什么不用 HDMI-CEC

方向反了。CEC 是让设备之间互相通知，不是让你从电脑精确指定「切到第几个输入口」。而且 PC 本身不发送 CEC 命令。

顺带一提：如果你的显示器接游戏机，**进显示器设置里关掉「CEC 一键关机 / 设备自动关机联动」，保留「CEC 开机联动」**，就能解决「电脑休眠把游戏机一起关掉」的问题——这跟切源是两件事，建议一起处理。

---

## 已知限制

- **DP / USB-C 的源名未在所有机型验证**，需要按你的设备实际试
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
│   ├── main.swift        菜单栏 App（AppKit + Carbon 热键）
│   ├── Info.plist
│   ├── build.sh          swiftc 一键编译打包
│   └── mitsw             命令行工具
├── windows/
│   ├── Program.cs        入口 + 单实例
│   ├── TrayApp.cs        托盘图标、菜单、热键、动作
│   ├── HotkeyWindow.cs   WM_HOTKEY / WM_POWERBROADCAST 接收窗口
│   ├── TvClient.cs       6095 接口调用 + 局域网发现
│   ├── Settings.cs       配置读写 + 开机自启
│   ├── Prompt.cs         两个小对话框
│   ├── MiTVSwitch.csproj
│   ├── build.bat
│   └── switch.ps1        命令行工具
└── .github/workflows/build.yml
```

## 安全性

- 工具只在局域网内发 HTTP GET，不联网、不上报
- 设备端无鉴权：能访问到 6095 端口的人都能控制它。**不要把 6095 端口映射到公网**
- 日志只记请求结果，不含任何隐私数据
  - macOS：`~/Library/Logs/MiTVSwitch.log`
  - Windows：`%APPDATA%\MiTVSwitch\MiTVSwitch.log`

---

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
