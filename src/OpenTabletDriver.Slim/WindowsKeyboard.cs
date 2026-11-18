using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 往系统里发键盘事件，给"自定义按键"用。
    /// 只做按下/抬起两件事，组合键的按下顺序是 修饰键 → 主键，抬起反过来。
    /// </summary>
    public static class WindowsKeyboard
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public KeyboardInput Keyboard;
            public IntPtr Padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SPACE"] = 0x20,
            ["ENTER"] = 0x0D,
            ["ESC"] = 0x1B,
            ["TAB"] = 0x09,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["INSERT"] = 0x2D,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["UP"] = 0x26,
            ["DOWN"] = 0x28,
            ["LEFT"] = 0x25,
            ["RIGHT"] = 0x27,
            // 缩放用：主键盘上的 +/-（OEM 键，不受输入法影响）
            ["PLUS"] = 0xBB,
            ["MINUS"] = 0xBD
        };

        public static bool IsModifier(string name) =>
            name is "Ctrl" or "Shift" or "Alt" or "Win";

        /// <summary>"Ctrl" / "Z" / "F5" → 虚拟键码。</summary>
        public static ushort VirtualKey(string name)
        {
            if (IsModifier(name))
            {
                return name switch
                {
                    "Ctrl" => 0x11,
                    "Shift" => 0x10,
                    "Alt" => 0x12,
                    _ => 0x5B // Win
                };
            }

            if (name.Length == 1)
            {
                var c = char.ToUpperInvariant(name[0]);
                if (c >= 'A' && c <= 'Z')
                    return (ushort)(0x41 + (c - 'A'));
                if (c >= '0' && c <= '9')
                    return (ushort)(0x30 + (c - '0'));
            }

            if (name.Length is 2 or 3 && (name[0] == 'F' || name[0] == 'f'))
            {
                var number = int.TryParse(name[1..], out var parsed) ? parsed : 0;
                if (number is >= 1 and <= 12)
                    return (ushort)(0x70 + number - 1);
            }

            return Named.TryGetValue(name, out var value) ? value : (ushort)0;
        }

        public static void PressModifiers(IReadOnlyList<string> modifiers)
        {
            var inputs = new List<Input>();
            foreach (var modifier in modifiers)
                inputs.Add(Build(VirtualKey(modifier), false));
            Send(inputs);
        }

        public static void ReleaseModifiers(IReadOnlyList<string> modifiers)
        {
            var inputs = new List<Input>();
            for (var i = modifiers.Count - 1; i >= 0; i--)
                inputs.Add(Build(VirtualKey(modifiers[i]), true));
            Send(inputs);
        }

        public static void Press(IReadOnlyList<string> modifiers, ushort key)
        {
            var inputs = new List<Input>();
            foreach (var modifier in modifiers)
                inputs.Add(Build(VirtualKey(modifier), false));
            inputs.Add(Build(key, false));
            Send(inputs);
        }

        public static void Release(IReadOnlyList<string> modifiers, ushort key)
        {
            var inputs = new List<Input> { Build(key, true) };
            for (var i = modifiers.Count - 1; i >= 0; i--)
                inputs.Add(Build(VirtualKey(modifiers[i]), true));
            Send(inputs);
        }

        private static Input Build(ushort key, bool up) => new()
        {
            Type = InputKeyboard,
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                ScanCode = 0,
                Flags = up ? KeyEventKeyUp : 0
            }
        };

        private static void Send(List<Input> inputs)
        {
            if (inputs.Count == 0)
                return;

            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        }
    }
}
