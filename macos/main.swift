//
//  MiTVSwitch — 小米 / 红米电视与智能显示器的信号源一键切换
//
//  SPDX-License-Identifier: CC-BY-NC-SA-4.0
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
// key   : 传给 changesource 的源名，大小写不敏感
// title : 菜单里显示的名字
// 热键  : 0 表示不绑定。默认给前两个绑 ⌃⌥1 / ⌃⌥2。
//         按自己的设备改这里的顺序和名字即可。

struct TVSource {
    let key: String
    let title: String
    let hotKeyCode: UInt32
    let hotMods: UInt32
}

let allSources: [TVSource] = [
    TVSource(key: "HDMI1", title: "HDMI 1",     hotKeyCode: UInt32(kVK_ANSI_1), hotMods: UInt32(controlKey | optionKey)),
    TVSource(key: "HDMI2", title: "HDMI 2",     hotKeyCode: UInt32(kVK_ANSI_2), hotMods: UInt32(controlKey | optionKey)),
    TVSource(key: "DP",    title: "DisplayPort", hotKeyCode: 0, hotMods: 0),
    TVSource(key: "USBC",  title: "USB-C",       hotKeyCode: 0, hotMods: 0),
]

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

@discardableResult
func api(_ action: String) -> (code: Int, body: String) {
    let host = tvHost
    guard !host.isEmpty else { return (-3, "尚未设置显示器地址") }

    let path = "/controller?action=\(action)"

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

func switchSource(_ key: String) -> Bool {
    let r = api("changesource&source=\(key)")
    let ok = r.code == 200 && r.body.contains("\"success\"")
    logLine("changesource \(key) -> HTTP \(r.code) \(r.body.prefix(160))")
    return ok
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
func discoverTVs(progress: @escaping (Int) -> Void) -> [(ip: String, name: String)] {
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
        return (ip, name)
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate {
    static let shared = AppDelegate()

    var statusItem: NSStatusItem!
    var busy = false

    func applicationDidFinishLaunching(_ note: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            button.image = NSImage(systemSymbolName: "rectangle.on.rectangle", accessibilityDescription: "信号源")
            button.image?.isTemplate = true
        }
        buildMenu()
        registerHotKeys()
        observeSleepWake()

        if tvHost.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { self.setHost(nil) }
        } else {
            DispatchQueue.global().async {
                let host = readHost()
                let r = api("getsysteminfo")
                logLine("启动自检 \(host) HTTP \(r.code)")
            }
        }
    }

    // MARK: 菜单

    func buildMenu() {
        let menu = NSMenu()

        let head = NSMenuItem(title: tvHost.isEmpty ? "未设置显示器地址" : "显示器信号源 · \(tvHost)", action: nil, keyEquivalent: "")
        head.isEnabled = false
        menu.addItem(head)
        menu.addItem(.separator())

        for s in allSources {
            var title = "   " + s.title
            if s.hotKeyCode == UInt32(kVK_ANSI_1) { title += "    ⌃⌥1" }
            if s.hotKeyCode == UInt32(kVK_ANSI_2) { title += "    ⌃⌥2" }
            let item = NSMenuItem(title: title, action: #selector(menuSwitch(_:)), keyEquivalent: "")
            item.representedObject = s.key
            item.target = self
            menu.addItem(item)
        }

        menu.addItem(.separator())

        let auto = NSMenuItem(title: "睡眠时切到第 1 个源", action: #selector(toggleAuto(_:)), keyEquivalent: "")
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

    func performSwitch(_ key: String) {
        guard !busy else { return }
        if tvHost.isEmpty { setHost(nil); return }
        busy = true
        DispatchQueue.global().async {
            let ok = switchSource(key)
            DispatchQueue.main.async {
                self.busy = false
                if !ok {
                    self.alert("切换失败", "显示器无响应，或该源名不被设备接受（\(key)）。\n\n可在菜单里「测试连接」检查地址是否正确。")
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
                for f in found { pick.addButton(withTitle: f.name) }
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
        let r = api("getsysteminfo")
        if r.code == 200 && r.body.contains("success") {
            alert("连接正常", "地址：\(tvHost)\n\n\(r.body)")
        } else {
            alert("连接失败", "地址：\(tvHost)\nHTTP \(r.code)\n\(r.body)")
        }
    }

    // MARK: 全局热键

    func registerHotKeys() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                 eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { (_, event, _) -> OSStatus in
            var hkID = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject),
                              EventParamType(typeEventHotKeyID), nil,
                              MemoryLayout<EventHotKeyID>.size, nil, &hkID)
            let idx = Int(hkID.id)
            DispatchQueue.main.async {
                if idx >= 0 && idx < allSources.count {
                    AppDelegate.shared.performSwitch(allSources[idx].key)
                }
            }
            return noErr
        }, 1, &spec, nil, nil)

        for (i, s) in allSources.enumerated() where s.hotKeyCode != 0 {
            var ref: EventHotKeyRef?
            let hkID = EventHotKeyID(signature: OSType(0x4D545653), id: UInt32(i))
            if RegisterEventHotKey(s.hotKeyCode, s.hotMods, hkID,
                                   GetApplicationEventTarget(), 0, &ref) != noErr {
                logLine("热键注册失败：\(s.title)")
            }
        }
    }

    // MARK: 睡眠 / 唤醒

    func observeSleepWake() {
        let nc = NSWorkspace.shared.notificationCenter
        nc.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { _ in
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
