using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using static ScreenDeck.Native;

namespace ScreenDeck
{
    public enum MouseButtonKind { Left, Right, Middle }

    /// <summary>
    /// Sends mouse input to an arbitrary screen point and returns the cursor to where it was.
    /// Windows has a single cursor, so each action is one SendInput batch (move → act → move back),
    /// which the system delivers without interleaving other input. Coordinates are physical pixels.
    /// </summary>
    internal static class InputInjector
    {
        public static void Click(int x, int y, MouseButtonKind button)
        {
            var (down, up) = Flags(button);
            Send(Batch(x, y, Mouse(down), Mouse(up)));
        }

        public static void Wheel(int x, int y, int delta)
        {
            Send(Batch(x, y, new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = delta }));
        }

        /// <summary>Press at (x1,y1), move in steps to (x2,y2), release. Steps are paced so apps see a real drag.</summary>
        public static void Drag(int x1, int y1, int x2, int y2, MouseButtonKind button)
        {
            var (down, up) = Flags(button);
            GetCursorPos(out POINT home);
            Send(new[] { MoveTo(x1, y1), Input(Mouse(down)) });
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                Thread.Sleep(8);
                Send(new[] { MoveTo(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps) });
            }
            Thread.Sleep(8);
            Send(new[] { Input(Mouse(up)), MoveTo(home.X, home.Y) });
        }

        private static INPUT[] Batch(int x, int y, params MOUSEINPUT[] actions)
        {
            GetCursorPos(out POINT home);
            var list = new List<INPUT> { MoveTo(x, y) };
            foreach (var a in actions) list.Add(Input(a));
            list.Add(MoveTo(home.X, home.Y));
            return list.ToArray();
        }

        private static (uint down, uint up) Flags(MouseButtonKind b) => b switch
        {
            MouseButtonKind.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButtonKind.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };

        private static MOUSEINPUT Mouse(uint flags) => new() { dwFlags = flags };

        private static INPUT Input(MOUSEINPUT mi) => new() { type = INPUT_MOUSE, mi = mi };

        // Absolute coordinates are normalized to 0..65535 across the whole virtual desktop.
        private static INPUT MoveTo(int x, int y)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
            return Input(new MOUSEINPUT
            {
                dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)),
                dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1)),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
            });
        }

        private static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
