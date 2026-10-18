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
