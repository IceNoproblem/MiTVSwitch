//
//  MiTVSwitch — 小米 / 红米电视与智能显示器的信号源一键切换
//
//  SPDX-License-Identifier: GPL-3.0-only
//

import Cocoa
import Carbon.HIToolbox
import ServiceManagement

// MARK: - 常量

let kConfigDir = (("~/.config/mitvswitch") as NSString).expandingTildeInPath
let kHostFile  = kConfigDir + "/host"
let kLogFile   = (("~/Library/Logs/MiTVSwitch.log") as NSString).expandingTildeInPath
let kPort      = "6095"

let kAutoSleepKey = "autoOnSleep"

// MARK: - 信号源表
//
// key   : 源名，大小写不敏感；程序启动时用 getsources 解析成设备上的编号
// title : 界面里显示的名字
// hotkey: 默认快捷键，用户可在设置面板里改
//         按自己的设备改这里的顺序和名字即可。

struct HotkeySpec {
    let keyCode: UInt32
    let mods: UInt32      // Carbon 修饰键：controlKey / optionKey / shiftKey / cmdKey
}

struct TVSource {
    let key: String
    let title: String
    let hotkey: HotkeySpec?
}

let allSources: [TVSource] = [
    TVSource(key: "HDMI1", title: "HDMI 1",      hotkey: HotkeySpec(keyCode: UInt32(kVK_ANSI_1), mods: UInt32(controlKey | optionKey))),
    TVSource(key: "HDMI2", title: "HDMI 2",      hotkey: HotkeySpec(keyCode: UInt32(kVK_ANSI_2), mods: UInt32(controlKey | optionKey))),
    TVSource(key: "DP",    title: "DisplayPort", hotkey: nil),
    TVSource(key: "USBC",  title: "USB-C",       hotkey: nil),
]

// MARK: - 快捷键配置
//
// 动作 id 是稳定的字符串，用户改过的键存在 UserDefaults 里，
// 没存过就用上面的默认值。空字符串表示「这个动作不绑键」。

let kHotkeysKey = "hotkeyMap"

func defaultHotkeys() -> [String: HotkeySpec] {
    var m: [String: HotkeySpec] = [:]
    for s in allSources { if let h = s.hotkey { m["src.\(s.key)"] = h } }
    m["vol.up"]     = HotkeySpec(keyCode: UInt32(kVK_UpArrow),   mods: UInt32(controlKey | optionKey | shiftKey))
    m["vol.down"]   = HotkeySpec(keyCode: UInt32(kVK_DownArrow), mods: UInt32(controlKey | optionKey | shiftKey))
    m["bright.up"]  = HotkeySpec(keyCode: UInt32(kVK_UpArrow),   mods: UInt32(controlKey | optionKey))
    m["bright.down"] = HotkeySpec(keyCode: UInt32(kVK_DownArrow), mods: UInt32(controlKey | optionKey))
    return m
}

/// 某个动作当前生效的快捷键；返回 nil 表示不绑
func hotkey(for action: String) -> HotkeySpec? {
    let raw = UserDefaults.standard.dictionary(forKey: kHotkeysKey) as? [String: String]
    if let stored = raw?[action] {
        if stored.isEmpty { return nil }              // 用户主动清空
        let parts = stored.split(separator: ":")
        if parts.count == 2, let c = UInt32(parts[0]), let m = UInt32(parts[1]) {
            return HotkeySpec(keyCode: c, mods: m)
        }
        return nil
    }
    return defaultHotkeys()[action]
}

func setHotkey(_ spec: HotkeySpec?, for action: String) {
    var raw = (UserDefaults.standard.dictionary(forKey: kHotkeysKey) as? [String: String]) ?? [:]
    if let s = spec {
        raw[action] = "\(s.keyCode):\(s.mods)"
    } else {
        raw[action] = ""                              // 空串 = 明确不绑
    }
    UserDefaults.standard.set(raw, forKey: kHotkeysKey)
}

func resetHotkeys() {
    UserDefaults.standard.removeObject(forKey: kHotkeysKey)
}

/// 把快捷键渲染成 ⌃⌥1 这种形式，显示在菜单和面板里
func formatHotkey(_ s: HotkeySpec?) -> String {
    guard let s = s else { return "未设置" }
    var out = ""
    if s.mods & UInt32(controlKey) != 0 { out += "⌃" }
    if s.mods & UInt32(optionKey)  != 0 { out += "⌥" }
    if s.mods & UInt32(shiftKey)   != 0 { out += "⇧" }
    if s.mods & UInt32(cmdKey)     != 0 { out += "⌘" }
    return out + keyName(s.keyCode)
}

func keyName(_ code: UInt32) -> String {
    switch Int(code) {
    case kVK_ANSI_0: return "0"
    case kVK_ANSI_1: return "1"
    case kVK_ANSI_2: return "2"
    case kVK_ANSI_3: return "3"
    case kVK_ANSI_4: return "4"
    case kVK_ANSI_5: return "5"
    case kVK_ANSI_6: return "6"
    case kVK_ANSI_7: return "7"
    case kVK_ANSI_8: return "8"
    case kVK_ANSI_9: return "9"
    case kVK_UpArrow:   return "↑"
    case kVK_DownArrow: return "↓"
    case kVK_LeftArrow: return "←"
    case kVK_RightArrow: return "→"
    case kVK_Space:     return "空格"
    case kVK_Return:    return "回车"
    case kVK_F1:  return "F1"
    case kVK_F2:  return "F2"
    case kVK_F3:  return "F3"
    case kVK_F4:  return "F4"
    case kVK_F5:  return "F5"
    case kVK_F6:  return "F6"
    case kVK_F7:  return "F7"
    case kVK_F8:  return "F8"
    case kVK_F9:  return "F9"
    case kVK_F10: return "F10"
    case kVK_F11: return "F11"
    case kVK_F12: return "F12"
    default:
        // 字母键的 keyCode 不连续，用当前键盘布局反查
        if let src = CGEventSource(stateID: .hidSystemState),
           let ev = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(code), keyDown: true),
           let ns = NSEvent(cgEvent: ev) {
            let s = ns.charactersIgnoringModifiers?.uppercased() ?? ""
            if !s.isEmpty { return s }
        }
        return "键\(code)"
    }
}

/// 所有可绑定快捷键的动作，设置面板和注册逻辑共用
struct HotkeyAction {
    let id: String
    let title: String
    let group: String
}

func allHotkeyActions() -> [HotkeyAction] {
    var out: [HotkeyAction] = []
    for s in allSources {
        out.append(HotkeyAction(id: "src.\(s.key)", title: "切到 \(s.title)", group: "信号源"))
    }
    out.append(HotkeyAction(id: "vol.up",      title: "音量 +",  group: "音量"))
    out.append(HotkeyAction(id: "vol.down",    title: "音量 −",  group: "音量"))
    out.append(HotkeyAction(id: "bright.up",   title: "亮度 +",  group: "画面"))
    out.append(HotkeyAction(id: "bright.down", title: "亮度 −",  group: "画面"))
    return out
}

/// 执行一个动作（菜单、面板、全局热键都走这里）
func runAction(_ action: String) {
    if action.hasPrefix("src.") {
        AppDelegate.shared.performSwitch(String(action.dropFirst(4)))
        return
    }
    switch action {
    case "vol.up":      stepVolume(up: true)
    case "vol.down":    stepVolume(up: false)
    case "bright.up":   stepBrightness(+5)
    case "bright.down": stepBrightness(-5)
    default: break
    }
    DispatchQueue.main.async {
        AppDelegate.shared.buildMenu()
        SettingsPanel.shared.syncPicture()
    }
}

// MARK: - 设备信号源
//
// 设备会报一份真实的输入列表（getsources）。按编号切（changesource2）比按
// 名字切（changesource）可靠：DP 和 USB-C 用名字切会被设备拒绝，按编号才行。
//
// 编号沿用 MStar 电视输入表：23=HDMI1、24=HDMI2、29=DP、30=USB-C。
// 老固件会把 29 报成 DVI，所以 DP / USB-C 按编号纠正名字。
// 这套编号与名字映射的做法来自 MonitorBar（MIT，© 2026 binaryify），见 NOTICE。

struct DeviceSource {
    let id: Int
    let name: String
}

let kIDDisplayPort = 29
let kIDUSBC = 30

private let sourceLock = NSLock()
private var storedSources: [DeviceSource] = []

var deviceSources: [DeviceSource] {
    sourceLock.lock(); defer { sourceLock.unlock() }
    return storedSources
}

func setDeviceSources(_ v: [DeviceSource]) {
    sourceLock.lock(); storedSources = v; sourceLock.unlock()
}

/// 规范化源名，让 HDMI 1 / HDMI_1 / hdmi1 这几种写法能对上
func normalizeSource(_ s: String) -> String {
    s.uppercased().filter { $0.isLetter || $0.isNumber }
}

func sourceName(forID id: Int, raw: String) -> String {
    switch id {
    case kIDDisplayPort: return "DP"
    case kIDUSBC:        return "USBC"
    default:             return raw
    }
}

func parseSources(_ body: String) -> [DeviceSource] {
    guard let data = jsonPayload(body),
          let list = data["sourceList"] as? [[String: Any]] else { return [] }
    return list.compactMap { item in
        guard let id = item["sourceId"] as? Int else { return nil }
        return DeviceSource(id: id, name: sourceName(forID: id, raw: item["sourceName"] as? String ?? ""))
    }
}

/// 静态表里的 key 对应设备上的哪个编号；设备没有这个输入时返回 nil
func resolveSourceID(_ key: String) -> Int? {
    let list = deviceSources
    guard !list.isEmpty else { return nil }
    let want = normalizeSource(key)

    // 先按编号认领显示器专属输入，避免老固件把 DP 报成 DVI
    if want == "USBC" || want == "TYPEC" {
        if list.contains(where: { $0.id == kIDUSBC }) { return kIDUSBC }
    }
    if want == "DP" || want == "DP1" {
        if list.contains(where: { $0.id == kIDDisplayPort }) { return kIDDisplayPort }
    }
    if let exact = list.first(where: { normalizeSource($0.name) == want }) { return exact.id }
    if let loose = list.first(where: { normalizeSource($0.name).hasPrefix(want) }) { return loose.id }
    return nil
}

/// 有 DP / USB-C 输入的几乎只可能是显示器，电视没有这两个口
func looksLikeMonitor(name: String, sources: [DeviceSource]) -> Bool {
    let n = name.lowercased()
    if n.contains("显示器") || n.contains("monitor") { return true }
    return sources.contains { $0.id == kIDDisplayPort || $0.id == kIDUSBC }
}

// MARK: - 日志

func logLine(_ s: String) {
    let f = DateFormatter()
    f.dateFormat = "yyyy-MM-dd HH:mm:ss"
    let line = "[\(f.string(from: Date()))] \(s)\n"
    let url = URL(fileURLWithPath: kLogFile)
    if let h = try? FileHandle(forWritingTo: url) {
        h.seekToEndOfFile()
        h.write(line.data(using: .utf8) ?? Data())
        try? h.close()
    } else {
        try? FileManager.default.createDirectory(
            atPath: (kLogFile as NSString).deletingLastPathComponent,
            withIntermediateDirectories: true)
        try? line.write(toFile: kLogFile, atomically: true, encoding: .utf8)
    }
}

// MARK: - 配置读写

func readHost() -> String {
    if let s = try? String(contentsOfFile: kHostFile, encoding: .utf8) {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        if !t.isEmpty { return t }
    }
    if let s = UserDefaults.standard.string(forKey: "tvHost"), !s.isEmpty { return s }
    if let s = ProcessInfo.processInfo.environment["MITV_IP"], !s.isEmpty { return s }
    return ""
}

func writeHost(_ v: String) {
    try? FileManager.default.createDirectory(atPath: kConfigDir, withIntermediateDirectories: true)
    try? v.write(toFile: kHostFile, atomically: true, encoding: .utf8)
    UserDefaults.standard.set(v, forKey: "tvHost")
}

var tvHost: String { readHost() }

// MARK: - 网络
//
// 这里刻意不走 NSURLSession：macOS 会套用系统代理，而系统代理的例外列表
// 不支持 CIDR 网段（例如只写 192.168.0.0/16 是无效的），发往局域网设备的
// 请求会被塞进代理然后失败。裸 socket 直连可以绕开这一整层。

func rawGet(host: String, port: String, path: String, timeoutSec: Int32 = 4) -> (errno: Int32, text: String) {
    var hints = addrinfo()
    hints.ai_family = AF_INET
    hints.ai_socktype = SOCK_STREAM

    var res: UnsafeMutablePointer<addrinfo>? = nil
    let gai = getaddrinfo(host, port, &hints, &res)
    guard gai == 0, let info = res else { return (-2, "getaddrinfo 失败(\(gai))") }
    defer { freeaddrinfo(res) }

    let fd = socket(info.pointee.ai_family, info.pointee.ai_socktype, info.pointee.ai_protocol)
    guard fd >= 0 else { return (Darwin.errno, "socket() 失败") }
    defer { close(fd) }

    var tv = timeval(tv_sec: Int(timeoutSec), tv_usec: 0)
    setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

    if connect(fd, info.pointee.ai_addr, info.pointee.ai_addrlen) != 0 {
        return (Darwin.errno, "connect() 失败")
    }

    let req = "GET \(path) HTTP/1.0\r\nHost: \(host)\r\nConnection: close\r\n\r\n"
    _ = req.withCString { send(fd, $0, strlen($0), 0) }

    var data = Data()
    var buf = [UInt8](repeating: 0, count: 4096)
    while true {
        let n = recv(fd, &buf, buf.count, 0)
        if n <= 0 { break }
        data.append(buf, count: n)
    }
    return (0, String(data: data, encoding: .utf8) ?? "")
}

/// 通用的路径请求。host 传 nil 时用配置里的设备地址；
/// 局域网发现需要逐个 IP 探测，所以留了 override。
@discardableResult
func apiPath(_ path: String, host override: String? = nil) -> (code: Int, body: String) {
    let host = override ?? tvHost
    guard !host.isEmpty else { return (-3, "尚未设置显示器地址") }

    // 首次访问时 ARP 缓存可能是冷的，connect 会立刻返回 EHOSTUNREACH(65)。
    // 稍等重试即可，不会真的失败。
    var r: (errno: Int32, text: String) = (-1, "")
    for attempt in 0..<3 {
        r = rawGet(host: host, port: kPort, path: path)
        if r.errno == 0 { break }
        if r.errno == 65 || r.errno == 51 || r.errno == 60 {
            usleep(useconds_t(300_000 * (attempt + 1)))
            continue
        }
        break
    }

    guard r.errno == 0 else {
        logLine("socket 失败 errno=\(r.errno) \(r.text)")
        return (-1, "\(r.text) (errno \(r.errno))")
    }

    let firstLine = r.text.components(separatedBy: "\r\n").first ?? ""
    let code = Int(firstLine.split(separator: " ").dropFirst().first ?? "") ?? -1
    let body = r.text.components(separatedBy: "\r\n\r\n").dropFirst().joined(separator: "\r\n")
    return (code, body)
}

@discardableResult
func api(_ action: String) -> (code: Int, body: String) {
    apiPath("/controller?action=\(action)")
}

// MARK: - JSON 解析

func jsonRoot(_ body: String) -> [String: Any]? {
    guard let d = body.data(using: .utf8),
          let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return nil }
    return o
}

func jsonPayload(_ body: String) -> [String: Any]? {
    jsonRoot(body)?["data"] as? [String: Any]
}

/// /request?action=isalive 里带的设备名，比 getsysteminfo 的干净
func deviceName(from body: String) -> String? {
    jsonPayload(body)?["devicename"] as? String
}

func number(from body: String, _ key: String) -> Int? {
    (jsonPayload(body)?[key] as? NSNumber)?.intValue
}

// MARK: - 信号源

func fetchSources(host: String? = nil) -> [DeviceSource] {
    let r = apiPath("/controller?action=getsources", host: host)
    guard r.code == 200 else { return [] }
    return parseSources(r.body)
}

/// 拉一次真实输入列表，供按编号切换使用
func refreshSources() {
    let s = fetchSources()
    guard !s.isEmpty else {
        logLine("getsources 没有返回列表，退回按名字切换")
        return
    }
    setDeviceSources(s)
    logLine("信号源：" + s.map { "\($0.name)=\($0.id)" }.joined(separator: " "))
}

/// 切信号源。优先 changesource2 按编号切（能切 DP / USB-C），
/// 设备不认这个 action 时退回按名字切。
func switchSource(_ key: String) -> Bool {
    if let id = resolveSourceID(key) {
        let r = api("changesource2&sourceid=\(id)")
        if r.code == 200 && r.body.contains("\"success\"") {
            logLine("changesource2 \(key) (id=\(id)) -> OK")
            return true
        }
        logLine("changesource2 \(key) (id=\(id)) 失败 HTTP \(r.code) \(r.body.prefix(120))，改按名字试")
    }
    let r = api("changesource&source=\(key)")
    let ok = r.code == 200 && r.body.contains("\"success\"")
    logLine("changesource \(key) -> HTTP \(r.code) \(r.body.prefix(160))")
    return ok
}

// MARK: - 音量与遥控按键

/// 发一个遥控按键，例如 volumeup / volumedown / menu / right
@discardableResult
func pressKey(_ key: String) -> Bool {
    let r = api("keyevent&keycode=\(key)")
    return r.code == 200 && r.body.contains("\"success\"")
}

/// 读音量，返回 (当前, 最大)
func readVolume() -> (value: Int, max: Int)? {
    let r = api("getvolume")
    guard r.code == 200,
          let p = jsonPayload(r.body),
          let v = (p["volume"] as? NSNumber)?.intValue else { return nil }
    let m = (p["maxVolume"] as? NSNumber)?.intValue ?? 100
    return (v, max(m, 1))
}

/// 音量按步长调整。接口只支持 ±1，所以循环按次数。
func stepVolume(up: Bool, steps: Int = 2) {
    for _ in 0..<steps {
        pressKey(up ? "volumeup" : "volumedown")
        usleep(60_000)
    }
    logLine("音量 \(up ? "+" : "-")\(steps)")
}

// MARK: - 双模显示器的竞技模式
//
// G Pro 27U 这类双模显示器在 4K160 与 1080p320 之间切换要换 EDID，
// 没有对应接口，只能走按键宏：menu 打开游戏栏，右移 4 次到模式项，
// enter 进入，right 开 / left 关。按键序列由冰哥实测确认。
// 思路参考 MonitorBar（MIT，© 2026 binaryify），见 NOTICE。

func setCompetitiveMode(on: Bool) {
    let steps: [(key: String, wait: useconds_t)] = [
        ("menu", 700_000),
        ("right", 100_000), ("right", 100_000),
        ("right", 100_000), ("right", 100_000),
        ("enter", 300_000),
        (on ? "right" : "left", 0),
    ]
    for s in steps {
        _ = pressKey(s.key)
        if s.wait > 0 { usleep(s.wait) }
    }
    // 关掉游戏栏。此时显示器正在换 EDID，可能不应答，不影响切换结果。
    _ = pressKey("back")
    _ = pressKey("back")
    logLine("竞技模式 320Hz -> \(on ? "开" : "关")")
}


// MARK: - 局域网发现

func localIPv4() -> String? {
    var ifaddr: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&ifaddr) == 0, ifaddr != nil else { return nil }
    defer { freeifaddrs(ifaddr) }

    var ptr = ifaddr
    while let p = ptr {
        let f = p.pointee
        if let addr = f.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) {
            let name = String(cString: f.ifa_name)
            if name == "en0" || name == "en1" {
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(addr, socklen_t(addr.pointee.sa_len),
                               &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 {
                    let s = String(cString: host)
                    if !s.hasPrefix("169.254.") { return s }
                }
            }
        }
        ptr = f.ifa_next
    }
    return nil
}

/// 扫描本机所在的 /24 网段，返回所有 6095 端口可用且能报告设备名的 IP
func discoverTVs(progress: @escaping (Int) -> Void) -> [(ip: String, name: String, isMonitor: Bool)] {
    guard let local = localIPv4() else { return [] }
    let parts = local.split(separator: ".")
    guard parts.count == 4 else { return [] }
    let prefix = "\(parts[0]).\(parts[1]).\(parts[2])."

    var hits = [String]()
    let lock = NSLock()
    var done = 0

    DispatchQueue.concurrentPerform(iterations: 254) { i in
        let ip = prefix + String(i + 1)
        let r = rawGet(host: ip, port: kPort, path: "/controller?action=getsysteminfo", timeoutSec: 1)
        if r.errno == 0 && r.text.contains("\"success\"") {
            lock.lock(); hits.append(ip); lock.unlock()
        }
        lock.lock(); done += 1; let d = done; lock.unlock()
        if d % 20 == 0 { progress(d) }
    }

    return hits.sorted().map { ip in
        let r = rawGet(host: ip, port: kPort, path: "/controller?action=getsysteminfo")
        var name = "未知设备"
        if let range = r.text.range(of: "\"devicename\":\"") {
            let rest = r.text[range.upperBound...]
            if let end = rest.firstIndex(of: "\"") { name = String(rest[..<end]) }
        }
        // 有 DP / USB-C 输入的几乎只可能是显示器，电视没有这两个口
        let sources = fetchSources(host: ip)
        return (ip, name, looksLikeMonitor(name: name, sources: sources))
    }
}

// MARK: - 软件调光与色温（Gamma 表）
//
// 改的是显卡的 Gamma 查色表，不动背光，所以任何连接线、任何显示器都能用。
// 代价是压得越暗对比度越低。退出时恢复原始曲线。
// 思路与档位设计参考 MonitorBar（MIT，© 2026 binaryify），见 NOTICE。

// 注意：这个 key 与 v1.2 的档位序号 key 不能复用 —— 旧值是个 0…3 的整数，
// 按 Double 读出来会被当成 0%…3%，亮度直接黑屏。换新 key 让旧值自然失效。
let kGammaLevelKey = "gammaBrightnessLevel"  // 0.30 ... 1.00
let kGammaTempKey  = "gammaTemp"
let kAutoSceneKey  = "autoScene"
let kSceneDayKey   = "sceneDayMinutes"      // 从 0 点起的分钟数
let kSceneNightKey = "sceneNightMinutes"

let kMinBrightness: Double = 0.30
/// 菜单里的快捷档位（面板上是连续滑动条）
let kBrightnessNames = ["100%", "85%", "70%", "55%"]
let kBrightnessPercents: [Double] = [100, 85, 70, 55]
let kTempValues: [Double] = [6500, 5000, 4000]
let kTempNames = ["标准 6500K", "暖 5000K", "夜间 4000K"]

/// 场景 = 亮度 + 色温的组合，普通显示器也能用（纯 Gamma）
struct Scene {
    let name: String
    let brightness: Double
    let tempIndex: Int
}

let kScenes: [Scene] = [
    Scene(name: "日间", brightness: 1.00, tempIndex: 0),
    Scene(name: "阅读", brightness: 0.85, tempIndex: 1),
    Scene(name: "观影", brightness: 0.70, tempIndex: 2),
    Scene(name: "夜间", brightness: 0.55, tempIndex: 2),
]

private var origRed   = [CGGammaValue]()
private var origGreen = [CGGammaValue]()
private var origBlue  = [CGGammaValue]()

func saveGammaOnce() {
    guard origRed.isEmpty else { return }
    let n = 256
    var r = [CGGammaValue](repeating: 0, count: n)
    var g = r, b = r
    var count: UInt32 = UInt32(n)
    if CGGetDisplayTransferByTable(CGMainDisplayID(), UInt32(n), &r, &g, &b, &count) == .success,
       count > 0 {
        origRed   = Array(r.prefix(Int(count)))
        origGreen = Array(g.prefix(Int(count)))
        origBlue  = Array(b.prefix(Int(count)))
    }
}

/// 按开尔文算 RGB 增益（Tanner Helland 近似），再归一化，只改色不改整体亮度
func rgbGains(kelvin: Double) -> (r: Double, g: Double, b: Double) {
    let t = kelvin / 100
    var r = 255.0, g = 255.0, b = 255.0
    if t <= 66 {
        g = 99.4708025861 * log(t) - 161.1195681661
        b = t <= 19 ? 0 : 138.5177312231 * log(t - 10) - 305.0447927307
    } else {
        r = 329.698727446 * pow(t - 60, -0.1332047592)
        g = 288.1221695283 * pow(t - 60, -0.0755148492)
    }
    func clamp(_ v: Double) -> Double { min(255, max(0, v)) / 255 }
    let rr = clamp(r), gg = clamp(g), bb = clamp(b)
    let m = max(rr, max(gg, bb))
    guard m > 0 else { return (1, 1, 1) }
    return (rr / m, gg / m, bb / m)
}

func applyGamma(level: Double, kelvin: Double) {
    saveGammaOnce()
    guard !origRed.isEmpty else { return }
    let n = origRed.count
    let gains = rgbGains(kelvin: kelvin)
    var r = [CGGammaValue](repeating: 0, count: n)
    var g = r, b = r
    for i in 0..<n {
        r[i] = CGGammaValue(min(1.0, Double(origRed[i])   * level * gains.r))
        g[i] = CGGammaValue(min(1.0, Double(origGreen[i]) * level * gains.g))
        b[i] = CGGammaValue(min(1.0, Double(origBlue[i])  * level * gains.b))
    }
    CGSetDisplayTransferByTable(CGMainDisplayID(), UInt32(n), &r, &g, &b)
}

func restoreGamma() {
    guard !origRed.isEmpty else { return }
    let n = origRed.count
    CGSetDisplayTransferByTable(CGMainDisplayID(), UInt32(n), &origRed, &origGreen, &origBlue)
}

/// 亮度是连续值（0.30 ... 1.00），没存过就是 100%
var gammaBrightness: Double {
    get {
        if let v = UserDefaults.standard.object(forKey: kGammaLevelKey) as? Double {
            return min(1.0, max(kMinBrightness, v))
        }
        return 1.0
    }
    set { UserDefaults.standard.set(min(1.0, max(kMinBrightness, newValue)), forKey: kGammaLevelKey) }
}

var gammaTempIndex: Int {
    get { min(max(UserDefaults.standard.integer(forKey: kGammaTempKey), 0), kTempValues.count - 1) }
    set { UserDefaults.standard.set(newValue, forKey: kGammaTempKey) }
}

private var ddcWork: DispatchWorkItem?
/// 是否已经尝试过硬写背光。试过且失败就不再反复试，直接走软件调光。
private var hardwareTried = false

/// 硬件背光写入有延迟，拖动滑动条时合并成最后一次，避免把 I²C 打满
func pushHardwareBrightness(_ percent: Int) {
    ddcWork?.cancel()
    let w = DispatchWorkItem {
        hardwareTried = true
        let ok = DDC.shared.setBrightness(percent)
        if !ok {
            // 硬件不通，立刻退回软件调光，别让用户对着没反应的滑动条发呆
            applyGamma(level: gammaBrightness, kelvin: kTempValues[gammaTempIndex])
            logLine("DDC 写入失败，改走软件调光")
        }
        SettingsPanel.shared.syncPicture()
    }
    ddcWork = w
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: w)
}

/// 把当前的亮度 / 色温套到屏幕上。
///
/// 两条路：DDC/CI 能写就调硬件背光（Gamma 只负责色温），写不了就整块 Gamma 软件调光。
/// 所以「支持 DDC 的显示器」和「什么协议都不支持的普通显示器」用的是同一套界面。
func applyPicture() {
    let kelvin = kTempValues[gammaTempIndex]
    let ddcWorthTrying = DDC.shared.available && (DDC.shared.working || !hardwareTried)
    if ddcWorthTrying {
        applyGamma(level: 1.0, kelvin: kelvin)     // 亮度交给背光，Gamma 只管色温
        pushHardwareBrightness(Int((gammaBrightness * 100).rounded()))
    } else {
        applyGamma(level: gammaBrightness, kelvin: kelvin)
    }
}

/// 当前亮度走的是哪条路（给面板显示）
var brightnessBackendLabel: String {
    if DDC.shared.working { return "硬件背光（DDC/CI）" }
    if !DDC.shared.probed { return "正在探测…" }
    return "软件调光（Gamma）"
}

/// 设置亮度百分比（30 ... 100）
func setBrightnessPercent(_ percent: Double) {
    gammaBrightness = percent / 100
    applyPicture()
}

/// 相对调整亮度，delta 是百分点
func stepBrightness(_ delta: Double) {
    setBrightnessPercent(gammaBrightness * 100 + delta)
}

/// 套用第 index 个场景
func applyScene(_ index: Int) {
    guard index >= 0 && index < kScenes.count else { return }
    let s = kScenes[index]
    gammaBrightness = s.brightness
    gammaTempIndex = s.tempIndex
    applyPicture()
    logLine("场景：\(s.name)（亮度 \(Int((gammaBrightness * 100).rounded()))%，\(kTempNames[gammaTempIndex])）")
}

// MARK: - 按时间自动切换场景

func minutesOfDay(_ d: Date) -> Int {
    let c = Calendar.current.dateComponents([.hour, .minute], from: d)
    return (c.hour ?? 0) * 60 + (c.minute ?? 0)
}

var sceneDayMinutes: Int {
    get { UserDefaults.standard.object(forKey: kSceneDayKey) as? Int ?? 8 * 60 }
    set { UserDefaults.standard.set(newValue, forKey: kSceneDayKey) }
}

var sceneNightMinutes: Int {
    get { UserDefaults.standard.object(forKey: kSceneNightKey) as? Int ?? 20 * 60 }
    set { UserDefaults.standard.set(newValue, forKey: kSceneNightKey) }
}

private var lastAutoSceneIsNight: Bool?

/// 到点就切场景。force = 用户刚改过开关或时间，立刻重算一次。
func checkAutoScene(force: Bool = false) {
    guard UserDefaults.standard.bool(forKey: kAutoSceneKey) else {
        lastAutoSceneIsNight = nil
        return
    }
    let now = minutesOfDay(Date())
    let day = sceneDayMinutes, night = sceneNightMinutes
    // 白天区间算 [day, night)；若用户把夜间设得比白天早（跨夜），按补集处理
    let isNight: Bool
    if day <= night {
        isNight = !(now >= day && now < night)
    } else {
        isNight = (now >= night && now < day)
    }
    guard force || lastAutoSceneIsNight != isNight else { return }
    lastAutoSceneIsNight = isNight
    applyScene(isNight ? 3 : 0)      // 3 = 夜间，0 = 日间
    SettingsPanel.shared.syncPicture()
}

/// 把一个子菜单挂到条目上
func submenu(_ title: String, _ items: [NSMenuItem]) -> NSMenuItem {
    let parent = NSMenuItem(title: title, action: nil, keyEquivalent: "")
    let m = NSMenu()
    for i in items { m.addItem(i) }
    parent.submenu = m
    return parent
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate {
    static let shared = AppDelegate()

    var statusItem: NSStatusItem!
    var busy = false
    var autoSceneTimer: Timer?

    func applicationDidFinishLaunching(_ note: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "rectangle.on.rectangle", accessibilityDescription: "信号源")
            button.image?.isTemplate = true
        }
        buildMenu()
        registerHotKeys()
        observeSleepWake()

        // 屏幕时间：30 秒采样一次，面板开着时同步刷新图表
        UsageTracker.shared.onChange = { SettingsPanel.shared.refreshUsage() }
        UsageTracker.shared.start()

        // 先探一下 DDC/CI 能不能读写（只读不写，不会动背光），再决定亮度走哪条路。
        // 系统重启或换显示器后 Gamma 会复位，所以每次启动都重新套一遍。
        DispatchQueue.global().async {
            let ddcOK = DDC.shared.probe()
            logLine("DDC 探活：\(ddcOK ? "可用，亮度走硬件背光" : "不可用，亮度走软件调光")")
            DispatchQueue.main.async {
                if gammaBrightness < 0.999 || gammaTempIndex != 0 { applyPicture() }
                SettingsPanel.shared.syncPicture()
            }
        }

        // 自动场景：每分钟看一眼是否该切日间 / 夜间
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { checkAutoScene(force: true) }
        autoSceneTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { _ in
            checkAutoScene()
        }

        if tvHost.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { self.setHost(nil) }
        } else {
            DispatchQueue.global().async {
                let host = readHost()
                refreshSources()
                let r = api("getsysteminfo")
                logLine("启动自检 \(host) HTTP \(r.code)")
                DispatchQueue.main.async { self.buildMenu() }
            }
        }
    }

    func applicationWillTerminate(_ note: Notification) {
        autoSceneTimer?.invalidate()
        UsageTracker.shared.stop()
        restoreGamma()
    }

    // MARK: 菜单

    func buildMenu() {
        let menu = NSMenu()

        let panelItem = NSMenuItem(title: "打开设置面板…", action: #selector(openPanel), keyEquivalent: "")
        panelItem.target = self
        menu.addItem(panelItem)

        let head = NSMenuItem(title: tvHost.isEmpty ? "未设置显示器地址" : "显示器 · \(tvHost)", action: nil, keyEquivalent: "")
        head.isEnabled = false
        menu.addItem(head)
        menu.addItem(.separator())

        for s in allSources {
            var title = "   " + s.title
            let hk = formatHotkey(hotkey(for: "src.\(s.key)"))
            if hk != "未设置" { title += "    " + hk }
            let item = NSMenuItem(title: title, action: #selector(menuSwitch(_:)), keyEquivalent: "")
            item.representedObject = s.key
            item.target = self
            // 设备报了输入列表时，标出它没有的口，免得点了白点
            if !deviceSources.isEmpty && resolveSourceID(s.key) == nil {
                item.title = title + "   （设备无此输入）"
            }
            menu.addItem(item)
        }

        menu.addItem(.separator())

        // 音量（走局域网遥控按键，不依赖 DDC）
        let volUp = NSMenuItem(title: "音量 +    " + formatHotkey(hotkey(for: "vol.up")),
                               action: #selector(volumeUp(_:)), keyEquivalent: "")
        volUp.target = self
        let volDown = NSMenuItem(title: "音量 −    " + formatHotkey(hotkey(for: "vol.down")),
                                 action: #selector(volumeDown(_:)), keyEquivalent: "")
        volDown.target = self
        let volShow = NSMenuItem(title: "查看当前音量", action: #selector(showVolume(_:)), keyEquivalent: "")
        volShow.target = self
        menu.addItem(submenu("音量", [volUp, volDown, .separator(), volShow]))

        // 画面：亮度（DDC 硬件背光优先，否则软件调光）+ 色温
        var picItems: [NSMenuItem] = []
        let hw = DDC.shared.working
        let briHead = NSMenuItem(title: hw ? "亮度（硬件背光）" : "亮度（软件调光）", action: nil, keyEquivalent: "")
        briHead.isEnabled = false
        picItems.append(briHead)
        for (i, name) in kBrightnessNames.enumerated() {
            let it = NSMenuItem(title: "   \(name)",
                                action: #selector(pickBrightness(_:)), keyEquivalent: "")
            it.tag = i
            it.target = self
            it.state = abs(gammaBrightness * 100 - kBrightnessPercents[i]) < 0.6 ? .on : .off
            picItems.append(it)
        }
        picItems.append(.separator())
        for (i, name) in kTempNames.enumerated() {
            let it = NSMenuItem(title: name, action: #selector(pickTemp(_:)), keyEquivalent: "")
            it.tag = i
            it.target = self
            it.state = (i == gammaTempIndex) ? .on : .off
            picItems.append(it)
        }
        picItems.append(.separator())
        // 场景：跟 Windows 托盘菜单同构（亮度 / 色温 / 场景 / 恢复）
        for (i, sc) in kScenes.enumerated() {
            let it = NSMenuItem(title: "场景：" + sc.name, action: #selector(pickScene(_:)), keyEquivalent: "")
            it.tag = i
            it.target = self
            picItems.append(it)
        }
        picItems.append(.separator())
        let resetPic = NSMenuItem(title: "恢复原始画面", action: #selector(resetPicture(_:)), keyEquivalent: "")
        resetPic.target = self
        picItems.append(resetPic)
        menu.addItem(submenu("画面", picItems))

        let comp = NSMenuItem(title: "切换 320Hz 竞技模式", action: #selector(toggleCompetitive(_:)), keyEquivalent: "")
        comp.target = self
        menu.addItem(comp)

        menu.addItem(.separator())

        // 与 Windows 托盘同文案：睡眠时切到第 1 个源（醒来切到第 2 个）
        let auto = NSMenuItem(title: "睡眠时切到「\(allSources[0].title)」", action: #selector(toggleAuto(_:)), keyEquivalent: "")
        auto.target = self
        auto.state = UserDefaults.standard.bool(forKey: kAutoSleepKey) ? .on : .off
        menu.addItem(auto)

        menu.addItem(.separator())

        let hostItem = NSMenuItem(title: "设置显示器地址…", action: #selector(setHost(_:)), keyEquivalent: "")
        hostItem.target = self
        menu.addItem(hostItem)

        let find = NSMenuItem(title: "自动搜索局域网设备…", action: #selector(discover(_:)), keyEquivalent: "")
        find.target = self
        menu.addItem(find)

        let test = NSMenuItem(title: "测试连接", action: #selector(testConn), keyEquivalent: "")
        test.target = self
        menu.addItem(test)

        let logItem = NSMenuItem(title: "查看日志", action: #selector(openLog(_:)), keyEquivalent: "")
        logItem.target = self
        menu.addItem(logItem)

        menu.addItem(.separator())

        let login = NSMenuItem(title: "开机自动启动", action: #selector(toggleLogin(_:)), keyEquivalent: "")
        login.target = self
        login.state = (SMAppService.mainApp.status == .enabled) ? .on : .off
        menu.addItem(login)

        let quit = NSMenuItem(title: "退出", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)

        statusItem.menu = menu
    }

    @objc func menuSwitch(_ sender: NSMenuItem) {
        guard let key = sender.representedObject as? String else { return }
        performSwitch(key)
    }

    @objc func openPanel() {
        SettingsPanel.shared.show()
    }

    func performSwitch(_ key: String) {
        guard !busy else { return }
        if tvHost.isEmpty { setHost(nil); return }
        busy = true
        DispatchQueue.global().async {
            if deviceSources.isEmpty { refreshSources() }
            let ok = switchSource(key)
            let available = deviceSources.map { $0.name }.joined(separator: "、")
            DispatchQueue.main.async {
                self.busy = false
                if !ok {
                    if available.isEmpty {
                        self.alert("切换失败", "显示器无响应。\n\n可在菜单里「测试连接」检查地址是否正确。")
                    } else {
                        self.alert("切换失败", "设备没有提供「\(key)」这个输入。\n\n当前可用：\(available)\n\n如果设备刚改过接线，先在菜单里「测试连接」刷新一次列表。")
                    }
                }
            }
        }
    }

    func alert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.alertStyle = .warning
        a.addButton(withTitle: "好")
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }

    // MARK: 音量 / 画面 / 竞技模式

    @objc func volumeUp(_ sender: Any?) { stepVolume(up: true) }
    @objc func volumeDown(_ sender: Any?) { stepVolume(up: false) }

    @objc func showVolume(_ sender: Any?) {
        DispatchQueue.global().async {
            let v = readVolume()
            DispatchQueue.main.async {
                if let v = v {
                    self.alert("当前音量", "\(v.value) / \(v.max)")
                } else {
                    self.alert("读取失败", "设备没有返回音量，可能不支持音量接口。")
                }
            }
        }
    }

    /// 打开日志文件（与 Windows 托盘的「查看日志」对齐）
    @objc func openLog(_ sender: Any?) {
        if !FileManager.default.fileExists(atPath: kLogFile) {
            try? FileManager.default.createDirectory(
                atPath: (kLogFile as NSString).deletingLastPathComponent,
                withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: kLogFile, contents: nil)
        }
        NSWorkspace.shared.open(URL(fileURLWithPath: kLogFile))
    }

    @objc func pickBrightness(_ sender: NSMenuItem) {
        guard sender.tag >= 0 && sender.tag < kBrightnessPercents.count else { return }
        setBrightnessPercent(kBrightnessPercents[sender.tag])
        buildMenu()
        SettingsPanel.shared.syncPicture()
    }

    @objc func pickTemp(_ sender: NSMenuItem) {
        gammaTempIndex = sender.tag
        applyPicture()
        buildMenu()
        SettingsPanel.shared.syncPicture()
    }

    @objc func pickScene(_ sender: NSMenuItem) {
        applyScene(sender.tag)
        buildMenu()
        SettingsPanel.shared.syncPicture()
    }

    @objc func resetPicture(_ sender: Any?) {
        setBrightnessPercent(100)
        gammaTempIndex = 0
        applyPicture()
        buildMenu()
        SettingsPanel.shared.syncPicture()
        logLine("画面已恢复")
    }

    @objc func toggleCompetitive(_ sender: Any?) {
        let a = NSAlert()
        a.messageText = "切换 320Hz 竞技模式"
        a.informativeText = "适用于 G Pro 27U 这类双模显示器：在 4K 160Hz 与 1080p 320Hz 之间切换。\n\n切换后显示器会换 EDID，屏幕黑一下再回来，系统需要几秒重新识别。"
        a.addButton(withTitle: "切换到 320Hz")
        a.addButton(withTitle: "切回 4K 160Hz")
        a.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        let r = a.runModal()
        if r == .alertFirstButtonReturn { setCompetitiveMode(on: true) }
        else if r == .alertSecondButtonReturn { setCompetitiveMode(on: false) }
    }

    // MARK: 地址设置

    @objc func setHost(_ sender: Any?) {
        let a = NSAlert()
        a.messageText = "设置显示器地址"
        a.informativeText = "输入小米 / 红米电视或智能显示器的局域网 IP。\n需要设备已联网，且 6095 端口可访问。"
        a.addButton(withTitle: "保存")
        a.addButton(withTitle: "取消")

        let tf = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 24))
        tf.stringValue = tvHost
        tf.placeholderString = "192.168.1.100"
        a.accessoryView = tf
        a.window.initialFirstResponder = tf

        NSApp.activate(ignoringOtherApps: true)
        if a.runModal() == .alertFirstButtonReturn {
            let v = tf.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            writeHost(v)
            buildMenu()
        }
    }

    @objc func discover(_ sender: Any?) {
        let a = NSAlert()
        a.messageText = "正在搜索局域网…"
        a.informativeText = "扫描本机所在网段的 6095 端口，大约需要几秒。"
        a.addButton(withTitle: "搜索")
        a.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        guard a.runModal() == .alertFirstButtonReturn else { return }

        DispatchQueue.global().async {
            let found = discoverTVs { _ in }
            DispatchQueue.main.async {
                if found.isEmpty {
                    self.alert("没有找到设备", "本机网段内没有响应 6095 端口的设备。\n\n请确认显示器已连接 Wi-Fi 或网线，并与本机处于同一网段。")
                    return
                }
                let pick = NSAlert()
                pick.messageText = "发现 \(found.count) 台设备"
                pick.informativeText = "选择要控制的一台："
                for f in found {
                    pick.addButton(withTitle: "\(f.name)\(f.isMonitor ? "（显示器）" : "")")
                }
                pick.addButton(withTitle: "取消")
                NSApp.activate(ignoringOtherApps: true)
                let idx = pick.runModal().rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
                if idx >= 0 && idx < found.count {
                    writeHost(found[idx].ip)
                    self.buildMenu()
                    self.alert("已保存", "\(found[idx].name)\n\(found[idx].ip)")
                }
            }
        }
    }

    @objc func testConn() {
        if tvHost.isEmpty { setHost(nil); return }
        let host = tvHost
        DispatchQueue.global().async {
            // isalive 拿设备名和平台，getsources 拿真实输入列表
            let alive = apiPath("/request?action=isalive")
            let name = deviceName(from: alive.body)
            let platform = number(from: alive.body, "platform")
            let version = number(from: alive.body, "version")
            let sources = fetchSources()
            if !sources.isEmpty { setDeviceSources(sources) }
            let info = api("getsysteminfo")

            var lines = ["地址：\(host)"]
            if let n = name { lines.append("设备：\(n)") }
            if let p = platform {
                var s = "平台：\(p)"
                if let v = version { s += "    固件：\(v)" }
                lines.append(s)
            }
            if sources.isEmpty {
                lines.append("信号源：设备没提供列表（会退回按名字切换）")
            } else {
                lines.append("信号源：" + sources.map { "\($0.name)=\($0.id)" }.joined(separator: "  "))
                lines.append("切换方式：changesource2（按编号，DP / USB-C 可用）")
            }
            let kind = looksLikeMonitor(name: name ?? "", sources: sources) ? "显示器" : "电视或其他设备"
            lines.append("判定：\(kind)")
            if alive.code != 200 && info.code != 200 {
                lines.append("HTTP：isalive \(alive.code) / getsysteminfo \(info.code)")
            }

            let text = lines.joined(separator: "\n")
            DispatchQueue.main.async {
                self.buildMenu()
                self.alert(alive.code == 200 || info.code == 200 ? "连接正常" : "连接失败", text)
            }
        }
    }

    // MARK: 全局热键

    /// 当前注册的 (动作 id, 引用)，改键时先全部注销再重注册
    private static var registeredRefs: [EventHotKeyRef] = []
    /// hkID → 动作 id
    private static var idToAction: [UInt32: String] = [:]
    private static var handlerInstalled = false

    func registerHotKeys() {
        if !AppDelegate.handlerInstalled {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                     eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { (_, event, _) -> OSStatus in
                var hkID = EventHotKeyID()
                GetEventParameter(event, EventParamName(kEventParamDirectObject),
                                  EventParamType(typeEventHotKeyID), nil,
                                  MemoryLayout<EventHotKeyID>.size, nil, &hkID)
                let rid = hkID.id
                DispatchQueue.main.async {
                    if let action = AppDelegate.idToAction[rid] { runAction(action) }
                }
                return noErr
            }, 1, &spec, nil, nil)
            AppDelegate.handlerInstalled = true
        }

        reloadHotkeys()
    }

    /// 按当前配置重新注册所有全局热键
    func reloadHotkeys() {
        for ref in AppDelegate.registeredRefs { UnregisterEventHotKey(ref) }
        AppDelegate.registeredRefs.removeAll()
        AppDelegate.idToAction.removeAll()

        var next: UInt32 = 1
        for a in allHotkeyActions() {
            guard let hk = hotkey(for: a.id) else { continue }
            var ref: EventHotKeyRef?
            let hkID = EventHotKeyID(signature: OSType(0x4D545653), id: next)
            let st = RegisterEventHotKey(hk.keyCode, hk.mods, hkID,
                                         GetApplicationEventTarget(), 0, &ref)
            if st == noErr, let r = ref {
                AppDelegate.registeredRefs.append(r)
                AppDelegate.idToAction[next] = a.id
                next += 1
            } else {
                logLine("热键注册失败：\(a.title) \(formatHotkey(hk)) status=\(st)")
            }
        }
        logLine("热键已注册 \(AppDelegate.registeredRefs.count) 个")
    }

    // MARK: 睡眠 / 唤醒

    func observeSleepWake() {
        let nc = NSWorkspace.shared.notificationCenter
        nc.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { _ in
            // 唤醒后 Gamma 不一定还在，重新套一遍
            if gammaBrightness < 0.999 || gammaTempIndex != 0 { applyPicture() }
            guard UserDefaults.standard.bool(forKey: kAutoSleepKey) else { return }
            if allSources.count > 1 { self.performSwitch(allSources[1].key) }
        }
        nc.addObserver(forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) { _ in
            guard UserDefaults.standard.bool(forKey: kAutoSleepKey) else { return }
            self.performSwitch(allSources[0].key)
        }
    }

    @objc func toggleAuto(_ sender: NSMenuItem) {
        let on = !UserDefaults.standard.bool(forKey: kAutoSleepKey)
        UserDefaults.standard.set(on, forKey: kAutoSleepKey)
        sender.state = on ? .on : .off
    }

    @objc func toggleLogin(_ sender: NSMenuItem) {
        do {
            if SMAppService.mainApp.status == .enabled {
                try SMAppService.mainApp.unregister()
                sender.state = .off
            } else {
                try SMAppService.mainApp.register()
                sender.state = .on
            }
        } catch {
            alert("开机启动设置失败", "\(error.localizedDescription)\n\n请先把 App 放到 /Applications 后再试。")
        }
    }
}

// MARK: - 启动

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let delegate = AppDelegate.shared
app.delegate = delegate
app.run()
