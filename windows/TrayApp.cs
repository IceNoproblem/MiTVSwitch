// SPDX-License-Identifier: CC-BY-NC-SA-4.0
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
        public Keys Hotkey = Keys.None;
    }

    internal sealed class TrayApp : ApplicationContext
    {
        // ───────────────────────────────────────────────────────────
        //  想改信号源名字、增减接口、换快捷键，改这张表就行。
        //  Key   : 传给 changesource 的源名，大小写不敏感
        //  Title : 菜单里显示的名字
        //  数组第 1、2 项同时是「睡眠切换」的目标：
        //  睡眠时切到第 1 项，唤醒后切回第 2 项。
        // ───────────────────────────────────────────────────────────
        private static readonly Source[] Sources =
        {
            new Source { Key = "HDMI1", Title = "HDMI 1",      Hotkey = Keys.Control | Keys.Alt | Keys.D1 },
            new Source { Key = "HDMI2", Title = "HDMI 2",      Hotkey = Keys.Control | Keys.Alt | Keys.D2 },
            new Source { Key = "DP",    Title = "DisplayPort" },
            new Source { Key = "USBC",  Title = "USB-C" },
        };

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly NotifyIcon _tray;
        private readonly HotkeyWindow _hidden;
        private readonly List<int> _hotkeyIds = new();
        private ToolStripMenuItem? _header;
        private bool _busy;

        public TrayApp()
        {
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

            _header = new ToolStripMenuItem("MiTVSwitch")
            {
                Enabled = false,
            };
            menu.Items.Add(_header);
            menu.Items.Add(new ToolStripSeparator());

            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                var item = new ToolStripMenuItem("  " + s.Title) { Tag = s };
                if (s.Hotkey != Keys.None)
                    item.ShortcutKeyDisplayString = FormatHotkey(s.Hotkey);
                item.Click += (_, _) => _ = SwitchAsync(s.Key);
                menu.Items.Add(item);
            }

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

        private void RegisterAllHotkeys()
        {
            for (int i = 0; i < Sources.Length; i++)
            {
                Source s = Sources[i];
                if (s.Hotkey == Keys.None) continue;

                uint mods = MOD_NOREPEAT;
                if ((s.Hotkey & Keys.Control) != 0) mods |= MOD_CONTROL;
                if ((s.Hotkey & Keys.Alt) != 0) mods |= MOD_ALT;
                if ((s.Hotkey & Keys.Shift) != 0) mods |= MOD_SHIFT;

                uint vk = (uint)(s.Hotkey & Keys.KeyCode);
                int id = i + 1;

                if (RegisterHotKey(_hidden.Handle, id, mods, vk))
                {
                    _hotkeyIds.Add(id);
                }
                else
                {
                    Log($"热键注册失败 {FormatHotkey(s.Hotkey)} err={Marshal.GetLastWin32Error()}");
                }
            }
        }

        private void UnregisterAllHotkeys()
        {
            foreach (int id in _hotkeyIds)
                UnregisterHotKey(_hidden.Handle, id);
            _hotkeyIds.Clear();
        }

        private void OnHotkeyPressed(int id)
        {
            int idx = id - 1;
            if (idx < 0 || idx >= Sources.Length) return;
            _ = SwitchAsync(Sources[idx].Key);
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
                TvResult r = await TvClient.SwitchAsync(host, key);
                Log($"changesource {key} -> {r} {Truncate(r.Body, 160)}");
                if (!r.Ok)
                {
                    MessageBox.Show(
                        $"设备无响应，或该源名不被接受（{key}）。\n\n{r}\n\n" +
                        "可以在菜单里用「测试连接」确认地址是否正确。",
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
            Log($"启动自检 {Settings.Host} -> {r}");
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
                list.ConvertAll(d => $"{d.Name}    ({d.Ip})"));

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

            TvResult r = await TvClient.InfoAsync(Settings.Host);
            if (r.Ok)
            {
                MessageBox.Show($"地址：{Settings.Host}\n\n{r.Body}",
                    "连接正常", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show($"地址：{Settings.Host}\n\n{r}\n\n{r.Body}",
                    "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExitApp()
        {
            UnregisterAllHotkeys();
            _tray.Visible = false;
            _tray.Dispose();
            _hidden.Dispose();
            ExitThread();
        }

        // ── 日志 ────────────────────────────────────────────────

        private static string LogPath => Path.Combine(Settings.DataDir, "MiTVSwitch.log");

        private static void Log(string msg)
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
