// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Runtime.InteropServices;

namespace MiTVSwitch
{
    /// <summary>
    /// 软件调光与色温：改写显卡的 Gamma 表，不动背光，
    /// 所以任何接口、任何显示器都能用（代价是压暗后对比度下降）。
    /// 思路与档位设计参考 MonitorBar（MIT，© 2026 binaryify），见 NOTICE。
    /// </summary>
    internal static class Gamma
    {
        public static readonly double[] BrightnessSteps = { 1.0, 0.85, 0.70, 0.55 };
        public static readonly string[] BrightnessNames = { "100%", "85%", "70%", "55%" };
        public static readonly double[] TempValues = { 6500, 5000, 4000 };
        public static readonly string[] TempNames = { "标准 6500K", "暖 5000K", "夜间 4000K" };

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] lpRamp);

        private static ushort[]? _original;

        /// <summary>在应用任何调光之前先存下原始曲线</summary>
        public static void SaveOriginal()
        {
            if (_original is not null) return;
            var ramp = new ushort[768];
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return;
            try
            {
                if (GetDeviceGammaRamp(dc, ramp)) _original = ramp;
            }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        /// <summary>按档位应用。返回 false 表示显卡不接受这条曲线</summary>
        public static bool Apply(double level, double kelvin)
        {
            SaveOriginal();
            if (_original is null) return false;

            var (gr, gg, gb) = Gains(kelvin);
            var ramp = new ushort[768];
            for (int ch = 0; ch < 3; ch++)
            {
                double gain = ch == 0 ? gr : (ch == 1 ? gg : gb);
                for (int i = 0; i < 256; i++)
                {
                    double v = _original[ch * 256 + i] * level * gain;
                    ramp[ch * 256 + i] = (ushort)Math.Clamp((int)Math.Round(v), 0, 65535);
                }
            }

            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(dc, ramp); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        public static bool Restore()
        {
            if (_original is null) return false;
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(dc, _original); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        /// <summary>按开尔文算 RGB 增益（Tanner Helland 近似），归一化后只改色不改亮度</summary>
        private static (double r, double g, double b) Gains(double kelvin)
        {
            double t = kelvin / 100;
            double r = 255, g = 255, b = 255;
            if (t <= 66)
            {
                g = 99.4708025861 * Math.Log(t) - 161.1195681661;
                b = t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
            }
            else
            {
                r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
                g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            }

            static double Clamp(double v) => Math.Clamp(v, 0, 255) / 255;
            double rr = Clamp(r), gg = Clamp(g), bb = Clamp(b);
            double m = Math.Max(rr, Math.Max(gg, bb));
            if (m <= 0) return (1, 1, 1);
            return (rr / m, gg / m, bb / m);
        }
    }
}
