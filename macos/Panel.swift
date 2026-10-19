//
//  设置面板
//  SPDX-License-Identifier: GPL-3.0-only
//
//  分区：设备状态 / 信号源 / 音量 / 画面 / 场景 / 分辨率 / 屏幕时间 / 快捷键
//  布局与交互按本项目重写（参考过 MonitorBar 的分区思路，未照抄其实现）。
//

import Cocoa
import Carbon

// MARK: - 快捷键录制

func carbonMods(_ f: NSEvent.ModifierFlags) -> UInt32 {
    var m: UInt32 = 0
    if f.contains(.control) { m |= UInt32(controlKey) }
    if f.contains(.option)  { m |= UInt32(optionKey) }
    if f.contains(.shift)   { m |= UInt32(shiftKey) }
    if f.contains(.command) { m |= UInt32(cmdKey) }
    return m
}

final class KeyCaptureView: NSView {
    var onCapture: ((HotkeySpec?) -> Void)?
    override var acceptsFirstResponder: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.windowBackgroundColor.setFill()
        dirtyRect.fill()
    }

    func addHint(_ title: String) {
        let l = NSTextField(wrappingLabelWithString:
            "\(title)\n\n按下新的组合键（需含 ⌃ ⌥ ⇧ ⌘ 至少一个）\nEsc 取消 · Delete 清除绑定")
        l.alignment = .center
        l.font = .systemFont(ofSize: 12)
        l.textColor = .labelColor
        l.frame = NSRect(x: 24, y: 24, width: 292, height: 96)
        l.autoresizingMask = [.width, .height]
        addSubview(l)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == UInt16(kVK_Escape) { onCapture?(nil); return }
        if event.keyCode == UInt16(kVK_Delete) || event.keyCode == UInt16(kVK_ForwardDelete) {
            onCapture?(HotkeySpec(keyCode: 0, mods: 0))
            return
        }
        let mods = carbonMods(event.modifierFlags)
        if mods == 0 { NSSound.beep(); return }
        onCapture?(HotkeySpec(keyCode: UInt32(event.keyCode), mods: mods))
    }
}

/// 让滚动视图从顶部开始 —— 不翻转的话 NSScrollView 默认停在左下角
final class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

// MARK: - 7 天柱状图
final class UsageChartView: NSView {
    private var data: [(label: String, value: Double)] = []

    func update(_ d: [(label: String, value: Double)]) {
        data = d
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let barW: CGFloat = 22
        let gap: CGFloat = 12
        let bottom: CGFloat = 16
        let maxV = max(data.map(\.value).max() ?? 0, 60)
        let text: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 9),
            .foregroundColor: NSColor.tertiaryLabelColor,
        ]

        for (i, item) in data.enumerated() {
            let x = CGFloat(i) * (barW + gap) + 6
            let h = CGFloat(item.value / maxV) * (bounds.height - bottom - 6)
            let bar = NSRect(x: x, y: bottom, width: barW, height: max(h, 1))
            let path = NSBezierPath(roundedRect: bar, xRadius: 4, yRadius: 4)
            (i == data.count - 1 ? NSColor.controlAccentColor : NSColor.separatorColor).setFill()
            path.fill()

            let label = NSAttributedString(string: item.label, attributes: text)
            let size = label.size()
            label.draw(at: NSPoint(x: x + (barW - size.width) / 2, y: 2))
        }
    }
}

// MARK: - 设置面板

final class SettingsPanel: NSObject {
    static let shared = SettingsPanel()

    private var window: NSWindow?
    private var volumeSlider: NSSlider?
    private var volumeValue: NSTextField?
    private var brightnessSlider: NSSlider?
    private var brightnessValue: NSTextField?
    private var deviceLabel: NSTextField?
    private var ddcLabel: NSTextField?
    private var usageChart: UsageChartView?
    private var usageOnLabel: NSTextField?
    private var usageActiveLabel: NSTextField?
    private var resPopup: NSPopUpButton?
    private var tempPopup: NSPopUpButton?
    private var resModes: [ResMode] = []
    private var volumeBusy = false

    func toggle() {
        if window?.isVisible == true { hide() } else { show() }
    }

    func hide() { window?.orderOut(nil) }

    func show() {
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 420, height: 600),
                             styleMask: [.titled, .closable],
                             backing: .buffered, defer: false)
            w.title = "MiTVSwitch 设置"
            w.isReleasedWhenClosed = false
            w.level = .floating
            w.backgroundColor = .windowBackgroundColor
            window = w
        }
        window?.contentView = buildContent()
        window?.center()
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        refreshLiveInfo()
        refreshUsage()
    }

    /// 外部动作执行后刷新数值
    func refresh() {
        guard let w = window, w.isVisible else { return }
        let old = w.frame
        w.contentView = buildContent()
        w.setFrame(old, display: true)
        refreshLiveInfo()
        refreshUsage()
    }

    // MARK: 实时数据

    private func refreshLiveInfo() {
        deviceLabel?.stringValue = tvHost.isEmpty ? "未设置显示器地址" : "正在读取…"

        DispatchQueue.global().async { [weak self] in
            guard !tvHost.isEmpty else {
                DispatchQueue.main.async { self?.deviceLabel?.stringValue = "未设置显示器地址" }
                return
            }
            let name = deviceName(from: apiPath("/request?action=isalive").body)
            let vol = readVolume()
            if deviceSources.isEmpty { refreshSources() }
            DispatchQueue.main.async {
                self?.deviceLabel?.stringValue = name ?? tvHost
                self?.ddcLabel?.stringValue = brightnessBackendLabel
                if let v = vol {
                    self?.volumeValue?.stringValue = "\(v.value)"
                    self?.volumeSlider?.doubleValue = Double(v.value) / Double(v.max) * 100
                }
            }
        }
    }

    // MARK: 组件工厂

    private func caption(_ text: String) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.font = .systemFont(ofSize: 10, weight: .semibold)
        l.textColor = .tertiaryLabelColor
        return l
    }

    private func label(_ text: String, size: CGFloat = 12, color: NSColor = .labelColor) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.font = .systemFont(ofSize: size)
        l.textColor = color
        return l
    }

    private func button(_ title: String, _ action: Selector, w: CGFloat = 0, h: CGFloat = 28) -> NSButton {
        let b = NSButton(title: title, target: self, action: action)
        b.bezelStyle = .rounded
        b.controlSize = .regular
        b.focusRingType = .none          // 避免第一个控件常驻蓝色焦点框
        if w > 0 { b.widthAnchor.constraint(equalToConstant: w).isActive = true }
        b.heightAnchor.constraint(equalToConstant: h).isActive = true
        return b
    }

    private func hr() -> NSBox {
        let b = NSBox()
        b.boxType = .separator
        b.heightAnchor.constraint(equalToConstant: 1).isActive = true
        return b
    }

    private func row(_ views: [NSView], spacing: CGFloat = 8) -> NSStackView {
        let s = NSStackView(views: views)
        s.orientation = .horizontal
        s.alignment = .centerY
        s.spacing = spacing
        return s
    }

    // MARK: 主体

    private func buildContent() -> NSView {
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 9

        // ── 头部 ──
        let icon = NSImageView()
        icon.image = NSImage(systemSymbolName: "rectangle.on.rectangle", accessibilityDescription: nil)
        icon.contentTintColor = .controlAccentColor
        icon.symbolConfiguration = .init(pointSize: 20, weight: .regular)

        let title = NSTextField(labelWithString: "MiTVSwitch")
        title.font = .systemFont(ofSize: 16, weight: .semibold)

        let subtitle = label(tvHost.isEmpty ? "未设置显示器地址" : "正在读取…", size: 11, color: .secondaryLabelColor)
        deviceLabel = subtitle

        let head = row([icon, { () -> NSStackView in
            let v = NSStackView(views: [title, subtitle])
            v.orientation = .vertical
            v.alignment = .leading
            v.spacing = 1
            return v
        }()], spacing: 10)
        stack.addArrangedSubview(head)
        stack.addArrangedSubview(hr())

        // ── 信号源 ──
        stack.addArrangedSubview(caption("信号源"))
        var srcViews: [NSView] = []
        for s in allSources {
            let has = deviceSources.isEmpty || resolveSourceID(s.key) != nil
            let b = button(s.title, #selector(doSwitch(_:)), w: 176, h: 32)
            b.identifier = NSUserInterfaceItemIdentifier(s.key)
            b.isEnabled = has
            b.toolTip = has ? formatHotkey(hotkey(for: "src.\(s.key)")) : "设备没有报告这个输入"
            srcViews.append(b)
        }
        for i in stride(from: 0, to: srcViews.count, by: 2) {
            stack.addArrangedSubview(row(Array(srcViews[i..<min(i + 2, srcViews.count)])))
        }

        // ── 音量 / 画面 ──
        stack.addArrangedSubview(caption("音量与亮度"))
        func inlineLabel(_ t: String) -> NSTextField {
            let l = label(t, size: 11, color: .secondaryLabelColor)
            l.widthAnchor.constraint(equalToConstant: 30).isActive = true
            return l
        }

        let vol = NSSlider(value: 0, minValue: 0, maxValue: 100, target: self, action: #selector(volumeSliderChanged(_:)))
        vol.isContinuous = false        // 松手才发，避免每帧都发一串按键
        vol.widthAnchor.constraint(equalToConstant: 208).isActive = true
        volumeSlider = vol
        let volValue = label("—", size: 12, color: .secondaryLabelColor)
        volValue.widthAnchor.constraint(equalToConstant: 44).isActive = true
        volValue.alignment = .right
        volumeValue = volValue
        stack.addArrangedSubview(row([
            inlineLabel("音量"),
            button("−", #selector(volDown), w: 32),
            vol,
            button("+", #selector(volUp), w: 32),
            volValue,
        ], spacing: 7))

        let bri = NSSlider(value: gammaBrightness * 100, minValue: 30, maxValue: 100,
                           target: self, action: #selector(brightnessChanged(_:)))
        bri.isContinuous = true
        bri.widthAnchor.constraint(equalToConstant: 208).isActive = true
        brightnessSlider = bri
        let briValue = label("\(Int(gammaBrightness * 100))%", size: 12, color: .secondaryLabelColor)
        briValue.widthAnchor.constraint(equalToConstant: 44).isActive = true
        briValue.alignment = .right
        brightnessValue = briValue
        stack.addArrangedSubview(row([
            inlineLabel("亮度"),
            button("−", #selector(brightnessDown), w: 32),
            bri,
            button("+", #selector(brightnessUp), w: 32),
            briValue,
        ], spacing: 7))

        let temp = NSPopUpButton(frame: .zero, pullsDown: false)
        temp.addItems(withTitles: kTempNames)
        temp.selectItem(at: gammaTempIndex)
        temp.target = self
        temp.action = #selector(tempChanged(_:))
        temp.widthAnchor.constraint(equalToConstant: 130).isActive = true
        temp.focusRingType = .none
        tempPopup = temp
        let ddc = label("", size: 10, color: .tertiaryLabelColor)
        ddcLabel = ddc
        stack.addArrangedSubview(row([inlineLabel("色温"), temp, ddc], spacing: 10))

        // ── 场景 ──
        stack.addArrangedSubview(caption("场景"))
        var sceneViews: [NSView] = []
        for (i, sc) in kScenes.enumerated() {
            let b = button(sc.name, #selector(applySceneButton(_:)), w: 84, h: 30)
            b.tag = i
            sceneViews.append(b)
        }
        stack.addArrangedSubview(row(sceneViews, spacing: 6))

        let autoScene = NSButton(checkboxWithTitle: "按时间自动切换（日间 / 夜间）",
                                 target: self, action: #selector(toggleAutoScene(_:)))
        autoScene.state = UserDefaults.standard.bool(forKey: kAutoSceneKey) ? .on : .off
        autoScene.focusRingType = .none
        autoScene.font = .systemFont(ofSize: 11)
        stack.addArrangedSubview(autoScene)

        let dayPicker = timePicker(minutes: sceneDayMinutes, action: #selector(dayTimeChanged(_:)))
        let nightPicker = timePicker(minutes: sceneNightMinutes, action: #selector(nightTimeChanged(_:)))
        stack.addArrangedSubview(row([
            label("白天从", size: 11, color: .secondaryLabelColor), dayPicker,
            label("夜间从", size: 11, color: .secondaryLabelColor), nightPicker,
        ], spacing: 8))

        // ── 分辨率 ──
        stack.addArrangedSubview(caption("分辨率 / 刷新率"))
        let popup = NSPopUpButton(frame: .zero, pullsDown: false)
        resModes = listResolutions()
        popup.addItems(withTitles: resModes.isEmpty ? ["设备未提供可选模式"] : resModes.map(\.label))
        if let cur = currentResolution(),
           let idx = resModes.firstIndex(where: { $0.width == cur.width && $0.height == cur.height
                                                  && abs($0.hz - cur.hz) < 0.6 }) {
            popup.selectItem(at: idx)
        }
        popup.widthAnchor.constraint(equalToConstant: 240).isActive = true
        popup.focusRingType = .none
        resPopup = popup
        stack.addArrangedSubview(row([
            popup,
            button("应用", #selector(applyResolutionButton), w: 64),
            button("刷新列表", #selector(reloadResolutions), w: 84),
        ], spacing: 8))

        // ── 屏幕时间 ──
        stack.addArrangedSubview(caption("屏幕时间"))
        let onLbl = label("亮屏 —", size: 12)
        let actLbl = label("使用 —", size: 12, color: .secondaryLabelColor)
        usageOnLabel = onLbl
        usageActiveLabel = actLbl
        stack.addArrangedSubview(row([onLbl, actLbl], spacing: 14))

        let chart = UsageChartView()
        chart.widthAnchor.constraint(equalToConstant: 360).isActive = true
        chart.heightAnchor.constraint(equalToConstant: 86).isActive = true
        usageChart = chart
        stack.addArrangedSubview(chart)
        stack.addArrangedSubview(label("最近 7 天（柱高 = 使用时长）", size: 10, color: .tertiaryLabelColor))

        // ── 快捷键 ──
        stack.addArrangedSubview(caption("快捷键"))
        var lastGroup = ""
        for a in allHotkeyActions() {
            if a.group != lastGroup {
                let g = label(a.group, size: 10, color: .tertiaryLabelColor)
                stack.addArrangedSubview(g)
                lastGroup = a.group
            }
            let name = label(a.title, size: 12)
            name.widthAnchor.constraint(equalToConstant: 110).isActive = true
            let key = NSButton(title: formatHotkey(hotkey(for: a.id)), target: self,
                               action: #selector(recordHotkey(_:)))
            key.bezelStyle = .rounded
            key.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
            key.identifier = NSUserInterfaceItemIdentifier(a.id)
            key.focusRingType = .none
            key.widthAnchor.constraint(equalToConstant: 132).isActive = true
            stack.addArrangedSubview(row([name, key], spacing: 6))
        }
        stack.addArrangedSubview(row([
            button("恢复默认快捷键", #selector(resetKeys), w: 132),
            button("恢复原始画面", #selector(resetPicture), w: 120),
        ], spacing: 8))

        // ── 底部 ──
        stack.addArrangedSubview(hr())
        stack.addArrangedSubview(row([
            button("测试连接", #selector(doTest), w: 90),
            button("搜索设备…", #selector(doDiscover), w: 96),
            button("关闭", #selector(doClose), w: 64),
        ], spacing: 8))

        // 放进滚动视图
        let doc = FlippedView()
        stack.translatesAutoresizingMaskIntoConstraints = false
        doc.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: doc.leadingAnchor, constant: 18),
            stack.trailingAnchor.constraint(equalTo: doc.trailingAnchor, constant: -18),
            stack.topAnchor.constraint(equalTo: doc.topAnchor, constant: 16),
            stack.bottomAnchor.constraint(equalTo: doc.bottomAnchor, constant: -16),
        ])
        doc.layoutSubtreeIfNeeded()
        let width: CGFloat = 420
        let height = max(stack.fittingSize.height + 32, 600)
        doc.frame = NSRect(x: 0, y: 0, width: width, height: height)

        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: width, height: 600))
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.documentView = doc
        scroll.contentView.scroll(to: NSPoint(x: 0, y: 0))   // 翻转后 (0,0) 就是顶部
        scroll.reflectScrolledClipView(scroll.contentView)
        return scroll
    }

    private func timePicker(minutes: Int, action: Selector) -> NSDatePicker {
        let p = NSDatePicker()
        p.datePickerStyle = .textFieldAndStepper
        p.datePickerElements = .hourMinute
        p.datePickerMode = .single
        p.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        var comps = DateComponents()
        comps.hour = minutes / 60
        comps.minute = minutes % 60
        p.dateValue = Calendar.current.date(from: comps) ?? Date()
        p.target = self
        p.action = action
        p.widthAnchor.constraint(equalToConstant: 96).isActive = true
        return p
    }

    // MARK: 动作

    @objc private func doSwitch(_ sender: NSButton) {
        guard let key = sender.identifier?.rawValue else { return }
        AppDelegate.shared.performSwitch(key)
    }

    @objc private func volUp()   { stepVolume(up: true);  refreshLiveInfo() }
    @objc private func volDown() { stepVolume(up: false); refreshLiveInfo() }

    @objc private func volumeSliderChanged(_ sender: NSSlider) {
        guard !volumeBusy else { return }
        volumeBusy = true
        let target = Int(sender.doubleValue.rounded())
        DispatchQueue.global().async { [weak self] in
            // 接口只支持 ±1，按差值连发，最多 30 次免得拖太久
            var cur = readVolume()?.value ?? 0
            var delta = target - cur
            let cap = 30
            if abs(delta) > cap { delta = delta > 0 ? cap : -cap }
            var sent = 0
            while sent != delta {
                let up = delta > 0
                _ = pressKey(up ? "volumeup" : "volumedown")
                sent += up ? 1 : -1
                cur += up ? 1 : -1
                usleep(25_000)
            }
            DispatchQueue.main.async {
                self?.volumeValue?.stringValue = "\(cur)"
                self?.volumeSlider?.doubleValue = Double(cur)
                self?.volumeBusy = false
            }
        }
    }

    @objc private func brightnessChanged(_ sender: NSSlider) {
        setBrightnessPercent(sender.doubleValue)
        brightnessValue?.stringValue = "\(Int(sender.doubleValue.rounded()))%"
    }

    @objc private func brightnessUp()   { stepBrightness(+5) }
    @objc private func brightnessDown() { stepBrightness(-5) }

    @objc private func tempChanged(_ sender: NSPopUpButton) {
        gammaTempIndex = sender.indexOfSelectedItem
        applyPicture()
        AppDelegate.shared.buildMenu()
    }

    @objc private func applySceneButton(_ sender: NSButton) {
        applyScene(sender.tag)
        syncPicture()
    }

    @objc private func toggleAutoScene(_ sender: NSButton) {
        UserDefaults.standard.set(sender.state == .on, forKey: kAutoSceneKey)
        checkAutoScene(force: sender.state == .on)
    }

    @objc private func dayTimeChanged(_ sender: NSDatePicker) {
        UserDefaults.standard.set(minutesOfDay(sender.dateValue), forKey: kSceneDayKey)
        checkAutoScene(force: true)
    }

    @objc private func nightTimeChanged(_ sender: NSDatePicker) {
        UserDefaults.standard.set(minutesOfDay(sender.dateValue), forKey: kSceneNightKey)
        checkAutoScene(force: true)
    }

    @objc private func applyResolutionButton() {
        guard let i = resPopup?.indexOfSelectedItem, i >= 0, i < resModes.count else { return }
        if applyResolution(resModes[i]) {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { self.refresh() }
        }
    }

    @objc private func reloadResolutions() {
        refresh()
    }

    @objc private func doTest() {
        AppDelegate.shared.testConn()
        refresh()
    }

    @objc private func doDiscover() {
        hide()
        AppDelegate.shared.discover(nil)
        DispatchQueue.main.asyncAfter(deadline: .now() + 12) { [weak self] in self?.refresh() }
    }

    @objc private func doClose() { hide() }

    @objc private func resetKeys() {
        resetHotkeys()
        AppDelegate.shared.reloadHotkeys()
        AppDelegate.shared.buildMenu()
        refresh()
    }

    @objc private func resetPicture() {
        setBrightnessPercent(100)
        gammaTempIndex = 0
        applyPicture()
        AppDelegate.shared.buildMenu()
        syncPicture()
    }

    @objc private func recordHotkey(_ sender: NSButton) {
        guard let action = sender.identifier?.rawValue else { return }
        let title = allHotkeyActions().first(where: { $0.id == action })?.title ?? action

        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 340, height: 132),
                         styleMask: [.titled], backing: .buffered, defer: false)
        w.title = "设置快捷键"
        w.level = .floating
        w.isReleasedWhenClosed = false

        let v = KeyCaptureView(frame: NSRect(x: 0, y: 0, width: 340, height: 132))
        v.addHint(title)
        v.onCapture = { spec in
            w.orderOut(nil)
            if let s = spec {
                if s.keyCode == 0 && s.mods == 0 { setHotkey(nil, for: action) }
                else { setHotkey(s, for: action) }
            }
            AppDelegate.shared.reloadHotkeys()
            AppDelegate.shared.buildMenu()
            self.refresh()
            logLine("快捷键 \(action) -> \(formatHotkey(hotkey(for: action)))")
        }
        w.contentView = v
        w.center()
        w.makeKeyAndOrderFront(nil)
        w.makeFirstResponder(v)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// 只同步画面相关控件，不重建整个面板（避免热键调亮度把滚动位置弹回顶部）
    func syncPicture() {
        let p = gammaBrightness * 100
        brightnessSlider?.doubleValue = p
        let fixed = Int(p.rounded())
        if brightnessValue?.stringValue != "\(fixed)%" {
            brightnessValue?.stringValue = "\(fixed)%"
        }
        if let pop = tempPopup, pop.indexOfSelectedItem != gammaTempIndex {
            pop.selectItem(at: gammaTempIndex)
        }
        ddcLabel?.stringValue = brightnessBackendLabel
    }

    /// 屏幕时间面板刷新（由 UsageTracker 的 onChange 触发）
    func refreshUsage() {
        let t = UsageTracker.shared.today()
        usageOnLabel?.stringValue = "亮屏 \(UsageTracker.duration(t.on))"
        usageActiveLabel?.stringValue = "使用 \(UsageTracker.duration(t.active))"
        let week = UsageTracker.shared.recent(7)
        usageChart?.update(week.map { ($0.label, $0.usage.active) })
    }
}
