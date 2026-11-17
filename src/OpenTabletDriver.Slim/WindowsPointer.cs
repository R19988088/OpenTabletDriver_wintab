using System;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTabletDriver.Native.Windows.Input;
using OpenTabletDriver.Plugin.Platform.Pointer;
using Win32 = OpenTabletDriver.Native.Windows.Windows;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 精简版内置的 Windows 指针：把管线算出的屏幕坐标用 SendInput 注入，
    /// 同时实现压力/按键接口，供 Wintab 输出模式透传。
    /// 起点只保留绝对定位 + 鼠标按键，滚轮/相对模式后续再补。
    /// </summary>
    public sealed class WindowsPointer : IAbsolutePointer, IMouseButtonHandler, IPressureHandler,
        ITiltHandler, ISynchronousPointer
    {
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        public WindowsPointer()
        {
            var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (width <= 0) width = GetSystemMetrics(SM_CXSCREEN);
            if (height <= 0) height = GetSystemMetrics(SM_CYSCREEN);
            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;

            ScreenSize = new Vector2(width, height);
            Origin = new Vector2(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN));
        }

        public Vector2 ScreenSize { get; }
        private readonly Vector2 Origin;

        private Vector2 pending;
        private bool hasPending;
        private float pressure;

        public float Pressure => pressure;

        public void SetPosition(Vector2 pos)
        {
            pending = pos;
            hasPending = true;
        }

        public void SetPressure(float percentage) => pressure = percentage;

        public void SetTilt(Vector2 tilt)
        {
            // Wintab 输出模式自己会把倾斜写进共享内存，这里不需要再注入
        }

        public void MouseDown(MouseButton button) => SendButton(button, down: true);

        public void MouseUp(MouseButton button) => SendButton(button, down: false);

        public void Flush()
        {
            if (!hasPending)
                return;
            hasPending = false;
            MoveTo(pending);
        }

        public void Reset()
        {
            hasPending = false;
            pressure = 0;
        }

        private void MoveTo(Vector2 pos)
        {
            var locked = HotkeyEngine.ClampAxis(pos.X, pos.Y);
            var x = Normalize(locked.X - Origin.X, ScreenSize.X);
            var y = Normalize(locked.Y - Origin.Y, ScreenSize.Y);
            Send(MOUSEEVENTF.ABSOLUTE | MOUSEEVENTF.MOVE | MOUSEEVENTF.VIRTUALDESK, x, y);
        }

        private static int Normalize(float value, float extent)
        {
            if (extent <= 0)
                return 0;
            var ratio = Math.Clamp(value / extent, 0f, 1f);
            return (int)Math.Round(ratio * 65535);
        }

        private static void SendButton(MouseButton button, bool down)
        {
            var flag = button switch
            {
                MouseButton.Left => down ? MOUSEEVENTF.LEFTDOWN : MOUSEEVENTF.LEFTUP,
                MouseButton.Right => down ? MOUSEEVENTF.RIGHTDOWN : MOUSEEVENTF.RIGHTUP,
                MouseButton.Middle => down ? MOUSEEVENTF.MIDDLEDOWN : MOUSEEVENTF.MIDDLEUP,
                _ => default(MOUSEEVENTF)
            };
            if (flag == 0)
                return;
            Send((MOUSEEVENTF)flag);
        }

        private static void Send(MOUSEEVENTF flags, int dx = 0, int dy = 0)
        {
            var input = new INPUT { type = INPUT_TYPE.MOUSE_INPUT };
            input.U.mi = new MOUSEINPUT
            {
                dx = dx,
                dy = dy,
                mouseData = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = UIntPtr.Zero
            };
            Win32.SendInput(1, [input], INPUT.Size);
        }
    }
}
