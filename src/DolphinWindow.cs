using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace DolphinAchiever
{
    // Finds the screen rectangle Dolphin is actually drawing the game into, so the
    // overlay can sit inside it rather than in a corner of the desktop.
    //
    // Dolphin presents two shapes:
    //   windowed   - the main window (with caption); the game is a child of it, so the
    //                client area is the right target.
    //   fullscreen - a separate borderless top-level window covering the monitor, while
    //                the captioned main window stays open behind it.
    public static class DolphinWindow
    {
        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0x00C00000;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);

        public static Rectangle? GetRenderArea(int pid)
        {
            if (pid <= 0) return null;

            IntPtr borderless = IntPtr.Zero, captioned = IntPtr.Zero;
            int bestBorderless = 0, bestCaptioned = 0;
            bool minimized = false;

            EnumWindows(delegate (IntPtr hwnd, IntPtr param)
            {
                uint wpid;
                GetWindowThreadProcessId(hwnd, out wpid);
                if (wpid != (uint)pid) return true;
                if (!IsWindowVisible(hwnd)) return true;

                RECT r;
                if (!GetWindowRect(hwnd, out r)) return true;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 200 || h < 150) return true;

                if (IsIconic(hwnd)) { minimized = true; return true; }

                bool hasCaption = (GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) != 0;
                int area = w * h;
                if (hasCaption)
                {
                    if (area > bestCaptioned) { bestCaptioned = area; captioned = hwnd; }
                }
                else
                {
                    if (area > bestBorderless) { bestBorderless = area; borderless = hwnd; }
                }
                return true;
            }, IntPtr.Zero);

            // A borderless window means Dolphin went fullscreen; it is on top of the
            // main window, so it wins.
            if (borderless != IntPtr.Zero)
            {
                RECT r;
                if (GetWindowRect(borderless, out r))
                    return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            }

            if (captioned != IntPtr.Zero)
            {
                RECT c;
                if (GetClientRect(captioned, out c))
                {
                    var origin = new POINT { X = 0, Y = 0 };
                    if (ClientToScreen(captioned, ref origin))
                        return new Rectangle(origin.X, origin.Y, c.Right - c.Left, c.Bottom - c.Top);
                }
            }

            if (minimized) return null;
            return null;
        }

        // --- borderless fullscreen -------------------------------------------------
        //
        // Dolphin's own fullscreen is *exclusive* on the Vulkan backend
        // (vkAcquireFullScreenExclusiveModeEXT), and nothing can be drawn over
        // exclusive fullscreen. Its "Borderless Fullscreen" option is written to the
        // config but never read by any backend, so it cannot help.
        //
        // Instead this resizes Dolphin's own window to fill the monitor and strips its
        // border. Dolphin still believes it is windowed, so it never asks for exclusive
        // mode, and the overlay composites normally.

        const int GWL_EXSTYLE = -20;
        const int WS_OVERLAPPEDWINDOW = 0x00CF0000;
        const int WS_POPUP = unchecked((int)0x80000000);
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

        static IntPtr _borderlessWindow = IntPtr.Zero;
        static int _savedStyle, _savedExStyle;
        static Rectangle _savedRect;

        public static bool IsBorderless { get { return _borderlessWindow != IntPtr.Zero; } }

        static IntPtr FindMainWindow(int pid)
        {
            IntPtr best = IntPtr.Zero;
            int bestArea = 0;
            EnumWindows(delegate (IntPtr hwnd, IntPtr param)
            {
                uint wpid;
                GetWindowThreadProcessId(hwnd, out wpid);
                if (wpid != (uint)pid || !IsWindowVisible(hwnd)) return true;
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return true;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 200 || h < 150) return true;
                int area = w * h;
                if (area > bestArea) { bestArea = area; best = hwnd; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        // Returns true if Dolphin is now borderless-fullscreen.
        public static bool ToggleBorderless(int pid)
        {
            if (_borderlessWindow != IntPtr.Zero)
            {
                IntPtr h = _borderlessWindow;
                _borderlessWindow = IntPtr.Zero;
                SetWindowLong(h, GWL_STYLE, _savedStyle);
                SetWindowLong(h, GWL_EXSTYLE, _savedExStyle);
                SetWindowPos(h, IntPtr.Zero, _savedRect.X, _savedRect.Y,
                             _savedRect.Width, _savedRect.Height,
                             SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                return false;
            }

            IntPtr win = FindMainWindow(pid);
            if (win == IntPtr.Zero) return false;

            if (IsIconic(win)) ShowWindow(win, SW_RESTORE);

            RECT wr;
            if (!GetWindowRect(win, out wr)) return false;
            _savedRect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);
            _savedStyle = GetWindowLong(win, GWL_STYLE);
            _savedExStyle = GetWindowLong(win, GWL_EXSTYLE);

            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            IntPtr mon = MonitorFromWindow(win, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (!GetMonitorInfo(mon, ref mi)) return false;

            SetWindowLong(win, GWL_STYLE, (_savedStyle & ~WS_OVERLAPPEDWINDOW) | WS_POPUP);
            SetWindowPos(win, IntPtr.Zero,
                         mi.rcMonitor.Left, mi.rcMonitor.Top,
                         mi.rcMonitor.Right - mi.rcMonitor.Left,
                         mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                         SWP_FRAMECHANGED | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetForegroundWindow(win);
            _borderlessWindow = win;
            return true;
        }

        // Put the window back if the overlay is shutting down.
        public static void RestoreIfBorderless()
        {
            if (_borderlessWindow != IntPtr.Zero) ToggleBorderless(0);
        }
    }
}
