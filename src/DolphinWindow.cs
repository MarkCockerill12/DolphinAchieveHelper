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
    }
}
