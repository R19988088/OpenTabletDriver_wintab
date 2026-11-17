using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 「附加」页的那一堆开关，原本是自己写的 AutoHotKey 脚本，
    /// 这里搬进程序本体，存 %LOCALAPPDATA%\OTDWintab\tweaks.json。
    /// </summary>
    public sealed class Tweaks
    {
        /// <summary>反向（自然）滚动：滚轮上下左右全反过来。</summary>
        public bool InvertScroll { get; set; } = true;

        /// <summary>低延迟模式：把系统计时器精度提到 0.5ms。</summary>
        public bool TimerResolution { get; set; } = true;

        /// <summary>Win 键要按住 1.5 秒才算，轻点不弹开始菜单。</summary>
        public bool WinKeyHold { get; set; } = true;

        /// <summary>CapsLock 改成切换输入法（发 Ctrl+Space），不再切换大小写。</summary>
        public bool CapsLockIme { get; set; } = true;

        /// <summary>鼠标在任务栏上时，滚轮调音量。</summary>
        public bool TaskbarVolume { get; set; } = true;

        /// <summary>选中文件后双击 Shift 复制文件名。</summary>
        public bool DoubleShiftCopyName { get; set; } = true;

        /// <summary>Alt + 右键拖动时把鼠标锁在单方向（PS 调笔刷尺寸用）。</summary>
        public bool PsBrushAxis { get; set; } = true;

        /// <summary>在 Everything 里不让空格/回车/单击把文件名塞进剪贴板。</summary>
        public bool EverythingGuard { get; set; } = true;


        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OTDWintab",
            "tweaks.json");

        public static Tweaks Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), TweaksJson.Default.Tweaks);
                    if (cfg != null)
                        return cfg;
                }
            }
            catch
            {
                // 配置坏了就按默认值来
            }

            return new Tweaks();
        }

        public void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonSerializer.Serialize(this, TweaksJson.Default.Tweaks));
            }
            catch
            {
                // 写盘失败不影响界面
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Tweaks))]
    internal partial class TweaksJson : JsonSerializerContext
    {
    }
}
