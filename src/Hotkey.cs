using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DolphinAchiever
{
    // A hidden message-only window that claims Alt+Enter system-wide.
    //
    // Registering the hotkey means Dolphin never receives the keystroke, so it cannot
    // switch to exclusive fullscreen; the overlay makes Dolphin's window borderless
    // instead, which looks the same but lets anything be drawn over it.
    public class HotkeyWindow : NativeWindow, IDisposable
    {
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x0001;
        const uint MOD_NOREPEAT = 0x4000;
        const uint VK_RETURN = 0x0D;
        const int HotkeyId = 0xDA01;

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        readonly Action _onPressed;
        bool _registered;

        public bool Registered { get { return _registered; } }

        public HotkeyWindow(Action onPressed)
        {
            _onPressed = onPressed;
            CreateHandle(new CreateParams
            {
                Caption = "DolphinAchieverHotkey",
                X = 0, Y = 0, Width = 0, Height = 0,
                Style = 0,
                Parent = (IntPtr)(-3)      // HWND_MESSAGE
            });
            _registered = RegisterHotKey(Handle, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_RETURN);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId && _onPressed != null)
            {
                try { _onPressed(); } catch { }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_registered)
            {
                UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
