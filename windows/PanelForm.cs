// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Drawing;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MiTVSwitch
{
    /// <summary>
    /// 设置面板。分区：信号源 / 音量与亮度 / 场景 / 分辨率 / 屏幕时间 / 快捷键。
    /// 布局与交互按本项目重写（参考过 MonitorBar 的分区思路，未照抄其实现）。
    /// </summary>
    internal sealed class PanelForm : Form
    {
        private readonly TrayApp _app;
        private Label _device = null!;
        private Label _backend = null!;
        private TrackBar _volumeBar = null!;
        private Label _volumeValue = null!;
        private Label _brightnessValue = null!;
        private Label _usageOn = null!;
        private Label _usageActive = null!;
        private UsageChart _chart = null!;
        private ComboBox _resCombo = null!;
        private System.Collections.Generic.List<ResMode> _resModes = new();
        private readonly System.Windows.Forms.Timer _volumeDebounce = new() { Interval = 400 };
        private int _pendingVolume = -1;

        public PanelForm(TrayApp app)
        {
            _app = app;
            Text = "MiTVSwitch 设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(430, 620);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = SystemColors.Window;
            Font = SystemFonts.MessageBoxFont;
            _volumeDebounce.Tick += (_, _) => FlushVolume();
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

        private static Label Caption(string text, int width = 34) => new Label
        {
            Text = text,
            Width = width,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 4, 0),
        };

        private static Button Small(string text, int w = 32) => new Button
        {
            Text = text,
            Width = w,
            Height = 26,
            Margin = new Padding(0, 0, 4, 0),
            FlatStyle = FlatStyle.System,
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
            var srcFlow = new FlowLayoutPanel { AutoSize = true, Width = 372, Margin = new Padding(0) };
            foreach (var s in TrayApp.Sources)
            {
                bool has = _app.CurrentSources.Count == 0 ||
                           TvClient.ResolveSourceId(_app.CurrentSources, s.Key) is not null;

                var b = new Button
                {
                    Text = has ? s.Title : s.Title + "（无）",
                    Width = 172,
                    Height = 34,
                    Margin = new Padding(0, 0, 6, 6),
                    Enabled = has,
                    FlatStyle = FlatStyle.System,
                };
                string key = s.Key;
                b.Click += (_, _) => _app.RunAction("src." + key);
                srcFlow.Controls.Add(b);
            }
            root.Controls.Add(srcFlow);

            // ── 音量与亮度 ──
            root.Controls.Add(Section("音量与亮度"));

            var volRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            var volMinus = Small("−");
            volMinus.Click += (_, _) => { _app.RunAction("vol.down"); _ = RefreshVolumeAsync(); };
            _volumeBar = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None,
                Width = 208,
                Height = 30,
                SmallChange = 1,
                LargeChange = 5,
                Margin = new Padding(0, 0, 4, 0),
            };
            _volumeBar.ValueChanged += (_, _) =>
            {
                _volumeValue.Text = _volumeBar.Value.ToString();
                _pendingVolume = _volumeBar.Value;
                _volumeDebounce.Stop();
                _volumeDebounce.Start();
            };
            _volumeValue = new Label
            {
                Text = "—",
                Width = 44,
                Height = 26,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0),
            };
            var volPlus = Small("+");
            volPlus.Click += (_, _) => { _app.RunAction("vol.up"); _ = RefreshVolumeAsync(); };
            volRow.Controls.AddRange(new Control[] { Caption("音量"), volMinus, _volumeBar, volPlus, _volumeValue });
            root.Controls.Add(volRow);

            var briRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            var briMinus = Small("−");
            briMinus.Click += (_, _) => { Picture.StepBrightness(-5); SyncBrightness(); };
            var briBar = new TrackBar
            {
                Minimum = Picture.MinBrightness,
                Maximum = Picture.MaxBrightness,
                Value = Math.Clamp(Picture.Percent, Picture.MinBrightness, Picture.MaxBrightness),
                TickStyle = TickStyle.None,
                Width = 208,
                Height = 30,
                SmallChange = 1,
                LargeChange = 5,
                Margin = new Padding(0, 0, 4, 0),
            };
            _brightnessValue = new Label
            {
                Text = Picture.Percent + "%",
                Width = 44,
                Height = 26,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0),
            };
            briBar.ValueChanged += (_, _) =>
            {
                Picture.SetBrightnessPercent(briBar.Value);
                _brightnessValue.Text = Picture.Percent + "%";
                UpdateBackend();
            };
            var briPlus = Small("+");
            briPlus.Click += (_, _) => { Picture.StepBrightness(+5); SyncBrightness(); };
            briRow.Controls.AddRange(new Control[] { Caption("亮度"), briMinus, briBar, briPlus, _brightnessValue });
            root.Controls.Add(briRow);

            var tempRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            var temp = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 136,
                Margin = new Padding(0, 0, 8, 0),
            };
            foreach (string n in Picture.TempNames) temp.Items.Add(n);
            temp.SelectedIndex = Picture.TempIndex;
            temp.SelectedIndexChanged += (_, _) =>
            {
                Picture.TempIndex = temp.SelectedIndex;
                Picture.Apply();
            };
            _backend = new Label
            {
                Text = Picture.BackendLabel,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 6, 0, 0),
            };
            tempRow.Controls.AddRange(new Control[] { Caption("色温", 34), temp, _backend });
            root.Controls.Add(tempRow);

            // ── 场景 ──
            root.Controls.Add(Section("场景"));
            var sceneRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
            for (int i = 0; i < Picture.Scenes.Length; i++)
            {
                int idx = i;
                var b = new Button
                {
                    Text = Picture.Scenes[i].Name,
                    Width = 84,
                    Height = 30,
                    Margin = new Padding(0, 0, 6, 0),
                    FlatStyle = FlatStyle.System,
                };
                b.Click += (_, _) =>
                {
                    Picture.ApplyScene(idx);
                    SyncBrightness();
                };
                sceneRow.Controls.Add(b);
            }
            root.Controls.Add(sceneRow);

            var autoScene = new CheckBox
            {
                Text = "按时间自动切换（日间 / 夜间）",
                AutoSize = true,
                Checked = Settings.AutoScene,
                Margin = new Padding(0, 2, 0, 4),
            };
            autoScene.CheckedChanged += (_, _) =>
            {
                Settings.AutoScene = autoScene.Checked;
                Picture.CheckAutoScene(force: true);
                SyncBrightness();
            };
            root.Controls.Add(autoScene);

            var timeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            timeRow.Controls.Add(new Label
            {
                Text = "白天从",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 7, 4, 0),
            });
            var dayPicker = TimePicker(Settings.SceneDayMinutes);
            dayPicker.ValueChanged += (_, _) =>
            {
                Settings.SceneDayMinutes = dayPicker.Value.Hour * 60 + dayPicker.Value.Minute;
                Picture.CheckAutoScene(force: true);
                SyncBrightness();
            };
            timeRow.Controls.Add(dayPicker);
            timeRow.Controls.Add(new Label
            {
                Text = "夜间从",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(12, 7, 4, 0),
            });
            var nightPicker = TimePicker(Settings.SceneNightMinutes);
            nightPicker.ValueChanged += (_, _) =>
            {
                Settings.SceneNightMinutes = nightPicker.Value.Hour * 60 + nightPicker.Value.Minute;
                Picture.CheckAutoScene(force: true);
                SyncBrightness();
            };
            timeRow.Controls.Add(nightPicker);
            root.Controls.Add(timeRow);

            // ── 分辨率 ──
            root.Controls.Add(Section("分辨率 / 刷新率"));
            var resRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            _resCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 210,
                Margin = new Padding(0, 0, 8, 0),
            };
            _resModes = Display.List();
            if (_resModes.Count == 0) _resCombo.Items.Add("设备未提供可选模式");
            else foreach (var m in _resModes) _resCombo.Items.Add(m.Label);

            var cur = Display.Current();
            if (cur is not null)
            {
                int idx = _resModes.FindIndex(m =>
                    m.Width == cur.Width && m.Height == cur.Height && m.Hz == cur.Hz);
                if (idx >= 0) _resCombo.SelectedIndex = idx;
                else if (_resModes.Count > 0) _resCombo.SelectedIndex = 0;
            }
            else if (_resModes.Count > 0) _resCombo.SelectedIndex = 0;

            var applyRes = new Button { Text = "应用", Width = 64, Height = 28, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.System };
            applyRes.Click += (_, _) =>
            {
                int i = _resCombo.SelectedIndex;
                if (i < 0 || i >= _resModes.Count) return;
                if (Display.Apply(_resModes[i]))
                {
                    var t = new System.Windows.Forms.Timer { Interval = 1500 };
                    t.Tick += (_, _) => { t.Stop(); t.Dispose(); Rebuild(); };
                    t.Start();
                }
            };
            var reloadRes = new Button { Text = "刷新列表", Width = 84, Height = 28, Margin = new Padding(0), FlatStyle = FlatStyle.System };
            reloadRes.Click += (_, _) => Rebuild();
            resRow.Controls.AddRange(new Control[] { _resCombo, applyRes, reloadRes });
            root.Controls.Add(resRow);

            // ── 屏幕时间 ──
            root.Controls.Add(Section("屏幕时间"));
            var usageRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            _usageOn = new Label { Text = "亮屏 —", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f), Margin = new Padding(0, 0, 16, 0) };
            _usageActive = new Label { Text = "使用 —", AutoSize = true, ForeColor = SystemColors.GrayText, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f), Margin = new Padding(0) };
            usageRow.Controls.AddRange(new Control[] { _usageOn, _usageActive });
            root.Controls.Add(usageRow);

            _chart = new UsageChart { Width = 372, Height = 88, Margin = new Padding(0, 0, 0, 2) };
            root.Controls.Add(_chart);

            root.Controls.Add(new Label
            {
                Text = "最近 7 天（柱高 = 使用时长）",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8f),
                Margin = new Padding(0, 0, 0, 4),
            });

            // ── 快捷键 ──
            root.Controls.Add(Section("快捷键（点右侧按键重新录制）"));
            var table = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 2,
                Width = 372,
                Margin = new Padding(0),
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 186));

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
                    Width = 178,
                    Height = 28,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Margin = new Padding(0, 2, 0, 2),
                    FlatStyle = FlatStyle.System,
                };
                string aid = id, atitle = title;
                kb.Click += (_, _) => RecordHotkey(aid, atitle);
                table.Controls.Add(kb);
            }
            root.Controls.Add(table);

            var resetKeys = new Button { Text = "恢复默认快捷键", Width = 150, Height = 30, Margin = new Padding(0, 8, 6, 0), FlatStyle = FlatStyle.System };
            resetKeys.Click += (_, _) =>
            {
                Hotkeys.Reset();
                _app.ReloadHotkeys();
                Rebuild();
            };
            var resetPic = new Button { Text = "恢复原始画面", Width = 130, Height = 30, Margin = new Padding(0, 8, 0, 0), FlatStyle = FlatStyle.System };
            resetPic.Click += (_, _) =>
            {
                Picture.Reset();
                Rebuild();
            };
            root.Controls.Add(resetKeys);
            root.Controls.Add(resetPic);

            // ── 底部 ──
            var bottom = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
            var test = new Button { Text = "测试连接", Width = 96, Height = 32, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.System };
            test.Click += (_, _) =>
            {
                Hide();
                _app.TestConnectionAndReport();
            };
            var find = new Button { Text = "搜索设备…", Width = 100, Height = 32, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.System };
            find.Click += (_, _) =>
            {
                Hide();
                _app.DiscoverDevices();
            };
            var close = new Button { Text = "关闭", Width = 80, Height = 32, Margin = new Padding(0), FlatStyle = FlatStyle.System };
            close.Click += (_, _) => Hide();
            bottom.Controls.AddRange(new Control[] { test, find, close });
            root.Controls.Add(bottom);

            Controls.Add(root);
            ResumeLayout();

            RefreshUsageChart();
            _ = RefreshVolumeAsync();
        }

        private static DateTimePicker TimePicker(int minutes)
        {
            var p = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm",
                ShowUpDown = true,
                Width = 84,
                Margin = new Padding(0, 3, 0, 0),
                Value = DateTime.Today.AddMinutes(Math.Clamp(minutes, 0, 1439)),
            };
            return p;
        }

        /// <summary>把亮度控件同步成当前值（场景 / 热键改了之后用）</summary>
        private void SyncBrightness()
        {
            int p = Math.Clamp(Picture.Percent, Picture.MinBrightness, Picture.MaxBrightness);
            if (_brightnessValue is not null) _brightnessValue.Text = p + "%";

            // 滑动条在 Rebuild 里重建，这里只更新数值，避免递归触发 ValueChanged
            var bar = FindBrightnessBar(this);
            if (bar is not null && bar.Value != p) bar.Value = p;

            UpdateBackend();
        }

        /// <summary>递归找亮度滑动条（它嵌在 root → 行容器两层里）</summary>
        private static TrackBar? FindBrightnessBar(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is TrackBar tb && tb.Minimum == Picture.MinBrightness) return tb;
                var found = FindBrightnessBar(c);
                if (found is not null) return found;
            }
            return null;
        }

        /// <summary>外部（热键 / 菜单 / 自动场景）改了画面之后刷新显示</summary>
        public void RefreshAfterExternalChange()
        {
            SyncBrightness();
        }

        private void UpdateBackend()
        {
            if (_backend is not null) _backend.Text = Picture.BackendLabel;
        }

        public void RefreshUsageChart()
        {
            var t = Usage.Today();
            if (_usageOn is not null) _usageOn.Text = "亮屏 " + Usage.Duration(t.On);
            if (_usageActive is not null) _usageActive.Text = "使用 " + Usage.Duration(t.Active);
            if (_chart is not null)
            {
                var week = Usage.Recent(7);
                var vals = new double[week.Count];
                var labels = new string[week.Count];
                for (int i = 0; i < week.Count; i++)
                {
                    vals[i] = week[i].Usage.Active;
                    labels[i] = week[i].Label;
                }
                _chart.SetData(labels, vals);
            }
        }

        private void RecordHotkey(string action, string title)
        {
            using var dlg = new HotkeyRecorderForm(title);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            Hotkeys.Set(action, dlg.Result);
            _app.ReloadHotkeys();
            Rebuild();
        }

        private void FlushVolume()
        {
            _volumeDebounce.Stop();
            int target = _pendingVolume;
            if (target < 0) return;
            _pendingVolume = -1;
            _ = ApplyVolumeAsync(target);
        }

        private async Task ApplyVolumeAsync(int target)
        {
            var cur = await TvClient.GetVolumeAsync(Settings.Host);
            if (cur is not (int now, int max)) return;

            int delta = target - now;
            int cap = 30;
            if (Math.Abs(delta) > cap) delta = delta > 0 ? cap : -cap;

            int sent = 0;
            while (sent != delta)
            {
                bool up = delta > 0;
                await TvClient.PressAsync(Settings.Host, up ? "volumeup" : "volumedown");
                sent += up ? 1 : -1;
                await Task.Delay(25);
            }
            await RefreshVolumeAsync();
        }

        private async Task RefreshVolumeAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.Host)) return;

            var v = await TvClient.GetVolumeAsync(Settings.Host);
            if (v is (int value, int max))
            {
                _volumeValue.Text = value.ToString();
                if (_volumeBar is not null && _volumeBar.Value != value)
                    _volumeBar.Value = Math.Clamp(value, 0, 100);
            }

            var alive = await TvClient.AliveAsync(Settings.Host);
            string? name = ExtractName(alive.Body);
            if (!string.IsNullOrEmpty(name)) _device.Text = name;

            if (_app.CurrentSources.Count == 0) _app.RefreshSourcesAsync();
            UpdateBackend();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _volumeDebounce.Stop();
            base.OnFormClosed(e);
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

    /// <summary>最近 7 天柱状图，最后一根用系统强调色</summary>
    internal sealed class UsageChart : Control
    {
        private string[] _labels = Array.Empty<string>();
        private double[] _values = Array.Empty<double>();

        public UsageChart()
        {
            DoubleBuffered = true;
            BackColor = SystemColors.Window;
        }

        public void SetData(string[] labels, double[] values)
        {
            _labels = labels;
            _values = values;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (_values.Length == 0) return;

            double maxV = 60;
            foreach (double v in _values) if (v > maxV) maxV = v;

            int n = _values.Length;
            int gap = 14;
            int barW = Math.Max(10, (Width - gap * (n + 1)) / n);
            int bottom = 16;
            float top = 6;

            using var labelBrush = new SolidBrush(SystemColors.GrayText);
            using var font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 7.5f);

            for (int i = 0; i < n; i++)
            {
                int x = gap + i * (barW + gap);
                float h = (float)(_values[i] / maxV) * (Height - bottom - top);
                if (h < 1) h = 1;
                var rect = new RectangleF(x, Height - bottom - h, barW, h);

                using var path = RoundedRect(rect, 4);
                using var brush = new SolidBrush(i == n - 1 ? SystemColors.Highlight : SystemColors.ControlDark);
                g.FillPath(brush, path);

                if (i < _labels.Length)
                {
                    var size = g.MeasureString(_labels[i], font);
                    g.DrawString(_labels[i], font, labelBrush,
                        x + (barW - size.Width) / 2, Height - bottom + 2);
                }
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            if (r.Height < d) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
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
