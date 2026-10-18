# MiTVSwitch

给**小米 / 红米电视**和**内置澎湃 OS 的智能显示器**做「一键切信号源」的 macOS 菜单栏小工具。

一个快捷键把显示器从电脑切到游戏机，再一个快捷键切回来。不用找遥控器，不用翻 OSD 菜单，不用改接线。

```
⌃⌥1  →  HDMI 1 （游戏机）
⌃⌥2  →  HDMI 2 （电脑）
```

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

在终端里跑一条命令：

```bash
curl -s --noproxy '*' "http://<设备IP>:6095/controller?action=getvolume"
```

- 返回类似 `{"status":0,"msg":"success","data":{...}}` → **支持**
- 连不上 → 设备没开这个服务，或者不在同一网段

> 智能电视基本都支持；显示器则看型号，内置澎湃 OS（小米电视系统）的型号大概率支持。

---

## 快速开始（三步）

### 1. 找到设备的局域网 IP

任选一种：

- 路由器后台的设备列表里找名字带 `mitv-`、`MiTV`、`Redmi` 的设备
- 米家 App 里的设备详情
- 终端里扫一下：`arp -a | grep -i -E "mi|redmi"`

### 2. 编译并安装

需要 macOS 13+ 和 Xcode Command Line Tools（`xcode-select --install`）。

```bash
git clone https://github.com/<你的账号>/MiTVSwitch.git
cd MiTVSwitch
chmod +x build.sh
./build.sh

# 装到 /Applications（开机自启功能需要这个位置）
cp -R MiTVSwitch.app /Applications/
open /Applications/MiTVSwitch.app
```

编译不需要开发者账号，脚本用 ad-hoc 签名，本机自用完全够。

### 3. 设置地址

第一次启动会自动弹出设置窗口，填入第 1 步拿到的 IP。

也可以从菜单里点「**自动搜索局域网设备…**」，工具会扫描本机所在网段，把找到的设备列出来让你选。

地址会保存在 `~/.config/mitvswitch/host`，命令行版也读这个文件。

---

## 使用

菜单栏会出现一个 `▭▭` 图标，点开是全部功能：

| 菜单项 | 说明 |
|---|---|
| HDMI 1 / HDMI 2 | 切信号源，带快捷键提示 |
| DisplayPort / USB-C | 其他接口（源名未在所有机型验证，见下） |
| 睡眠时切到第 1 个源 | 勾选后，Mac 休眠自动切到第一个源，醒来切回第二个 |
| 设置显示器地址… | 手动填 IP |
| 自动搜索局域网设备… | 扫描同网段 |
| 测试连接 | 查看设备信息，确认地址对不对 |
| 开机自动启动 | 注册登录项（App 需在 /Applications 下） |

### 全局快捷键

| 按键 | 动作 |
|---|---|
| `⌃⌥1` | 切到信号源列表第 1 项（默认 HDMI 1） |
| `⌃⌥2` | 切到第 2 项（默认 HDMI 2） |

用的是 Carbon `RegisterEventHotKey`，**不需要辅助功能权限**，也不会抢焦点。即使显示器当前显示的是游戏机画面、你看不到 Mac 桌面，在键盘上按下去照样生效。

### 命令行版

仓库里的 `mitsw` 是个独立脚本，不依赖 App：

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

地址来源优先级：环境变量 `MITV_IP` > 配置文件 `~/.config/mitvswitch/host`。

适合塞进 Raycast / Alfred / Shortcuts / 快捷指令，或者自己写自动化。

---

## 自定义信号源

打开 `main.swift`，改顶部的 `allSources`：

```swift
let allSources: [TVSource] = [
    TVSource(key: "HDMI1", title: "游戏机",  hotKeyCode: UInt32(kVK_ANSI_1), hotMods: UInt32(controlKey | optionKey)),
    TVSource(key: "HDMI2", title: "Mac",    hotKeyCode: UInt32(kVK_ANSI_2), hotMods: UInt32(controlKey | optionKey)),
    TVSource(key: "DP",    title: "显示器 DP", hotKeyCode: 0, hotMods: 0),
]
```

- `key`：传给 `changesource` 的源名，**大小写不敏感**
- `title`：菜单里显示的名字
- `hotKeyCode`：绑定的键（`kVK_ANSI_3` = 数字 3，以此类推），写 `0` 表示不绑
- 数组第 1、2 项同时是「睡眠切换」的目标

改完重新 `./build.sh` 即可。

---

## 排障

### 连接不上，但 `curl` 明明能通

**最常见的坑：系统代理。**

macOS 的系统代理「忽略这些主机」列表**不支持 CIDR 网段**——写 `192.168.0.0/16` 完全是无效的，系统会把它当一串普通字符。结果发往 `192.168.1.x` 的请求照样被塞进代理（比如 Clash 的 `127.0.0.1:7890`），然后死在代理里。

表现很有迷惑性：**公网能通、局域网不通**，容易被误判成权限问题。

本项目的做法是**完全不用 `NSURLSession`**，改用裸 BSD socket（`socket` + `connect` + `send/recv`）自己拼 HTTP/1.0 请求，从根上绕开代理层。命令行版则是给 `curl` 加 `--noproxy '*'`。

如果你在集成到自己的项目里，记得处理这一层。

### 开机后第一次切换失败

冷启动时 ARP 缓存是冷的，`connect()` 会立刻返回 `EHOSTUNREACH (65)`，看起来像设备不在线。工具内置了 3 次重试（间隔递增），通常一下就过去了。

### 提示「源名不被设备接受」

`changesource` 对无效源名会返回 `{"status":0,"msg":"error"}`，而完全不存在这个 action 时返回空 body 的 404——两者容易混淆。

- `HDMI1` / `hdmi1` / `HDMI2` 都行
- `HDMI_2` 不行
- DP / USB-C 的源名各机型可能不同，可以逐个试：
  ```bash
  ./mitsw raw DP
  ./mitsw raw USBC
  ./mitsw raw DP1
  ```

### 为什么不用 DDC/CI

DDC/CI 是显示器切源的标准做法，`m1ddc` / BetterDisplay 都走这条。但：

- **Apple Silicon 的 HDMI 口不开放 DDC 的 I²C 通道**。实测接 HDMI 时，`ioreg -c IOAVService` 返回空，任何 VCP 读取（亮度/对比度/音量）都直接失败
- 只有 DisplayPort / USB-C 链路才有机会通

如果你本来就是 DP 或 USB-C 连接，DDC 方案更通用（不依赖厂商协议），值得优先试。

### 为什么不用 HDMI-CEC

方向反了。CEC 是让设备之间互相通知，不是让你从电脑精确指定「切到第几个输入口」。而且 Mac 本身不发送 CEC 命令。

顺带一提：如果你的显示器接游戏机，**进显示器设置里关掉「CEC 一键关机 / 设备自动关机联动」，保留「CEC 开机联动」**，就能解决「电脑休眠把游戏机一起关掉」的问题——这跟切源是两件事，建议一起处理。

---

## 已知限制

- **DP / USB-C 的源名未在所有机型验证**，需要按你的设备实际试
- `action=capturescreen`（截屏）在 HDMI 输入下无效，因为此时 TV 系统在后台，只返回几十字节的 JSON
- 部分设备的 ADB(5555)、Android TV Remote(6466/6467) 是关的，本工具不依赖它们
- 小米电视助手协议是非公开接口，**系统更新后可能发生变化**。真改了就退回 `keyevent` 模拟按键
- 只在 macOS 上实现。Windows 用户可以用等价的 HTTP 请求自己包一层

## 安全性

- 工具只在局域网内发 HTTP GET，不联网、不上报
- 设备端无鉴权：能访问到 6095 端口的人都能控制它。**不要把 6095 端口映射到公网**
- 日志写在 `~/Library/Logs/MiTVSwitch.log`，只记请求结果，不含任何隐私数据

---

## 免责声明

非官方项目，与小米公司无任何关联。使用自有设备，风险自负。接口为非公开协议，可能随时变更。

## License

[MIT](LICENSE)
