using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTabletDriver.Slim
{
    /// <summary>
    /// 手感参数，和 wintab-shim 共用的同一个文件：
    /// %LOCALAPPDATA%\OTDWintab\feel.json
    ///
    /// 注意：**不能用反射式 JsonSerializer**。裁剪发布（PublishTrimmed）会把反射元数据裁掉，
    /// 反序列化不报错但会悄悄退回默认值 —— 表现为界面里所有数字都是 0/60/2/100%。
    /// 这里走源生成上下文，裁剪后也能正常读写。
    /// </summary>
    public sealed class FeelConfig
    {
        public double DeadZone { get; set; }
        public double Elasticity { get; set; } = 6.0;
        public int StrokeFilter { get; set; } = 2;
        public double MaxPressure { get; set; } = 1.0;
        public string InputMode { get; set; } = "winTab";

        // 「输入」页底部那三个触摸开关。只由本程序写，wintab-shim 不读它们，
        // 开关状态直接决定 TouchEngine 要不要把对应手势翻译成按键。
        public bool TouchUndo { get; set; } = true;
        public bool TouchRotate { get; set; } = true;
        public bool TouchZoom { get; set; } = true;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OTDWintab",
            "feel.json");

        public static FeelConfig Load()
        {
            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var cfg = JsonSerializer.Deserialize(File.ReadAllText(path), FeelJson.Default.FeelConfig);
                    if (cfg != null)
                        return cfg;
                }
            }
            catch
            {
                // 配置损坏就按默认值来
            }

            return new FeelConfig();
        }

        public void Save()
        {
            try
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(this, FeelJson.Default.FeelConfig));
            }
            catch
            {
                // 写盘失败不该影响界面操作
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(FeelConfig))]
    internal partial class FeelJson : JsonSerializerContext
    {
    }
}
