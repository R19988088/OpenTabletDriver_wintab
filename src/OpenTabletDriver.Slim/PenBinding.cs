using System;
using System.Collections.Generic;
using System.Linq;
using OpenTabletDriver.Plugin.Platform.Pointer;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 一条实际要执行的动作：要么是鼠标键（走原生指针），要么是键盘组合键（走 SendInput）。
    /// 自定义按键在 buttons.json 里存成给人看的文本，比如 "Ctrl+Z"、"鼠标右键"。
    /// </summary>
    public sealed class PenBinding
    {
        public bool IsMouse { get; private init; }
        public PenAction MouseAction { get; private init; }
        public string[] Modifiers { get; private init; } = Array.Empty<string>();
        public ushort VirtualKey { get; private init; }
        public bool IsValid { get; private init; }

        public static readonly PenBinding None = new();

        public static PenBinding FromFunction(PenFunction function)
        {
            var action = function switch
            {
                PenFunction.LeftClick => PenAction.Tip,
                PenFunction.RightClick => PenAction.BarrelButton1,
                PenFunction.MiddleClick => PenAction.BarrelButton2,
                PenFunction.Eraser => PenAction.Eraser,
                _ => (PenAction?)null
            };

            return action == null
                ? None
                : new PenBinding { IsMouse = true, MouseAction = action.Value, IsValid = true };
        }

        /// <summary>解析按钮上显示的那串文本（自定义绑定时用）。</summary>
        public static PenBinding FromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return None;

            // 统一按 "+" 拆：前面是修饰键，最后一个可能是键盘键，也可能是"鼠标右键"这种
            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();
            if (parts.Count == 0)
                return None;

            var last = parts[^1];
            var modifiers = parts.Take(parts.Count - 1).ToArray();

            if (last.StartsWith("鼠标", StringComparison.Ordinal))
            {
                var action = last switch
                {
                    "鼠标左键" => PenAction.Tip,
                    "鼠标右键" => PenAction.BarrelButton1,
                    "鼠标中键" => PenAction.BarrelButton2,
                    _ => (PenAction?)null
                };

                return action == null
                    ? None
                    : new PenBinding
                    {
                        IsMouse = true,
                        MouseAction = action.Value,
                        Modifiers = modifiers,
                        IsValid = true
                    };
            }

            var key = WindowsKeyboard.VirtualKey(last);
            if (key == 0)
                return None;

            return new PenBinding
            {
                IsMouse = false,
                Modifiers = modifiers,
                VirtualKey = key,
                IsValid = true
            };
        }
    }
}
