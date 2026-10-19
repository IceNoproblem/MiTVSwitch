// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace MiTVSwitch
{
    internal sealed class DayUsage
    {
        public double On { get; set; }
        public double Active { get; set; }
    }

    /// <summary>
    /// 屏幕时间统计：每 30 秒采样一次，屏幕亮着就算「亮屏」，
    /// 同时最近 5 分钟内有过键鼠输入才算「使用」。按天存 JSON，保留 60 天。
    /// 这个功能不依赖显示器协议，任何显示器都能用。
    /// </summary>
    internal static class Usage
    {
        private const double SampleInterval = 30;
        private const int KeepDays = 60;
        private const double IdleThreshold = 300;

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static readonly object Gate = new();
        private static Dictionary<string, DayUsage> _days = new();
        private static System.Threading.Timer? _timer;
        private static DateTime _lastSample = DateTime.MinValue;
        private static bool _loaded;
        private static bool _screenLocked;

        private static string Dir => Settings.DataDir;
        private static string FilePath => Path.Combine(Dir, "usage.json");

        public static event Action? Changed;

        /// <summary>合盖 / 锁屏时不计亮屏</summary>
        public static bool ScreenLocked
        {
            get => _screenLocked;
            set => _screenLocked = value;
        }

        public static void Start()
        {
            Load();
            Sample();
            _timer ??= new System.Threading.Timer(_ => Sample(), null, (int)(SampleInterval * 1000), (int)(SampleInterval * 1000));
        }

        public static void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            Save();
        }

        private static double IdleSeconds()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;
            // dwTime 是系统启动以来的毫秒数，用 Environment.TickCount 换算
            uint now = unchecked((uint)Environment.TickCount);
            uint idleMs = unchecked(now - info.dwTime);
            return idleMs / 1000.0;
        }

        private static void Sample()
        {
            var now = DateTime.Now;

            // 睡眠 / 休眠回来会跳很长一段，这一段直接不算，别把睡着的时间算成亮屏
            bool slept = _lastSample != DateTime.MinValue &&
                         (now - _lastSample).TotalSeconds > SampleInterval * 3;
            _lastSample = now;
            if (slept) return;

            double delta = SampleInterval;

            lock (Gate)
            {
                string key = now.ToString("yyyy-MM-dd");
                if (!_days.TryGetValue(key, out var d)) { d = new DayUsage(); _days[key] = d; }

                if (!_screenLocked)
                {
                    d.On += delta;
                    if (IdleSeconds() < IdleThreshold) d.Active += delta;
                }

                Prune();
                Save();
            }

            Changed?.Invoke();
        }

        private static void Prune()
        {
            if (_days.Count <= KeepDays) return;
            var keys = new List<string>(_days.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count - KeepDays; i++) _days.Remove(keys[i]);
        }

        private static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, DayUsage>>(File.ReadAllText(FilePath));
                if (data is not null) _days = data;
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_days));
            }
            catch { }
        }

        public static DayUsage Today()
        {
            lock (Gate)
            {
                return _days.TryGetValue(DateTime.Now.ToString("yyyy-MM-dd"), out var d) ? d : new DayUsage();
            }
        }

        /// <summary>最近 n 天，按时间正序</summary>
        public static List<(string Key, string Label, DayUsage Usage)> Recent(int n)
        {
            var outList = new List<(string, string, DayUsage)>();
            lock (Gate)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    var date = DateTime.Today.AddDays(-i);
                    string key = date.ToString("yyyy-MM-dd");
                    outList.Add((key, date.ToString("M/d"),
                        _days.TryGetValue(key, out var d) ? d : new DayUsage()));
                }
            }
            return outList;
        }

        public static string Duration(double seconds)
        {
            int s = (int)Math.Round(seconds);
            if (s < 60) return $"{s} 秒";
            int m = s / 60;
            if (m < 60) return $"{m} 分钟";
            return $"{m / 60} 小时 {m % 60} 分";
        }
    }
}
