//
//  显示器能力：分辨率 / 刷新率 + DDC/CI 硬件背光
//  SPDX-License-Identifier: GPL-3.0-only
//
//  分辨率走公开的 CoreGraphics 接口，任何显示器都能用。
//  DDC/CI 需要 IOKit 的私有符号 IOAVService*，用 dlsym 取；
//  Apple Silicon 上只有 DP / USB-C 链路通，HDMI 不行（实测 ioreg 无 IOAVService），
//  所以这里全部做了失败兜底，取不到就退回软件调光。
//

import Cocoa
import IOKit

// MARK: - 分辨率 / 刷新率

struct ResMode {
    let width: Int
    let height: Int
    let hz: Double
    let mode: CGDisplayMode

    var label: String {
        hz > 0 ? "\(width) × \(height)  @\(Int(hz.rounded()))Hz" : "\(width) × \(height)"
    }
}

func currentResolution() -> ResMode? {
    guard let m = CGDisplayCopyDisplayMode(CGMainDisplayID()) else { return nil }
    return ResMode(width: m.width, height: m.height,
                   hz: m.refreshRate, mode: m)
}

/// 列出可用模式，按 宽×高@刷新率 去重，优先保留可用于桌面的那条
func listResolutions() -> [ResMode] {
    let opts = [kCGDisplayShowDuplicateLowResolutionModes: true] as CFDictionary
    guard let arr = CGDisplayCopyAllDisplayModes(CGMainDisplayID(), opts) as? [CGDisplayMode] else {
        return []
    }

    var seen = Set<String>()
    var out: [ResMode] = []
    for m in arr {
        guard m.isUsableForDesktopGUI() else { continue }
        let key = "\(m.width)x\(m.height)@\(Int(m.refreshRate.rounded()))"
        if seen.contains(key) { continue }
        seen.insert(key)
        out.append(ResMode(width: m.width, height: m.height, hz: m.refreshRate, mode: m))
    }
    // 同分辨率下把高刷排前面，整体按分辨率降序
    return out.sorted {
        if $0.width != $1.width { return $0.width > $1.width }
        if $0.height != $1.height { return $0.height > $1.height }
        return $0.hz > $1.hz
    }
}

@discardableResult
func applyResolution(_ m: ResMode) -> Bool {
    let err = CGDisplaySetDisplayMode(CGMainDisplayID(), m.mode, nil)
    logLine("切换分辨率 \(m.label) -> \(err == .success ? "OK" : "失败 \(err.rawValue)")")
    return err == .success
}

// MARK: - DDC/CI（实验性）
//
// 报文格式与重试策略参考 MonitorControl（MIT，© MonitorControl 贡献者），见 NOTICE：
//   [0x80|len, len, ...send, checksum]
//   写：send = [VCP, hi, lo]   → 0x84, 0x03, 0x10, hi, lo, chk
//   读：send = [VCP]           → 0x82, 0x01, 0x10, chk，随后从 offset 0 读 11 字节
// 校验和种子：3 字节报文为 (0x37<<1)^0x51，1 字节报文为 (0x37<<1)；回包种子固定 0x50。
// 单次写经常失败，所以默认写 2 轮 × 最多重试 5 次，每次之间留 10~20ms 给显示器反应。

/// 通过 IOAVService 读写显示器 I²C，用于硬件背光。
/// 取不到服务或写入失败时 working 会是 false，调用方应退回软件调光。
final class DDC {
    static let shared = DDC()

    private typealias CreateFn = @convention(c) (CFAllocator?, io_service_t) -> UnsafeMutableRawPointer?
    private typealias RwFn = @convention(c) (UnsafeMutableRawPointer, UInt32, UInt32,
                                             UnsafeMutableRawPointer, UInt32) -> kern_return_t

    private static let chipAddress: UInt32 = 0x37       // DP / USB-C 链路上可用
    private static let dataAddress: UInt32 = 0x51
    private static let vcpBrightness: UInt8 = 0x10

    private var service: UnsafeMutableRawPointer?
    private var writeFn: RwFn?
    private var readFn: RwFn?

    /// 符号齐全且拿到了显示器服务
    private(set) var available = false
    /// 真正读或写成功过至少一次
    private(set) var working = false
    /// 探活跑过了（不管成没成），面板据此决定文案
    private(set) var probed = false

    private init() { setup() }

    // MARK: 取服务

    private func setup() {
        guard let h = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW) else {
            logLine("DDC: 打不开 IOKit")
            return
        }
        guard let cs = dlsym(h, "IOAVServiceCreateWithService"),
              let ws = dlsym(h, "IOAVServiceWriteI2C"),
              let rs = dlsym(h, "IOAVServiceReadI2C") else {
            logLine("DDC: 没有 IOAVService 符号")
            return
        }
        let create = unsafeBitCast(cs, to: CreateFn.self)
        writeFn = unsafeBitCast(ws, to: RwFn.self)
        readFn = unsafeBitCast(rs, to: RwFn.self)

        guard let s = pickService(create) else {
            logLine("DDC: 没有可用的 DCPAVServiceProxy（HDMI 直连时正常）")
            return
        }
        service = s
        available = true
        logLine("DDC: 服务已就绪（尚未验证能否通信）")
    }

    /// 机器上可能有多个 DCPAVServiceProxy（每个显示链路一个），优先取外接的那个
    private func pickService(_ create: CreateFn) -> UnsafeMutableRawPointer? {
        var iter: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault,
                                           IOServiceMatching("DCPAVServiceProxy"), &iter) == KERN_SUCCESS
        else { return nil }
        defer { IOObjectRelease(iter) }

        var fallback: UnsafeMutableRawPointer?
        while true {
            let svc = IOIteratorNext(iter)
            if svc == 0 { break }

            let supported = IORegistryEntryCreateCFProperty(
                svc, "IOAVServiceUserInterfaceSupported" as CFString, kCFAllocatorDefault, 0
            )?.takeRetainedValue() as? Bool
            let location = IORegistryEntryCreateCFProperty(
                svc, "Location" as CFString, kCFAllocatorDefault, 0
            )?.takeRetainedValue() as? String

            if supported == false { IOObjectRelease(svc); continue }
            guard let s = create(kCFAllocatorDefault, svc) else {
                IOObjectRelease(svc); continue
            }
            if location == "External" {
                if let old = fallback { _ = old }
                IOObjectRelease(svc)
                return s
            }
            if fallback == nil { fallback = s }
            IOObjectRelease(svc)
        }
        return fallback
    }

    // MARK: 报文

    private func checksum(seed: UInt8, _ bytes: [UInt8], end: Int) -> UInt8 {
        var c = seed
        for i in 0...end { c ^= bytes[i] }
        return c
    }

    /// send 是报文数据区（不含长度与校验和）
    private func packet(_ send: [UInt8]) -> [UInt8] {
        var p: [UInt8] = [UInt8(0x80 | (send.count + 1)), UInt8(send.count)] + send + [0]
        let seed: UInt8 = send.count == 1
            ? (UInt8(Self.chipAddress) << 1)
            : (UInt8(Self.chipAddress) << 1) ^ UInt8(Self.dataAddress)
        p[p.count - 1] = checksum(seed: seed, p, end: p.count - 2)
        return p
    }

    /// 读一次亮度（Get VCP 0x10）。这条路不改变任何状态，适合拿来做探活。
    func readBrightness() -> (current: Int, max: Int)? {
        guard available, let svc = service, let w = writeFn, let r = readFn else { return nil }

        var req = packet([Self.vcpBrightness])
        var reply = [UInt8](repeating: 0, count: 11)

        for _ in 1...5 {
            usleep(10_000)
            let wkr = req.withUnsafeMutableBytes { buf in
                w(svc, Self.chipAddress, Self.dataAddress, buf.baseAddress!, UInt32(buf.count))
            }
            if wkr == 0 {
                usleep(50_000)
                let rkr = reply.withUnsafeMutableBytes { buf in
                    r(svc, Self.chipAddress, 0, buf.baseAddress!, UInt32(buf.count))
                }
                if rkr == 0,
                   checksum(seed: 0x50, reply, end: reply.count - 2) == reply[reply.count - 1] {
                    let mx = Int(reply[6]) * 256 + Int(reply[7])
                    let cur = Int(reply[8]) * 256 + Int(reply[9])
                    if mx > 0 {
                        working = true
                        return (cur, mx)
                    }
                }
            }
            usleep(20_000)
        }
        return nil
    }

    /// 设置硬件背光，0...100
    @discardableResult
    func setBrightness(_ percent: Int) -> Bool {
        guard available, let svc = service, let w = writeFn else { return false }

        let v = UInt16(max(0, min(100, percent)))
        // 有些显示器按 0…max 而不是 0…100，先读一次拿到量程
        let maxV = readBrightness()?.max ?? 100
        let scaled = UInt16((Int(v) * maxV + 50) / 100)

        var req = packet([Self.vcpBrightness, UInt8(scaled >> 8), UInt8(scaled & 0xFF)])

        for _ in 1...5 {
            // 每轮写两次，DDC 首包丢包率很高
            for _ in 1...2 {
                usleep(10_000)
                let kr = req.withUnsafeMutableBytes { buf in
                    w(svc, Self.chipAddress, Self.dataAddress, buf.baseAddress!, UInt32(buf.count))
                }
                if kr == 0 {
                    working = true
                    return true
                }
            }
            usleep(20_000)
        }
        working = false
        return false
    }

    /// 非侵入探活：只读不写
    func probe() -> Bool {
        if working { probed = true; return true }
        let ok = readBrightness() != nil
        probed = true
        return ok
    }
}

