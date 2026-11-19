using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 映射设置，对应原版 OTD 的 AbsoluteModeSettings：
    ///   Display —— 显示器区域（像素，X/Y 是区域中心，坐标在虚拟桌面空间里）
    ///   Tablet  —— 数位板区域（毫米，X/Y 是区域中心，Rotation 是旋转角度）
    ///   LockAspectRatio —— 数位板宽高比锁定为显示器区域的宽高比
    ///
    /// 存到 %LOCALAPPDATA%\OTDWintab\mapping.json。
    /// 和 feel.json 一样用源生成序列化，避免裁剪发布后反射元数据被裁掉。
    /// </summary>
    public sealed class MappingConfig
    {
        public string DisplayDevice { get; set; } = "";

        public double DisplayWidth { get; set; }
        public double DisplayHeight { get; set; }
        public double DisplayX { get; set; }
        public double DisplayY { get; set; }

        public double TabletWidth { get; set; }
        public double TabletHeight { get; set; }
        public double TabletX { get; set; }
        public double TabletY { get; set; }
        public double TabletRotation { get; set; }

        /// <summary>ExpressKey 在数位板左侧(false)还是右侧(true)。右侧时映射整体旋转 180°。</summary>
        public bool ButtonsOnRight { get; set; }

        /// <summary>数位板有效区域的物理尺寸（毫米），用来把选区夹在板子范围内。</summary>
        public double DigitizerWidth { get; set; }
        public double DigitizerHeight { get; set; }

        public bool LockAspectRatio { get; set; } = true;
        public bool EnableClipping { get; set; } = true;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OTDWintab",
            "mapping.json");

        public static MappingConfig Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), MappingJson.Default.MappingConfig);
                    if (cfg != null)
                        return cfg;
                }
            }
            catch
            {
                // 配置损坏就按默认值来
            }

            return new MappingConfig();
        }

        public void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonSerializer.Serialize(this, MappingJson.Default.MappingConfig));
            }
            catch
            {
                // 写盘失败不该影响界面
            }
        }

        /// <summary>
        /// 补齐默认值：没配过显示器就选主显示器并铺满，没配过数位板就用整块板子。
        /// </summary>
        public void EnsureDefaults(double digitizerWidthMm, double digitizerHeightMm)
        {
            DigitizerWidth = digitizerWidthMm;
            DigitizerHeight = digitizerHeightMm;

            var display = Displays.Find(DisplayDevice) ?? Displays.Primary() ?? FirstDisplay();
            if (display != null)
            {
                if (string.IsNullOrEmpty(DisplayDevice))
                    DisplayDevice = display.Device;

                if (DisplayWidth <= 0 || DisplayHeight <= 0)
                {
                    DisplayWidth = display.Width;
                    DisplayHeight = display.Height;
                    DisplayX = display.X + display.Width / 2.0;
                    DisplayY = display.Y + display.Height / 2.0;
                }
            }

            if (TabletWidth <= 0 || TabletHeight <= 0)
            {
                TabletWidth = digitizerWidthMm;
                TabletHeight = digitizerHeightMm;
                TabletX = digitizerWidthMm / 2.0;
                TabletY = digitizerHeightMm / 2.0;
            }

            if (LockAspectRatio)
                LockTabletToDisplay();
        }

        /// <summary>数位板区域按显示器区域的宽高比走（高度跟着宽度），并把选区夹回板子范围内。</summary>
        public void LockTabletToDisplay()
        {
            if (DisplayWidth <= 0 || DisplayHeight <= 0 || TabletWidth <= 0)
                return;

            TabletHeight = Math.Round(TabletWidth * (DisplayHeight / DisplayWidth), 1);
            ClampTabletArea();
        }

        private void ClampTabletArea()
        {
            var fullW = DigitizerWidth > 0 ? DigitizerWidth : TabletWidth;
            var fullH = DigitizerHeight > 0 ? DigitizerHeight : TabletHeight;

            if (TabletWidth > fullW)
            {
                TabletWidth = fullW;
                if (DisplayWidth > 0 && DisplayHeight > 0)
                    TabletHeight = Math.Round(TabletWidth * (DisplayHeight / DisplayWidth), 1);
            }

            if (TabletHeight > fullH)
                TabletHeight = fullH;

            TabletX = TabletX <= 0 ? fullW / 2.0 : Math.Clamp(TabletX, TabletWidth / 2.0, fullW - TabletWidth / 2.0);
            TabletY = TabletY <= 0 ? fullH / 2.0 : Math.Clamp(TabletY, TabletHeight / 2.0, fullH - TabletHeight / 2.0);
        }

        public double DisplayRatio => DisplayHeight > 0 ? DisplayWidth / DisplayHeight : 1.0;

        /// <summary>把显示器区域重置成该显示器的整块面积。</summary>
        public void FillDisplay(Displays.Info display)
        {
            DisplayDevice = display.Device;
            DisplayWidth = display.Width;
            DisplayHeight = display.Height;
            DisplayX = display.X + display.Width / 2.0;
            DisplayY = display.Y + display.Height / 2.0;

            if (LockAspectRatio)
                LockTabletToDisplay();
        }

        /// <summary>
        /// 显示器拓扑变了之后把区域拉回现实：那块屏没了就退回主屏铺满；
        /// 屏还在但分辨率/位置变了：原来铺满屏幕的直接铺满新屏，其余只把中心挪回屏内。
        /// 返回 true 表示改过，调用方要保存并重新下发。
        /// </summary>
        public bool RefitToDisplays()
        {
            var all = Displays.Enum();
            if (all.Count == 0)
                return false;

            var display = Displays.Find(DisplayDevice);
            if (display == null)
            {
                FillDisplay(Displays.Primary() ?? all[0]);
                return true;
            }

            if (DisplayWidth > 0 && DisplayHeight > 0 && IsInside(display))
                return false;

            var target = display;

            // 原来就是"铺满整块屏"的映射（双屏时经常是铺满整个虚拟桌面），现在屏变了
            // 就直接铺满新屏。按比例缩会把 32:9 的区域压成屏幕中间一条，
            // 板子又只剩一半能用，等于没修。
            if (DisplayWidth <= 0 || DisplayHeight <= 0
                || DisplayWidth >= target.Width || DisplayHeight >= target.Height)
            {
                FillDisplay(target);
                return true;
            }

            // 区域比屏还宽/高，但没到"铺满"的程度：只把中心挪回屏内，尺寸不动，
            // 超出部分靠 EnableClipping 夹住，和用户在映射页看到的效果一致。
            var minX = target.X + DisplayWidth / 2.0;
            var maxX = target.X + target.Width - DisplayWidth / 2.0;
            var minY = target.Y + DisplayHeight / 2.0;
            var maxY = target.Y + target.Height - DisplayHeight / 2.0;

            DisplayX = DisplayWidth >= target.Width ? target.X + target.Width / 2.0 : Math.Clamp(DisplayX, minX, maxX);
            DisplayY = DisplayHeight >= target.Height ? target.Y + target.Height / 2.0 : Math.Clamp(DisplayY, minY, maxY);

            if (LockAspectRatio)
                LockTabletToDisplay();

            return true;
        }

        /// <summary>区域是否完整落在该显示器矩形内（留 1px 容差，避免四舍五入来回抖）。</summary>
        private bool IsInside(Displays.Info display)
        {
            var left = DisplayX - DisplayWidth / 2.0;
            var top = DisplayY - DisplayHeight / 2.0;
            var right = DisplayX + DisplayWidth / 2.0;
            var bottom = DisplayY + DisplayHeight / 2.0;

            return left >= display.X - 1 && top >= display.Y - 1
                && right <= display.X + display.Width + 1
                && bottom <= display.Y + display.Height + 1;
        }

        private static Displays.Info? FirstDisplay()
        {
            var all = Displays.Enum();
            return all.Count > 0 ? all[0] : null;
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(MappingConfig))]
    internal sealed partial class MappingJson : JsonSerializerContext
    {
    }
}
