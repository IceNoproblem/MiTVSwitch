// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MiTVSwitch
{
    internal sealed class KeyBinding
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        public uint Mods;
        public Keys Key;

        public bool IsEmpty => Key == Keys.None || Mods == 0;

        /// <summary>转成 RegisterHotKey 用的修饰键（不含 NOREPEAT）</summary>
        public uint ToRegisterMods() => Mods & (MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN);

        public string Display()
        {
            if (IsEmpty) return "未设置";
            var parts = new List<string>();
            if ((Mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((Mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Mods & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }

        public static string KeyName(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            return code switch
            {
                Keys.D0 => "0", Keys.D1 => "1", Keys.D2 => "2", Keys.D3 => "3", Keys.D4 => "4",
                Keys.D5 => "5", Keys.D6 => "6", Keys.D7 => "7", Keys.D8 => "8", Keys.D9 => "9",
                Keys.Up => "↑", Keys.Down => "↓", Keys.Left => "←", Keys.Right => "→",
                Keys.Space => "空格",
                Keys.Oemtilde => "`", Keys.OemMinus => "-", Keys.Oemplus => "=",
                _ => code.ToString(),
            };
        }

        public string Serialize() => $"{(uint)Mods}:{(int)Key}";

        public static KeyBinding? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string[] p = s.Split(':');
            if (p.Length != 2) return null;
            if (!uint.TryParse(p[0], out uint m)) return null;
            if (!int.TryParse(p[1], out int k)) return null;
            return new KeyBinding { Mods = m, Key = (Keys)k };
        }
    }

    /// <summary>
    /// 快捷键配置。用户改过的存在 %APPDATA%\MiTVSwitch\hotkeys.txt，
    /// 没存过就用默认值；文件里写成空表示「这个动作不绑键」。
    /// </summary>
    internal static class Hotkeys
    {
        private static string FilePath => Path.Combine(Settings.DataDir, "hotkeys.txt");

        public static Dictionary<string, KeyBinding> Defaults()
        {
            var m = new Dictionary<string, KeyBinding>
            {
                ["src.HDMI1"] = new KeyBinding { Mods = KeyBinding.MOD_CONTROL | KeyBinding.MOD_ALT, Key = Keys.D1 },
                ["src.HDMI2"] = new KeyBinding { Mods = KeyBinding.MOD_CONTROL | KeyBinding.MOD_ALT, Key = Keys.D2 },
                ["vol.up"] = new KeyBinding { Mods = KeyBinding.MOD_CONTROL | KeyBinding.MOD_ALT | KeyBinding.MOD_SHIFT, Key = Keys.Up },
                ["vol.down"] = new KeyBinding { Mods = KeyBinding.MOD_CONTROL | KeyBinding.MOD_ALT | KeyBinding.MOD_SHIFT, Key = Keys.Down },
                ["bright.up"] = new KeyBinding { Mods = KeyBinding.MOD_CONTROL | KeyBinding.MOD_ALT, Key = Keys.Up },
                ["bright.down"] = new KeyBinding { Mods = KeyBinding.MOD_CONTROL | KeyBinding.MOD_ALT, Key = Keys.Down },
            };
            return m;
        }

        private static Dictionary<string, string> LoadRaw()
        {
            var raw = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(FilePath)) return raw;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    raw[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
            catch { }
            return raw;
        }

        private static void SaveRaw(Dictionary<string, string> raw)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.WriteAllLines(FilePath,
                    raw.Select(kv => $"{kv.Key}={kv.Value}"), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>某个动作当前生效的快捷键；null 表示不绑</summary>
        public static KeyBinding? Get(string action)
        {
            var raw = LoadRaw();
            if (raw.TryGetValue(action, out string? stored))
            {
                if (string.IsNullOrEmpty(stored)) return null;   // 用户主动清空
                return KeyBinding.Parse(stored);
            }
            return Defaults().TryGetValue(action, out var d) ? d : null;
        }

        public static void Set(string action, KeyBinding? b)
        {
            var raw = LoadRaw();
            raw[action] = b is null ? "" : b.Serialize();
            SaveRaw(raw);
        }

        public static void Reset()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }

        /// <summary>所有可绑定的动作（id, 显示名, 分组）</summary>
        public static List<(string Id, string Title, string Group)> Actions()
        {
            var list = new List<(string, string, string)>();
            foreach (var s in TrayApp.Sources)
                list.Add(($"src.{s.Key}", $"切到 {s.Title}", "信号源"));
            list.Add(("vol.up", "音量 +", "音量"));
            list.Add(("vol.down", "音量 −", "音量"));
            list.Add(("bright.up", "亮度 +", "画面"));
            list.Add(("bright.down", "亮度 −", "画面"));
            return list;
        }
    }
}
