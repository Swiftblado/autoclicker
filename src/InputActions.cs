using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AutoClicker
{
    internal interface IInputAction
    {
        void Perform();
    }

    internal enum ClickButton { Left, Right, Middle }

    internal sealed class MouseClickAction : IInputAction
    {
        readonly ClickButton button;
        readonly bool doubleClick;
        readonly bool useFixedPosition;
        readonly int x, y;

        public MouseClickAction(ClickButton button, bool doubleClick, bool useFixedPosition, int x, int y)
        {
            this.button = button;
            this.doubleClick = doubleClick;
            this.useFixedPosition = useFixedPosition;
            this.x = x;
            this.y = y;
        }

        public void Perform()
        {
            if (useFixedPosition)
                NativeMethods.SetCursorPos(x, y);

            uint down, up;
            switch (button)
            {
                case ClickButton.Right:
                    down = NativeMethods.MOUSEEVENTF_RIGHTDOWN; up = NativeMethods.MOUSEEVENTF_RIGHTUP; break;
                case ClickButton.Middle:
                    down = NativeMethods.MOUSEEVENTF_MIDDLEDOWN; up = NativeMethods.MOUSEEVENTF_MIDDLEUP; break;
                default:
                    down = NativeMethods.MOUSEEVENTF_LEFTDOWN; up = NativeMethods.MOUSEEVENTF_LEFTUP; break;
            }

            int clicks = doubleClick ? 2 : 1;
            var inputs = new NativeMethods.INPUT[clicks * 2];
            for (int i = 0; i < clicks; i++)
            {
                inputs[i * 2] = Mouse(down);
                inputs[i * 2 + 1] = Mouse(up);
            }
            Input.Send(inputs);
        }

        static NativeMethods.INPUT Mouse(uint flags)
        {
            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_MOUSE;
            input.U.mi.dwFlags = flags;
            return input;
        }
    }

    internal sealed class KeyPressAction : IInputAction
    {
        readonly Keys key;
        readonly bool ctrl, shift, alt;
        readonly int holdMs;

        public KeyPressAction(Keys key, bool ctrl, bool shift, bool alt, int holdMs)
        {
            this.key = key;
            this.ctrl = ctrl;
            this.shift = shift;
            this.alt = alt;
            this.holdMs = holdMs;
        }

        public void Perform()
        {
            if (ctrl) Input.Key(Keys.ControlKey, false);
            if (shift) Input.Key(Keys.ShiftKey, false);
            if (alt) Input.Key(Keys.Menu, false);

            Input.Key(key, false);
            if (holdMs > 0) Thread.Sleep(holdMs);
            Input.Key(key, true);

            if (alt) Input.Key(Keys.Menu, true);
            if (shift) Input.Key(Keys.ShiftKey, true);
            if (ctrl) Input.Key(Keys.ControlKey, true);
        }
    }

    internal static class Input
    {
        static readonly int InputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        public static void Send(NativeMethods.INPUT[] inputs)
        {
            NativeMethods.SendInput((uint)inputs.Length, inputs, InputSize);
        }

        public static void Key(Keys key, bool up)
        {
            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.U.ki.wVk = (ushort)key;
            input.U.ki.wScan = (ushort)NativeMethods.MapVirtualKey((uint)key, 0);
            uint flags = 0;
            if (IsExtended(key)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
            if (up) flags |= NativeMethods.KEYEVENTF_KEYUP;
            input.U.ki.dwFlags = flags;
            Send(new[] { input });
        }

        public static bool IsExtended(Keys key)
        {
            switch (key)
            {
                case Keys.Insert: case Keys.Delete: case Keys.Home: case Keys.End:
                case Keys.PageUp: case Keys.PageDown:
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.NumLock: case Keys.Divide: case Keys.PrintScreen:
                case Keys.RControlKey: case Keys.RMenu:
                case Keys.LWin: case Keys.RWin: case Keys.Apps:
                    return true;
                default:
                    return false;
            }
        }

        public static string KeyName(Keys key)
        {
            if (key == Keys.None) return "";
            uint scan = NativeMethods.MapVirtualKey((uint)key, 0);
            if (scan != 0)
            {
                int lParam = (int)(scan << 16);
                if (IsExtended(key)) lParam |= 1 << 24;
                var sb = new StringBuilder(64);
                if (NativeMethods.GetKeyNameText(lParam, sb, sb.Capacity) > 0)
                    return sb.ToString();
            }
            return key.ToString();
        }
    }
}
