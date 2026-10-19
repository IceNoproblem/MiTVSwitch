// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiTVSwitch
{
    /// <summary>
    /// 控制面板。参考了 MonitorBar 的分区思路（信号源 / 音量 / 画面 / 快捷键），
    /// 但布局与交互按本项目重写。
    /// </summary>
    internal sealed class PanelForm : Form
    {
        private readonly TrayApp _app;
        private Label _device = null!;
        private Label _volume = null!;

        public PanelForm(TrayApp app)
        {
            _app = app;
            Text = "MiTVSwitch";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(420, 680);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = SystemColors.Window;
            Rebuild();
        }

        private static Font Bold(float size) =>
            new Font(SystemFonts.MessageBoxFont!.FontFamily, size, FontStyle.Bold);

        private static Label Section(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            Font = Bold(9f),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 12, 0, 4),
        };

        public void Rebuild()
        {
            SuspendLayout();
            Controls.Clear();

            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(16),
            };

            root.Controls.Add(new Label
            {
                Text = "MiTVSwitch",
                AutoSize = true,
                Font = Bold(15f),
                Margin = new Padding(0),
            });

            _device = new Label
            {
                Text = string.IsNullOrWhiteSpace(Settings.Host) ? "未设置显示器地址" : $"设备 {Settings.Host}",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0),
            };
            root.Controls.Add(_device);

            // ── 信号源 ──
            root.Controls.Add(Section("信号源"));
            var srcFlow = new FlowLayoutPanel { AutoSize = true, Width = 360, Margin = new Padding(0) };
            foreach (var s in TrayApp.Sources)
            {
                bool has = _app.CurrentSources.Count == 0 ||
                           TvClient.ResolveSourceId(_app.CurrentSources, s.Key) is not null;

                var b = new Button
                {
                    Text = s.Title,
                    Width = 166,
                    Height = 36,
                    Margin = new Padding(0, 0, 6, 6),
                    Enabled = has,
                };
                if (!has) b.Text = s.Title + "（无）";
                string key = s.Key;
                b.Click += (_, _) => _app.RunAction("src." + key);
                srcFlow.Controls.Add(b);
            }
            root.Controls.Add(srcFlow);

            // ── 音量 ──
            root.Controls.Add(Section("显示器音量"));
            var volRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            var minus = new Button { Text = "−", Width = 46, Height = 34, Margin = new Padding(0, 0, 6, 0) };
            minus.Click += (_, _) => { _app.RunAction("vol.down"); _ = RefreshVolumeAsync(); };
            _volume = new Label
            {
                Text = "—",
                Width = 92,
                Height = 34,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f),
                Margin = new Padding(0),
            };
            var plus = new Button { Text = "+", Width = 46, Height = 34, Margin = new Padding(6, 0, 6, 0) };
            plus.Click += (_, _) => { _app.RunAction("vol.up"); _ = RefreshVolumeAsync(); };
            var refresh = new Button { Text = "刷新", Width = 64, Height = 34, Margin = new Padding(0) };
            refresh.Click += (_, _) => _ = RefreshVolumeAsync();
            volRow.Controls.AddRange(new Control[] { minus, _volume, plus, refresh });
            root.Controls.Add(volRow);

            // ── 画面 ──
            root.Controls.Add(Section("画面（软件调光 / 色温）"));
            var picRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };

            var lv = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                Margin = new Padding(0, 0, 12, 0),
            };
            for (int i = 0; i < Gamma.BrightnessNames.Length; i++)
                lv.Items.Add("亮度 " + Gamma.BrightnessNames[i]);
            lv.SelectedIndex = Math.Clamp(Settings.GammaLevel, 0, Gamma.BrightnessNames.Length - 1);
            lv.SelectedIndexChanged += (_, _) =>
            {
                Settings.GammaLevel = lv.SelectedIndex;
                _app.PictureChanged();
            };

            var tp = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 140,
                Margin = new Padding(0),
            };
            foreach (string n in Gamma.TempNames) tp.Items.Add(n);
            tp.SelectedIndex = Math.Clamp(Settings.GammaTemp, 0, Gamma.TempNames.Length - 1);
            tp.SelectedIndexChanged += (_, _) =>
            {
                Settings.GammaTemp = tp.SelectedIndex;
                _app.PictureChanged();
            };

            picRow.Controls.AddRange(new Control[] { lv, tp });
            root.Controls.Add(picRow);

            var resetPic = new Button { Text = "恢复原始画面", Width = 140, Height = 30, Margin = new Padding(0, 6, 0, 0) };
            resetPic.Click += (_, _) =>
            {
                Settings.GammaLevel = 0;
                Settings.GammaTemp = 0;
                Gamma.Restore();
                _app.PictureChanged();
                Rebuild();
            };
            root.Controls.Add(resetPic);

            // ── 快捷键 ──
            root.Controls.Add(Section("快捷键（点右侧按键重新录制）"));
            var table = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 2,
                Width = 360,
                Margin = new Padding(0),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));

            string lastGroup = "";
            foreach (var (id, title, group) in Hotkeys.Actions())
            {
                if (group != lastGroup)
                {
                    var g = new Label
                    {
                        Text = group,
                        AutoSize = true,
                        ForeColor = SystemColors.GrayText,
                        Margin = new Padding(0, 8, 0, 2),
                    };
                    table.Controls.Add(g);
                    table.SetColumnSpan(g, 2);
                    lastGroup = group;
                }

                table.Controls.Add(new Label
                {
                    Text = title,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 3, 0, 3),
                });

                var kb = new Button
                {
                    Text = Hotkeys.Get(id)?.Display() ?? "未设置",
                    Width = 168,
                    Height = 28,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 2, 0, 2),
                };
                string aid = id, atitle = title;
                kb.Click += (_, _) => RecordHotkey(aid, atitle);
                table.Controls.Add(kb);
            }
            root.Controls.Add(table);

            var resetKeys = new Button { Text = "恢复默认快捷键", Width = 150, Height = 30, Margin = new Padding(0, 8, 0, 0) };
            resetKeys.Click += (_, _) =>
            {
                Hotkeys.Reset();
                _app.ReloadHotkeys();
                Rebuild();
            };
            root.Controls.Add(resetKeys);

            // ── 底部 ──
            var bottom = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            var test = new Button { Text = "测试连接", Width = 96, Height = 32, Margin = new Padding(0, 0, 6, 0) };
            test.Click += (_, _) =>
            {
                Hide();
                _app.TestConnectionAndReport();
            };
            var close = new Button { Text = "关闭", Width = 80, Height = 32, Margin = new Padding(0) };
            close.Click += (_, _) => Hide();
            bottom.Controls.AddRange(new Control[] { test, close });
            root.Controls.Add(bottom);

            Controls.Add(root);
            ResumeLayout();

            _ = RefreshVolumeAsync();
        }

        private void RecordHotkey(string action, string title)
        {
            using var dlg = new HotkeyRecorderForm(title);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            Hotkeys.Set(action, dlg.Result);
            _app.ReloadHotkeys();
            Rebuild();
        }

        private async Task RefreshVolumeAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.Host)) return;

            var v = await TvClient.GetVolumeAsync(Settings.Host);
            _volume.Text = v is (int value, int max) ? $"{value} / {max}" : "—";

            var alive = await TvClient.AliveAsync(Settings.Host);
            string? name = ExtractName(alive.Body);
            if (!string.IsNullOrEmpty(name)) _device.Text = name;

            if (_app.CurrentSources.Count == 0) _app.RefreshSourcesAsync();
        }

        private static string? ExtractName(string body)
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
    }

    /// <summary>录制一个快捷键组合。Esc 取消，Delete 清除绑定。</summary>
    internal sealed class HotkeyRecorderForm : Form
    {
        public KeyBinding? Result;

        public HotkeyRecorderForm(string actionTitle)
        {
            Text = "设置快捷键";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            KeyPreview = true;
            ClientSize = new Size(360, 150);
            ShowInTaskbar = false;

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = $"{actionTitle}\n\n按下新的组合键（需含 Ctrl / Alt / Shift / Win 至少一个）\nEsc 取消 · Delete 清除绑定",
            };
            Controls.Add(hint);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                Result = null;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            // 只按修饰键时继续等真正的按键
            if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
                return;

            uint mods = 0;
            if (e.Control) mods |= KeyBinding.MOD_CONTROL;
            if (e.Alt) mods |= KeyBinding.MOD_ALT;
            if (e.Shift) mods |= KeyBinding.MOD_SHIFT;

            if (mods == 0)
            {
                SystemSounds.Beep.Play();
                return;
            }

            Result = new KeyBinding { Mods = mods, Key = e.KeyCode };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
