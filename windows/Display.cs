// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MiTVSwitch
{
    internal sealed class ResMode
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public int Hz { get; init; }
        public string DeviceName { get; init; } = "";
        public int Index { get; init; }

        public string Label => Hz > 0 ? $"{Width} × {Height}  @{Hz}Hz" : $"{Width} × {Height}";
    }

    /// <summary>
    /// 分辨率 / 刷新率切换，走 ChangeDisplaySettingsEx，任何显示器都能用。
    /// </summary>
    internal static class Display
    {
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DM_PELSWIDTH = 0x80000;
        private const int DM_PELSHEIGHT = 0x100000;
        private const int DM_DISPLAYFREQUENCY = 0x400000;
        private const int DISP_CHANGE_SUCCESSFUL = 0;
        private const int CDS_UPDATEREGISTRY = 0x1;
        private const int CDS_GLOBAL = 0x8;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(
            string? deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        private static DEVMODE NewDevMode()
        {
            return new DEVMODE
            {
                dmDeviceName = "",
                dmFormName = "",
                dmSize = (ushort)Marshal.SizeOf<DEVMODE>(),
            };
        }

        public static ResMode? Current()
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) return null;
            return new ResMode
            {
                Width = (int)dm.dmPelsWidth,
                Height = (int)dm.dmPelsHeight,
                Hz = (int)dm.dmDisplayFrequency,
                DeviceName = dm.dmDeviceName,
                Index = ENUM_CURRENT_SETTINGS,
            };
        }

        /// <summary>列出可用模式，按 宽×高@刷新率 去重，分辨率大的排前面</summary>
        public static List<ResMode> List()
        {
            var outList = new List<ResMode>();
            var seen = new HashSet<string>();

            for (int i = 0; i < 512; i++)
            {
                var dm = NewDevMode();
                if (!EnumDisplaySettings(null, i, ref dm)) break;

                int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, hz = (int)dm.dmDisplayFrequency;
                if (w < 640 || h < 480) continue;

                string key = $"{w}x{h}@{hz}";
                if (!seen.Add(key)) continue;

                outList.Add(new ResMode
                {
                    Width = w,
                    Height = h,
                    Hz = hz,
                    DeviceName = dm.dmDeviceName,
                    Index = i,
                });
            }

            outList.Sort((a, b) =>
            {
                if (a.Width != b.Width) return b.Width.CompareTo(a.Width);
                if (a.Height != b.Height) return b.Height.CompareTo(a.Height);
                return b.Hz.CompareTo(a.Hz);
            });
            return outList;
        }

        public static bool Apply(ResMode m)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettings(null, m.Index, ref dm)) return false;

            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            int r = ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero,
                                            CDS_UPDATEREGISTRY | CDS_GLOBAL, IntPtr.Zero);
            TrayApp.Log($"切换分辨率 {m.Label} -> {(r == DISP_CHANGE_SUCCESSFUL ? "OK" : "失败 " + r)}");
            return r == DISP_CHANGE_SUCCESSFUL;
        }
    }
}
