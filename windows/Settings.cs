// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.IO;
using Microsoft.Win32;

namespace MiTVSwitch
{
    internal static class Settings
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "MiTVSwitch";

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiTVSwitch");

        private static string HostFile => Path.Combine(DataDir, "host.txt");

        /// <summary>显示器地址。优先级：配置文件 > 环境变量 MITV_IP</summary>
        public static string Host
        {
            get
            {
                try
                {
                    if (File.Exists(HostFile))
                    {
                        string s = File.ReadAllText(HostFile).Trim();
                        if (s.Length > 0) return s;
                    }
                }
                catch { }

                string? env = Environment.GetEnvironmentVariable("MITV_IP");
                return string.IsNullOrWhiteSpace(env) ? "" : env.Trim();
            }
            set
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(HostFile, value.Trim());
            }
        }

        private static string FlagPath(string name) => Path.Combine(DataDir, name + ".flag");

        public static bool AutoSwitch
        {
            get => File.Exists(FlagPath("autoswitch"));
            set
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    string p = FlagPath("autoswitch");
                    if (value) File.WriteAllText(p, "");
                    else if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
        }

        private static int ReadInt(string name)
        {
            try
            {
                string p = Path.Combine(DataDir, name + ".txt");
                if (File.Exists(p) && int.TryParse(File.ReadAllText(p).Trim(), out int v)) return v;
            }
            catch { }
            return 0;
        }

        private static void WriteInt(string name, int v)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(Path.Combine(DataDir, name + ".txt"), v.ToString());
            }
            catch { }
        }

        /// <summary>亮度百分比，30…100（没存过就是 100）</summary>
        public static int BrightnessPercent
        {
            get
            {
                int v = ReadInt("brightness");
                return v == 0 ? 100 : Math.Clamp(v, 30, 100);
            }
            set => WriteInt("brightness", Math.Clamp(value, 30, 100));
        }

        /// <summary>色温档位下标</summary>
        public static int GammaTemp
        {
            get => ReadInt("gamma-temp");
            set => WriteInt("gamma-temp", value);
        }

        private static string SceneFlagPath => FlagPath("autoscene");

        /// <summary>按时间自动切换日间 / 夜间场景</summary>
        public static bool AutoScene
        {
            get => File.Exists(SceneFlagPath);
            set
            {
                try
                {
                    Directory.CreateDirectory(DataDir);
                    if (value) File.WriteAllText(SceneFlagPath, "");
                    else if (File.Exists(SceneFlagPath)) File.Delete(SceneFlagPath);
                }
                catch { }
            }
        }

        /// <summary>白天开始的分钟数，默认 08:00</summary>
        public static int SceneDayMinutes
        {
            get
            {
                int v = ReadInt("scene-day");
                return v == 0 ? 8 * 60 : Math.Clamp(v, 0, 1439);
            }
            set => WriteInt("scene-day", Math.Clamp(value, 0, 1439));
        }

        /// <summary>夜间开始的分钟数，默认 20:00</summary>
        public static int SceneNightMinutes
        {
            get
            {
                int v = ReadInt("scene-night");
                return v == 0 ? 20 * 60 : Math.Clamp(v, 0, 1439);
            }
            set => WriteInt("scene-night", Math.Clamp(value, 0, 1439));
        }

        /// <summary>开机自启：写 HKCU 的 Run 键，不需要管理员权限</summary>
        public static bool StartWithWindows
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                    return key?.GetValue(RunValueName) is string s && s.Length > 0;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                    if (key is null) return;

                    if (value)
                    {
                        string exe = Environment.ProcessPath ?? "";
                        if (exe.Length > 0) key.SetValue(RunValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, throwOnMissingValue: false);
                    }
                }
                catch { }
            }
        }
    }
}
