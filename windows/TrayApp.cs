// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiTVSwitch
{
    internal sealed class Source
    {
        public string Key = "";
        public string Title = "";
    }

    internal sealed class TrayApp : ApplicationContext
    {
        // ───────────────────────────────────────────────────────────
        //  想改信号源名字、增减接口，改这张表就行。
        //  Key   : 源名，大小写不敏感；程序启动时用 getsources 解析成编号
        //  Title : 界面里显示的名字
        //  默认快捷键在 Hotkeys.Defaults() 里，用户可在设置面板里改。
        //  数组第 1、2 项同时是「睡眠切换」的目标：
        //  睡眠时切到第 1 项，唤醒后切回第 2 项。
        // ───────────────────────────────────────────────────────────
        internal static readonly Source[] Sources =
        {
            new Source { Key = "HDMI1", Title = "HDMI 1" },
            new Source { Key = "HDMI2", Title = "HDMI 2" },
            new Source { Key = "DP",    Title = "DisplayPort" },
            new Source { Key = "USBC",  Title = "USB-C" },
        };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hidden;
        private readonly List<int> _hotkeyIds = new();
        private readonly Dictionary<int, string> _idToAction = new();
        private List<DeviceSource> _sources = new();
        private ToolStripMenuItem? _header;
        private bool _busy;
        private PanelForm? _panel;
        private System.Threading.Timer? _autoSceneTimer;

        public TrayApp()
        {
            // 先把原始 Gamma 曲线存下来，之后的调光都基于它算
            Gamma.SaveOriginal();

            _hidden = new HotkeyWindow();
            _hidden.HotkeyPressed += OnHotkeyPressed;
            _hidden.PowerChanged += OnPowerChanged;

            _tray = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "MiTVSwitch",
                Visible = true,
            };
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += (_, _) =>
                _tray.ContextMenuStrip?.Show(Cursor.Position);

            RegisterAllHotkeys();

            // 先探一下 DDC/CI 能不能读写（只读不写，不会动背光），再决定亮度走哪条路
            bool ddcOK = Ddc.Probe();
            Log($"DDC 探活：{(ddcOK ? "可用，亮度走硬件背光" : "不可用，亮度走软件调光")}");

            // 恢复上次的调光 / 色温
            if (Picture.Percent != 100 || Picture.TempIndex != 0) Picture.Apply();

            // 屏幕时间：30 秒采样一次（回调在定时器线程，必须切回 UI 线程再碰控件）
            Usage.Changed += OnUsageChanged;
            Usage.Start();
            Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
            {
                Usage.ScreenLocked = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
            };

            // 自动场景：每分钟看一眼是否该切日间 / 夜间
            Picture.CheckAutoScene(force: true);
            _autoSceneTimer = new System.Threading.Timer(_ => Picture.CheckAutoScene(), null, 60_000, 60_000);

            Picture.Changed += OnPictureChanged;

            if (string.IsNullOrWhiteSpace(Settings.Host))
                PromptForHost();
            else
                _ = SelfCheckAsync();
        }

        // ── 图标 ────────────────────────────────────────────────

        private static Icon LoadIcon()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    Icon? ico = Icon.ExtractAssociatedIcon(exe);
                    if (ico is not null) return ico;
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        // ── 菜单 ────────────────────────────────────────────────

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            var panelItem = new ToolStripMenuItem("打开设置面板…");
            panelItem.Font = new Font(panelItem.Font, FontStyle.Bold);
            panelItem.Click += (_, _) => ShowPanel();
            menu.Items.Add(panelItem);
            menu.Items.Add(new ToolStripSeparator());

            _header = new ToolStripMenuItem("MiTVSwitch")
            {
                Enabled = false,
            };
            menu.Items.Add(_header);
            menu.Items.Add(new ToolStripSeparator());

            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                string title = "  " + s.Title;
                // 设备报了输入列表时，标出它没有的口，免得点了白点
                if (_sources.Count > 0 && TvClient.ResolveSourceId(_sources, s.Key) is null)
                    title += "   （设备无此输入）";

                var item = new ToolStripMenuItem(title) { Tag = s };
                KeyBinding? hk = Hotkeys.Get($"src.{s.Key}");
                if (hk is not null) item.ShortcutKeyDisplayString = hk.Display();
                item.Click += (_, _) => _ = SwitchAsync(s.Key);
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());

            // 音量：走局域网遥控按键，不依赖 DDC
            var volume = new ToolStripMenuItem("音量");
            var volUp = new ToolStripMenuItem("音量 +")
            {
                ShortcutKeyDisplayString = Hotkeys.Get("vol.up")?.Display(),
            };
            volUp.Click += (_, _) => _ = VolumeAsync(true);
            var volDown = new ToolStripMenuItem("音量 −")
            {
                ShortcutKeyDisplayString = Hotkeys.Get("vol.down")?.Display(),
            };
            volDown.Click += (_, _) => _ = VolumeAsync(false);
            var volShow = new ToolStripMenuItem("查看当前音量");
            volShow.Click += async (_, _) => await ShowVolumeAsync();
            volume.DropDownItems.Add(volUp);
            volume.DropDownItems.Add(volDown);
            volume.DropDownItems.Add(new ToolStripSeparator());
            volume.DropDownItems.Add(volShow);
            menu.Items.Add(volume);

            // 画面：亮度（DDC 硬件背光优先，否则软件调光）+ 色温 + 场景
            var picture = new ToolStripMenuItem("画面");
            picture.DropDownItems.Add(new ToolStripMenuItem(
                Ddc.Working ? "亮度（硬件背光）" : "亮度（软件调光）") { Enabled = false });

            for (int i = 0; i < Picture.PresetPercents.Length; i++)
            {
                int pct = Picture.PresetPercents[i];
                var it = new ToolStripMenuItem($"   {pct}%")
                {
                    Checked = Math.Abs(Picture.Percent - pct) <= 1,
                };
                it.Click += (_, _) => { Picture.SetBrightnessPercent(pct); RebuildMenu(); };
                picture.DropDownItems.Add(it);
            }

            picture.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < Picture.TempNames.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(Picture.TempNames[i])
                {
                    Checked = Picture.TempIndex == i,
                };
                it.Click += (_, _) => { Picture.TempIndex = idx; Picture.Apply(); RebuildMenu(); };
                picture.DropDownItems.Add(it);
            }

            picture.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < Picture.Scenes.Length; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem("场景：" + Picture.Scenes[i].Name);
                it.Click += (_, _) => { Picture.ApplyScene(idx); RebuildMenu(); };
                picture.DropDownItems.Add(it);
            }

            picture.DropDownItems.Add(new ToolStripSeparator());
            var resetPic = new ToolStripMenuItem("恢复原始画面");
            resetPic.Click += (_, _) =>
            {
                Picture.Reset();
                RebuildMenu();
            };
            picture.DropDownItems.Add(resetPic);
            menu.Items.Add(picture);

            var comp = new ToolStripMenuItem("切换 320Hz 竞技模式");
            comp.Click += async (_, _) => await CompetitiveAsync();
            menu.Items.Add(comp);

            menu.Items.Add(new ToolStripSeparator());

            var auto = new ToolStripMenuItem($"睡眠时切到「{Sources[0].Title}」")
            {
                CheckOnClick = true,
                Checked = Settings.AutoSwitch,
            };
            auto.CheckedChanged += (_, _) => Settings.AutoSwitch = auto.Checked;
            menu.Items.Add(auto);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(CreateItem("设置显示器地址…", (_, _) => PromptForHost()));
            menu.Items.Add(CreateItem("自动搜索局域网设备…", async (_, _) => await DiscoverAsync()));
            menu.Items.Add(CreateItem("测试连接", async (_, _) => await TestAsync()));
            menu.Items.Add(CreateItem("查看日志", (_, _) => OpenLog()));

            menu.Items.Add(new ToolStripSeparator());

            var startup = new ToolStripMenuItem("开机自动启动")
            {
                CheckOnClick = true,
                Checked = Settings.StartWithWindows,
            };
            startup.CheckedChanged += (_, _) => Settings.StartWithWindows = startup.Checked;
            menu.Items.Add(startup);

            menu.Items.Add(CreateItem("退出", (_, _) => ExitApp()));

            UpdateHeader();
            return menu;
        }

        private static ToolStripMenuItem CreateItem(string text, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += onClick;
            return item;
        }

        /// <summary>菜单里的勾选状态变了，重建一次</summary>
        private void RebuildMenu()
        {
            _tray.ContextMenuStrip?.Dispose();
            _tray.ContextMenuStrip = BuildMenu();
        }

        // ── 画面 / 音量 / 竞技模式 ──────────────────────────────

        internal static void ApplyPicture()
        {
            Picture.Apply();
        }

        /// <summary>面板里改了画面参数后调用</summary>
        internal void PictureChanged()
        {
            Picture.Apply();
            RebuildMenu();
        }

        private void StepBrightness(double delta)
        {
            double before = Picture.Percent;
            Picture.StepBrightness(delta);
            if (Math.Abs(Picture.Percent - before) > 0.5) RebuildMenu();
        }

        private async Task VolumeAsync(bool up)
        {
            if (string.IsNullOrWhiteSpace(Settings.Host)) { PromptForHost(); return; }
            await TvClient.StepVolumeAsync(Settings.Host, up);
            Log($"音量 {(up ? "+" : "-")}2");
        }

        private async Task ShowVolumeAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.Host)) { PromptForHost(); return; }
            var v = await TvClient.GetVolumeAsync(Settings.Host);
            MessageBox.Show(
                v is (int value, int max) ? $"{value} / {max}" : "设备没有返回音量，可能不支持音量接口。",
                "当前音量", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task CompetitiveAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.Host)) { PromptForHost(); return; }

            DialogResult pick = MessageBox.Show(
                "适用于 G Pro 27U 这类双模显示器：在 4K 160Hz 与 1080p 320Hz 之间切换。\n\n" +
                "切换后显示器会换 EDID，屏幕黑一下再回来，系统需要几秒重新识别。\n\n" +
                "点「是」切到 320Hz，点「否」切回 4K 160Hz。",
                "切换 320Hz 竞技模式",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (pick == DialogResult.Cancel) return;

            await Task.Run(async () =>
            {
                await TvClient.SetCompetitiveAsync(Settings.Host, pick == DialogResult.Yes);
                Log($"竞技模式 320Hz -> {(pick == DialogResult.Yes ? "开" : "关")}");
            });
        }

        private void UpdateHeader()
        {
            if (_header is null) return;
            string host = Settings.Host;
            _header.Text = string.IsNullOrWhiteSpace(host)
                ? "⚠ 未设置设备地址"
                : $"显示器信号源 · {host}";
            _tray.Text = string.IsNullOrWhiteSpace(host)
                ? "MiTVSwitch（未设置地址）"
                : $"MiTVSwitch · {host}";
        }

        private static string FormatHotkey(Keys k)
        {
            var parts = new List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Ctrl");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            if ((k & Keys.Shift) != 0) parts.Add("Shift");
            Keys code = k & Keys.KeyCode;
            string key = code switch
            {
                Keys.D0 => "0",
                Keys.D1 => "1",
                Keys.D2 => "2",
                Keys.D3 => "3",
                Keys.D4 => "4",
                Keys.D5 => "5",
                Keys.D6 => "6",
                Keys.D7 => "7",
                Keys.D8 => "8",
                Keys.D9 => "9",
                _ => code.ToString(),
            };
            parts.Add(key);
            return string.Join("+", parts);
        }

        // ── 热键 ────────────────────────────────────────────────

        internal void RegisterAllHotkeys()
        {
            UnregisterAllHotkeys();

            int id = 1;
            foreach (var (action, title, _) in Hotkeys.Actions())
            {
                KeyBinding? hk = Hotkeys.Get(action);
                if (hk is null || hk.IsEmpty) continue;

                uint mods = hk.ToRegisterMods() | KeyBinding.MOD_NOREPEAT;
                if (RegisterHotKey(_hidden.Handle, id, mods, (uint)hk.Key))
                {
                    _hotkeyIds.Add(id);
                    _idToAction[id] = action;
                    id++;
                }
                else
                {
                    Log($"热键注册失败 {title} ({hk.Display()}) err={Marshal.GetLastWin32Error()}");
                }
            }
            Log($"热键已注册 {_hotkeyIds.Count} 个");
        }

        internal void UnregisterAllHotkeys()
        {
            foreach (int id in _hotkeyIds)
                UnregisterHotKey(_hidden.Handle, id);
            _hotkeyIds.Clear();
            _idToAction.Clear();
        }

        /// <summary>改完快捷键后重注册并刷新界面</summary>
        internal void ReloadHotkeys()
        {
            RegisterAllHotkeys();
            RebuildMenu();
        }

        private void OnHotkeyPressed(int id)
        {
            if (!_idToAction.TryGetValue(id, out string? action)) return;
            RunAction(action);
        }

        internal void RunAction(string action)
        {
            if (action.StartsWith("src."))
            {
                _ = SwitchAsync(action[4..]);
                return;
            }

            switch (action)
            {
                case "vol.up": _ = VolumeAsync(true); break;
                case "vol.down": _ = VolumeAsync(false); break;
                case "bright.up": StepBrightness(+5); break;
                case "bright.down": StepBrightness(-5); break;
            }
        }

        // ── 面板回调（都可能来自非 UI 线程）────────────────────

        private void OnUsageChanged()
        {
            var p = _panel;
            if (p is null || p.IsDisposed) return;
            try { p.BeginInvoke(new Action(p.RefreshUsageChart)); } catch { }
        }

        private void OnPictureChanged()
        {
            var p = _panel;
            if (p is null || p.IsDisposed) return;
            try { p.BeginInvoke(new Action(p.RefreshAfterExternalChange)); } catch { }
        }

        internal void ShowPanel()        {
            if (_panel is null || _panel.IsDisposed)
                _panel = new PanelForm(this);

            _panel.Rebuild();
            _panel.RefreshUsageChart();
            _panel.Show();
            _panel.WindowState = FormWindowState.Normal;
            _panel.Activate();
        }

        internal List<DeviceSource> CurrentSources => _sources;

        internal void TestConnectionAndReport() => _ = TestAsync();

        internal void DiscoverDevices() => _ = DiscoverAsync();

        internal void RefreshSourcesAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.Host)) return;
            _ = Task.Run(async () =>
            {
                _sources = await TvClient.GetSourcesAsync(Settings.Host);
            });
        }

        private void OnPowerChanged(bool resumed)
        {
            if (!Settings.AutoSwitch) return;
            if (string.IsNullOrWhiteSpace(Settings.Host)) return;

            if (resumed)
            {
                if (Sources.Length > 1) _ = SwitchAsync(Sources[1].Key);
            }
            else
            {
                _ = SwitchAsync(Sources[0].Key);
            }
        }

        // ── 动作 ────────────────────────────────────────────────

        private async Task SwitchAsync(string key)
        {
            if (_busy) return;

            string host = Settings.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                PromptForHost();
                return;
            }

            _busy = true;
            try
            {
                if (_sources.Count == 0)
                    _sources = await TvClient.GetSourcesAsync(host);

                TvResult r = await TvClient.SwitchAsync(host, key, _sources);
                Log($"切源 {key} -> {r} {Truncate(r.Body, 160)}");

                if (!r.Ok)
                {
                    string available = string.Join("、", _sources.ConvertAll(s => s.Name));
                    MessageBox.Show(
                        available.Length == 0
                            ? $"显示器无响应。\n\n{r}\n\n可以在菜单里用「测试连接」确认地址是否正确。"
                            : $"设备没有提供「{key}」这个输入。\n\n当前可用：{available}\n\n" +
                              "如果刚改过接线，先用「测试连接」刷新一次列表。",
                        "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task SelfCheckAsync()
        {
            TvResult r = await TvClient.InfoAsync(Settings.Host);
            _sources = await TvClient.GetSourcesAsync(Settings.Host);
            string list = string.Join(" ", _sources.ConvertAll(s => $"{s.Name}={s.Id}"));
            Log($"启动自检 {Settings.Host} -> {r} 信号源[{list}]");
            if (_sources.Count > 0) RebuildMenu();
        }

        private void PromptForHost()
        {
            string? v = Prompt.AskText(
                "设置显示器地址",
                "输入小米 / 红米电视或智能显示器的局域网 IP（需支持 6095 端口）：",
                Settings.Host);

            if (v is null) return;
            if (v.Length == 0) return;

            Settings.Host = v;
            UpdateHeader();
            _ = SelfCheckAsync();
        }

        private async Task DiscoverAsync()
        {
            if (MessageBox.Show(
                    "将在本机所在网段扫描 6095 端口，大约需要 10 秒。\n\n继续吗？",
                    "自动搜索局域网设备", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information) != DialogResult.OK)
                return;

            var list = await TvClient.DiscoverAsync();

            if (list.Count == 0)
            {
                MessageBox.Show(
                    "本机网段内没有响应 6095 端口的设备。\n\n" +
                    "请确认显示器已连接 Wi-Fi 或网线，并与本机处于同一网段。",
                    "没有找到设备", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int idx = Prompt.AskList(
                "发现设备",
                $"找到 {list.Count} 台，选择要控制的一台：",
                list.ConvertAll(d => $"{d.Name}{(d.IsMonitor ? "（显示器）" : "")}    ({d.Ip})"));

            if (idx < 0 || idx >= list.Count) return;

            Settings.Host = list[idx].Ip;
            UpdateHeader();
            _ = SelfCheckAsync();
        }

        private async Task TestAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.Host))
            {
                PromptForHost();
                return;
            }

            string host = Settings.Host;
            TvResult alive = await TvClient.AliveAsync(host);
            TvResult info = await TvClient.InfoAsync(host);
            var sources = await TvClient.GetSourcesAsync(host);
            if (sources.Count > 0)
            {
                _sources = sources;
                RebuildMenu();
            }

            var lines = new List<string> { $"地址：{host}" };
            string? name = ExtractDeviceName(alive.Body) ?? ExtractDeviceName(info.Body);
            if (!string.IsNullOrEmpty(name)) lines.Add($"设备：{name}");

            if (sources.Count == 0)
            {
                lines.Add("信号源：设备没提供列表（会退回按名字切换）");
            }
            else
            {
                lines.Add("信号源：" + string.Join("  ", sources.ConvertAll(s => $"{s.Name}={s.Id}")));
                lines.Add("切换方式：changesource2（按编号，DP / USB-C 可用）");
            }

            lines.Add("判定：" + (TvClient.LooksLikeMonitor(name ?? "", sources)
                ? "显示器" : "电视或其他设备"));

            if (!alive.Ok && !info.Ok)
                lines.Add($"HTTP：isalive {alive} / getsysteminfo {info}");

            MessageBox.Show(
                string.Join("\n", lines),
                alive.Ok || info.Ok ? "连接正常" : "连接失败",
                MessageBoxButtons.OK,
                alive.Ok || info.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static string? ExtractDeviceName(string body)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var d) &&
                    d.TryGetProperty("devicename", out var n))
                    return n.GetString();
            }
            catch { }
            return null;
        }

        private void ExitApp()
        {
            _autoSceneTimer?.Dispose();
            Usage.Stop();
            Gamma.Restore();
            Ddc.Close();
            UnregisterAllHotkeys();
            _tray.Visible = false;
            _tray.Dispose();
            _hidden.Dispose();
            ExitThread();
        }

        // ── 日志 ────────────────────────────────────────────────

        private static string LogPath => Path.Combine(Settings.DataDir, "MiTVSwitch.log");

        internal static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
            catch { }
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

        private static void OpenLog()
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                if (!File.Exists(LogPath)) File.WriteAllText(LogPath, "");
                Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "打不开日志", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
