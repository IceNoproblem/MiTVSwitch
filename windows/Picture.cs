// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Threading;

namespace MiTVSwitch
{
    /// <summary>一个场景 = 亮度 + 色温的组合</summary>
    internal readonly struct Scene
    {
        public readonly string Name;
        public readonly int Brightness;   // 百分点
        public readonly int TempIndex;

        public Scene(string name, int brightness, int tempIndex)
        {
            Name = name;
            Brightness = brightness;
            TempIndex = tempIndex;
        }
    }

    /// <summary>
    /// 画面：亮度 / 色温 / 场景。
    ///
    /// 两条路：DDC/CI 能写就调硬件背光（Gamma 只管色温），写不了就整块 Gamma 软件调光。
    /// 所以「支持 DDC 的显示器」和「什么协议都不支持的普通显示器」用的是同一套界面。
    /// </summary>
    internal static class Picture
    {
        public const int MinBrightness = 30;
        public const int MaxBrightness = 100;

        public static readonly string[] TempNames = { "标准 6500K", "暖 5000K", "夜间 4000K" };
        public static readonly double[] TempValues = { 6500, 5000, 4000 };

        public static readonly Scene[] Scenes =
        {
            new Scene("日间", 100, 0),
            new Scene("阅读", 85, 1),
            new Scene("观影", 70, 2),
            new Scene("夜间", 55, 2),
        };

        /// <summary>菜单里的快捷档位（面板上是连续滑动条）</summary>
        public static readonly int[] PresetPercents = { 100, 85, 70, 55 };

        private static readonly object Gate = new();
        private static System.Threading.Timer? _ddcTimer;
        private static int _pendingPercent = -1;
        private static bool _hardwareTried;

        public static event Action? Changed;

        public static int Percent => Settings.BrightnessPercent;

        public static int TempIndex
        {
            get => Math.Clamp(Settings.GammaTemp, 0, TempValues.Length - 1);
            set => Settings.GammaTemp = Math.Clamp(value, 0, TempValues.Length - 1);
        }

        /// <summary>当前亮度走的是哪条路（给面板显示）</summary>
        public static string BackendLabel
        {
            get
            {
                if (Ddc.Working) return "硬件背光（DDC/CI）";
                if (!Ddc.Probed) return "正在探测…";
                return "软件调光（Gamma）";
            }
        }

        /// <summary>把当前亮度 / 色温套到屏幕上</summary>
        public static void Apply()
        {
            int pct = Percent;
            double kelvin = TempValues[TempIndex];
            bool ddcWorthTrying = Ddc.Probed ? Ddc.Working : true;

            if (_hardwareTried && !Ddc.Working) ddcWorthTrying = false;

            if (ddcWorthTrying)
            {
                // 亮度交给背光，Gamma 只管色温
                Gamma.Apply(1.0, kelvin);
                PushHardware(pct);
            }
            else
            {
                Gamma.Apply(pct / 100.0, kelvin);
            }
        }

        /// <summary>DDC 写入慢，拖动滑动条时合并成最后一次</summary>
        public static void PushHardware(int percent)
        {
            lock (Gate)
            {
                _pendingPercent = percent;
                if (_ddcTimer is null)
                {
                    _ddcTimer = new System.Threading.Timer(_ => FlushHardware(), null, 250, Timeout.Infinite);
                }
                else
                {
                    _ddcTimer.Change(250, Timeout.Infinite);
                }
            }
        }

        private static void FlushHardware()
        {
            int pct;
            lock (Gate) { pct = _pendingPercent; }
            if (pct < 0) return;

            bool ok = Ddc.SetBrightness(pct);
            _hardwareTried = true;

            if (!ok)
            {
                // 硬件不通，立刻退回软件调光
                Gamma.Apply(pct / 100.0, TempValues[TempIndex]);
                TrayApp.Log("DDC 写入失败，改走软件调光");
            }

            Changed?.Invoke();
        }

        public static void SetBrightnessPercent(double percent)
        {
            Settings.BrightnessPercent = (int)Math.Round(Math.Clamp(percent, MinBrightness, MaxBrightness));
            Apply();
        }

        public static void StepBrightness(double delta)
        {
            SetBrightnessPercent(Percent + delta);
        }

        public static void ApplyScene(int index)
        {
            if (index < 0 || index >= Scenes.Length) return;
            var s = Scenes[index];
            Settings.BrightnessPercent = s.Brightness;
            TempIndex = s.TempIndex;
            Apply();
            TrayApp.Log($"场景：{s.Name}（亮度 {s.Brightness}%，{TempNames[s.TempIndex]}）");
            Changed?.Invoke();
        }

        public static void Reset()
        {
            Settings.BrightnessPercent = 100;
            TempIndex = 0;
            Gamma.Restore();
            Gamma.Apply(1.0, 6500);
            Changed?.Invoke();
        }

        // MARK: 按时间自动切换

        private static bool? _lastWasNight;

        public static void CheckAutoScene(bool force = false)
        {
            if (!Settings.AutoScene) { _lastWasNight = null; return; }

            int now = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            int day = Settings.SceneDayMinutes;
            int night = Settings.SceneNightMinutes;

            // 白天区间算 [day, night)；跨夜时按补集处理
            bool isNight = day <= night
                ? !(now >= day && now < night)
                : (now >= night && now < day);

            if (!force && _lastWasNight == isNight) return;
            _lastWasNight = isNight;
            ApplyScene(isNight ? 3 : 0);
            TrayApp.Log($"自动场景：{(isNight ? "夜间" : "日间")}");
        }
    }
}
