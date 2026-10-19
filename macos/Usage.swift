//
//  屏幕时间统计
//  SPDX-License-Identifier: GPL-3.0-only
//
//  每 30 秒采样一次：屏幕亮着就算「亮屏」，同时最近 5 分钟内有过
//  键鼠/触控板事件才算「使用」。数据按天存 JSON，保留 60 天。
//  这个功能不依赖显示器协议，任何显示器都能用。
//

import Cocoa

struct DayUsage: Codable {
    var on: Double = 0        // 亮屏秒数
    var active: Double = 0    // 使用秒数（有输入事件）
}

final class UsageTracker {
    static let shared = UsageTracker()

    private let dir = (("~/Library/Application Support/MiTVSwitch") as NSString).expandingTildeInPath
    private var file: String { dir + "/usage.json" }

    private var days: [String: DayUsage] = [:]
    private var timer: Timer?
    private let sampleInterval: Double = 30
    private let keepDays = 60

    private static let dayFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    var onChange: (() -> Void)?

    func start() {
        load()
        sample()
        timer = Timer.scheduledTimer(withTimeInterval: sampleInterval, repeats: true) { [weak self] _ in
            self?.sample()
        }
        timer?.tolerance = 5
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    // MARK: 采样

    /// 最近一次任意输入距今的秒数（用一个不存在的 CGEventType 取「全部事件」）
    private func idleSeconds() -> Double {
        let anyEvent = CGEventType(rawValue: ~0) ?? .null
        return CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyEvent)
    }

    private func sample() {
        // 屏幕睡着时不计入
        guard CGDisplayIsAsleep(CGMainDisplayID()) == 0 else { return }

        let key = UsageTracker.dayFormatter.string(from: Date())
        var d = days[key] ?? DayUsage()
        d.on += sampleInterval
        if idleSeconds() < 300 { d.active += sampleInterval }
        days[key] = d

        prune()
        save()
        onChange?()
    }

    private func prune() {
        guard days.count > keepDays else { return }
        let sorted = days.keys.sorted()
        for k in sorted.prefix(days.count - keepDays) { days.removeValue(forKey: k) }
    }

    // MARK: 读写

    private func load() {
        guard let data = FileManager.default.contents(atPath: file),
              let decoded = try? JSONDecoder().decode([String: DayUsage].self, from: data) else { return }
        days = decoded
    }

    private func save() {
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(days) else { return }
        try? data.write(to: URL(fileURLWithPath: file), options: .atomic)
    }

    // MARK: 查询

    func today() -> DayUsage {
        days[UsageTracker.dayFormatter.string(from: Date())] ?? DayUsage()
    }

    /// 最近 n 天，按时间正序
    func recent(_ n: Int) -> [(key: String, label: String, usage: DayUsage)] {
        var out: [(String, String, DayUsage)] = []
        let cal = Calendar.current
        let labelFmt = DateFormatter()
        labelFmt.dateFormat = "M/d"
        for i in stride(from: n - 1, through: 0, by: -1) {
            guard let date = cal.date(byAdding: .day, value: -i, to: Date()) else { continue }
            let key = UsageTracker.dayFormatter.string(from: date)
            out.append((key, labelFmt.string(from: date), days[key] ?? DayUsage()))
        }
        return out
    }

    static func duration(_ seconds: Double) -> String {
        let s = Int(seconds.rounded())
        if s < 60 { return "\(s) 秒" }
        let m = s / 60
        if m < 60 { return "\(m) 分钟" }
        return "\(m / 60) 小时 \(m % 60) 分"
    }
}
