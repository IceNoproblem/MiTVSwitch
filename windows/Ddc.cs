// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Runtime.InteropServices;

namespace MiTVSwitch
{
    /// <summary>
    /// 硬件背光：走 Windows 自带的 dxva2（DDC/CI 封装），成功时亮度调的是显示器背光本身。
    /// 取不到物理显示器或显示器不支持 MCCS 亮度时 Working 保持 false，调用方退回软件调光。
    /// </summary>
    internal static class Ddc
    {
        private const int MONITOR_DEFAULTTOPRIMARY = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, uint arraySize, [Out] PHYSICAL_MONITOR[] array);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(
            IntPtr hMonitor, ref uint min, ref uint current, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        private static readonly object Gate = new();
        private static IntPtr _handle = IntPtr.Zero;
        private static uint _min, _max;
        private static bool _initTried;

        /// <summary>探活跑过了（不管成没成）</summary>
        public static bool Probed { get; private set; }

        /// <summary>真的读写成功过至少一次</summary>
        public static bool Working { get; private set; }

        private static bool EnsureInit()
        {
            lock (Gate)
            {
                if (_initTried) return _handle != IntPtr.Zero;
                _initTried = true;

                try
                {
                    // 主显示器原点，多屏时也只管主屏
                    IntPtr hmon = MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
                    if (hmon == IntPtr.Zero) return false;

                    uint count = 0;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, ref count) || count == 0) return false;

                    var arr = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(hmon, count, arr)) return false;

                    _handle = arr[0].hPhysicalMonitor;
                    return _handle != IntPtr.Zero;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        /// <summary>读一次亮度（不改变任何状态），顺便拿到量程</summary>
        public static bool TryRead(out int current, out int max)
        {
            current = 0;
            max = 100;
            if (!EnsureInit()) return false;

            uint mn = 0, cur = 0, mx = 0;
            if (!GetMonitorBrightness(_handle, ref mn, ref cur, ref mx)) return false;
            if (mx == 0) return false;

            _min = mn;
            _max = mx;
            current = (int)cur;
            max = (int)mx;
            Working = true;
            return true;
        }

        /// <summary>非侵入探活：只读不写</summary>
        public static bool Probe()
        {
            if (Working) { Probed = true; return true; }
            bool ok = TryRead(out _, out _);
            Probed = true;
            return ok;
        }

        /// <summary>设置硬件背光，0…100</summary>
        public static bool SetBrightness(int percent)
        {
            if (!EnsureInit()) return false;

            // 先拿量程，有些显示器最大值不是 100
            if (_max == 0 && !TryRead(out _, out _)) return false;

            int p = Math.Clamp(percent, 0, 100);
            uint value = (uint)((_min + (long)(_max - _min) * p + 50) / 100);
            if (!SetMonitorBrightness(_handle, value)) { Working = false; return false; }

            Working = true;
            return true;
        }

        public static void Close()
        {
            lock (Gate)
            {
                if (_handle != IntPtr.Zero)
                {
                    try { DestroyPhysicalMonitor(_handle); } catch { }
                    _handle = IntPtr.Zero;
                }
                _initTried = false;
            }
        }
    }
}
