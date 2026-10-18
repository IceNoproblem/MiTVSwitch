// SPDX-License-Identifier: CC-BY-NC-SA-4.0
using System;
using System.Threading;
using System.Windows.Forms;

namespace MiTVSwitch
{
    internal static class Program
    {
        private static Mutex? _mutex;

        [STAThread]
        private static void Main()
        {
            // 单实例：重复启动时直接退出，交给已在运行的那个
            _mutex = new Mutex(true, @"Local\MiTVSwitch_SingleInstance", out bool created);
            if (!created)
            {
                _mutex.Dispose();
                _mutex = null;
                return;
            }

            try
            {
                ApplicationConfiguration.Initialize();

                // 崩溃时不要让用户看到一堆英文堆栈，记日志就好
                Application.ThreadException += (_, e) => LogCrash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

                Application.Run(new TrayApp());
            }
            finally
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
        }

        private static void LogCrash(Exception? ex)
        {
            if (ex is null) return;
            try
            {
                System.IO.Directory.CreateDirectory(Settings.DataDir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Settings.DataDir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { }
        }
    }
}
