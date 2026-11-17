using System;
using System.IO;

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTabletDriver.Slim
{
    /// <summary>一个笔上热点能被映射成的鼠标动作。</summary>
    public enum PenFunction
    {
        LeftClick,
        RightClick,
        MiddleClick,
        DoubleClick,
        Eraser,
        None
    }

    /// <summary>
    /// 笔上按键的映射，存 %LOCALAPPDATA%\OTDWintab\buttons.json。
    /// 和 FeelConfig 一样走源生成上下文，裁剪发布后也能正常读写。
    /// </summary>
    public sealed class ButtonsConfig
    {
        /// <summary>笔尖（靠压力触发）。</summary>
        public string Tip { get; set; } = "左键单击";

        /// <summary>靠笔尖那个侧键。</summary>
        public string Barrel1 { get; set; } = "右键单击";

        /// <summary>离笔尖远的那个侧键。</summary>
        public string Barrel2 { get; set; } = "双击";

        /// <summary>橡皮擦（翻过来的笔尾）。</summary>
        public string Eraser { get; set; } = "橡皮擦";

        /// <summary>自定义按键（键盘组合或鼠标键），空字符串表示没用自定义。</summary>
        public string TipCustom { get; set; } = string.Empty;
        public string Barrel1Custom { get; set; } = string.Empty;
        public string Barrel2Custom { get; set; } = string.Empty;

        /// <summary>可以循环切换的动作，按顺序点一下就换下一个。</summary>
        public static readonly string[] Cycle =
        {
            "左键单击", "右键单击", "中键单击", "双击", "无"
        };

        [JsonIgnore]
        public PenFunction TipFunction => Parse(Tip);
        [JsonIgnore]
        public PenFunction Barrel1Function => Parse(Barrel1);
        [JsonIgnore]
        public PenFunction Barrel2Function => Parse(Barrel2);

        public static string Next(string current)
        {
            var index = Array.IndexOf(Cycle, current);
            return Cycle[(index < 0 ? 0 : index + 1) % Cycle.Length];
        }

        public static PenFunction Parse(string? text) => text switch
        {
            "右键单击" => PenFunction.RightClick,
            "中键单击" => PenFunction.MiddleClick,
            "双击" => PenFunction.DoubleClick,
            "橡皮擦" => PenFunction.Eraser,
            "无" => PenFunction.None,
            _ => PenFunction.LeftClick
        };

        public static string Text(PenFunction function) => function switch
        {
            PenFunction.RightClick => "右键单击",
            PenFunction.MiddleClick => "中键单击",
            PenFunction.DoubleClick => "双击",
            PenFunction.Eraser => "橡皮擦",
            PenFunction.None => "无",
            _ => "左键单击"
        };

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OTDWintab",
            "buttons.json");

        public static ButtonsConfig Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), ButtonsJson.Default.ButtonsConfig);
                    if (cfg != null)
                        return cfg;
                }
            }
            catch
            {
                // 配置坏了就按默认值来
            }

            return new ButtonsConfig();
        }

        public void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonSerializer.Serialize(this, ButtonsJson.Default.ButtonsConfig));
            }
            catch
            {
                // 写盘失败不影响界面
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ButtonsConfig))]
    internal partial class ButtonsJson : JsonSerializerContext
    {
    }
}
