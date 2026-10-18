using System;
using System.Windows.Forms;

namespace MiTVSwitch
{
    /// <summary>
    /// 一个不显示的窗口，专门用来接收：
    ///   - WM_HOTKEY        全局热键
    ///   - WM_POWERBROADCAST 睡眠 / 唤醒通知
    /// 用 NativeWindow 而不是 Form，避免出现任何可见窗口或任务栏图标。
    /// </summary>
    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        public event Action<int>? HotkeyPressed;

        /// <summary>true = 系统恢复，false = 系统即将睡眠</summary>
        public event Action<bool>? PowerChanged;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_HOTKEY:
                    HotkeyPressed?.Invoke(m.WParam.ToInt32());
                    break;

                case WM_POWERBROADCAST:
                    int evt = m.WParam.ToInt32();
                    if (evt == PBT_APMSUSPEND) PowerChanged?.Invoke(false);
                    else if (evt == PBT_APMRESUMEAUTOMATIC) PowerChanged?.Invoke(true);
                    break;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
