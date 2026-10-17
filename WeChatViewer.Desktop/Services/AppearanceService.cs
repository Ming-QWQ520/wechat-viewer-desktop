using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// 外观设置：背景模式 / 主题 / 自定义背景图 / 模糊 / 压暗 / 面板透明度 / 消息字号 / 气泡配色。
/// </summary>
public class AppearanceSettings
{
    /// <summary>mica | acrylic | image</summary>
    public string Mode { get; set; } = "acrylic";
    /// <summary>auto | light | dark</summary>
    public string Theme { get; set; } = "auto";
    public string ImagePath { get; set; } = "";
    /// <summary>0=原图，1-4 模糊级别（仅自定义背景图模式）</summary>
    public int Blur { get; set; } = 2;
    /// <summary>0-70，背景压暗百分比（仅自定义背景图模式）</summary>
    public int Dim { get; set; } = 15;
    /// <summary>30-100，侧栏/主区面板不透明度百分比（数值越小越通透）</summary>
    public int PanelOpacity { get; set; } = 80;
    /// <summary>12-20，消息正文基准字号</summary>
    public int FontSize { get; set; } = 14;
    /// <summary>green | blue | pink | purple | orange（自己气泡的颜色）</summary>
    public string BubbleColor { get; set; } = "green";
}

/// <summary>
/// 窗口外观：三个背景模式全部改为“应用层背景图层”渲染，跨 Windows 版本表现一致：
///   - mica    桌面壁纸 → 极强模糊 + 降饱和 + 压暗（Mica 质感）
///   - acrylic 桌面壁纸 → 中度模糊 + 白雾提亮（液态玻璃通透感）
///   - image   自定义全屏背景图（可调模糊/压暗）
/// 原实现依赖系统 SystemBackdrop（Mica/Acrylic 类），在不支持的 Windows 版本上两种
/// 模式都会退化成同一纯色，用户反馈“两个选项无差别”；改用应用层渲染后差异确定可见。
/// </summary>
public static class AppearanceService
{
    private static string SettingsFile => Path.Combine(NativeTools.DataDir, "appearance.json");

    public static AppearanceSettings Load()
    {
        try
        {
            var json = File.ReadAllText(SettingsFile);
            var settings = JsonSerializer.Deserialize<AppearanceSettings>(json);
            if (settings != null) return Normalize(settings);
        }
        catch
        {
            // 首次运行或文件损坏 → 默认值
        }
        return new AppearanceSettings();
    }

    public static void Save(AppearanceSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort
        }
    }

    private static AppearanceSettings Normalize(AppearanceSettings s)
    {
        s.Mode = s.Mode is "mica" or "acrylic" or "image" ? s.Mode : "acrylic";
        s.Theme = s.Theme is "light" or "dark" ? s.Theme : "auto";
        s.Blur = Math.Clamp(s.Blur, 0, 4);
        s.Dim = Math.Clamp(s.Dim, 0, 70);
        s.PanelOpacity = Math.Clamp(s.PanelOpacity <= 0 ? 80 : s.PanelOpacity, 30, 100);
        s.FontSize = Math.Clamp(s.FontSize <= 0 ? 14 : s.FontSize, 12, 20);
        s.BubbleColor = BubbleColorFor(s.BubbleColor).Key == s.BubbleColor ? s.BubbleColor : "green";
        if (s.Mode == "image" && (string.IsNullOrEmpty(s.ImagePath) || !File.Exists(s.ImagePath))) s.Mode = "acrylic";
        return s;
    }

    // ---------- backdrop & brushes ----------

    /// <summary>可选择的气泡配色（key / 显示名 / RGB）。</summary>
    public static readonly (string Key, string Label, byte R, byte G, byte B)[] BubbleColorOptions =
    {
        ("green", "微信绿", 0x95, 0xEC, 0x69),
        ("blue", "晴空蓝", 0x5F, 0xB8, 0xFF),
        ("pink", "蜜桃粉", 0xFF, 0xA8, 0xC8),
        ("purple", "薰衣紫", 0xB9, 0x9F, 0xF0),
        ("orange", "暖阳橙", 0xFF, 0xC2, 0x69),
    };

    private static (string Key, string Label, byte R, byte G, byte B) BubbleColorFor(string key)
    {
        foreach (var option in BubbleColorOptions)
        {
            if (option.Key == (key ?? "")) return option;
        }
        return BubbleColorOptions[0];
    }

    /// <summary>面板不透明度：各模式叠加系数（液态玻璃/壁纸模式更通透）。</summary>
    private static byte PanelAlpha(AppearanceSettings s)
    {
        var alpha = 255 * s.PanelOpacity / 100;
        var factor = s.Mode switch
        {
            "acrylic" => 0.85,   // 液态玻璃：面板更透
            "image" => 0.72,     // 自定义背景：让壁纸清晰可见（少量遮罩）
            _ => 1.0,            // Mica 质感：面板较实
        };
        return (byte)Math.Clamp((int)Math.Round(alpha * factor), 30, 255);
    }

    /// <summary>按模式设置主题/画刷；背景一律由应用层图层承载（跨系统一致）。</summary>
    public static void Apply(AppearanceSettings s, Window window)
    {
        window.SystemBackdrop = null; // 背景图层铺满整个窗口（见类注释）
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = s.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        ApplyPanelBrushes(s);
    }

    /// <summary>重写 Wx 主题画刷：面板按模式透明度，气泡保持高可读性（不低于 88%）。</summary>
    private static void ApplyPanelBrushes(AppearanceSettings s)
    {
        var resources = Application.Current.Resources;
        var panelA = PanelAlpha(s);
        // 气泡/引用块保持接近不透明，避免文字压在背景上难以阅读
        var bubbleA = (byte)Math.Max(226, (int)panelA);
        var dim = s.Mode == "image" ? 0.96 : 0.92; // 通透模式下再略降
        var light = (ResourceDictionary)resources.ThemeDictionaries["Light"];
        var dark = (ResourceDictionary)resources.ThemeDictionaries["Dark"];
        var bubble = BubbleColorFor(s.BubbleColor);

        SetBrush(light, "WxSidebarBrush", 0xED, 0xED, 0xE9, Scale(panelA, dim));
        SetBrush(light, "WxMainBrush", 0xF7, 0xF7, 0xF5, Scale(panelA, dim));
        SetBrush(light, "WxHeaderBrush", 0xF7, 0xF7, 0xF7, Scale(panelA, dim));
        SetBrush(light, "WxBubbleInBrush", 0xFF, 0xFF, 0xFF, bubbleA);
        SetBrush(light, "WxBubbleOutBrush", bubble.R, bubble.G, bubble.B, bubbleA);
        SetBrush(light, "WxDaySepBrush", 0xE4, 0xE4, 0xE4, (byte)Math.Max(150, bubbleA - 60));

        SetBrush(dark, "WxSidebarBrush", 0x25, 0x25, 0x25, Scale(panelA, dim));
        SetBrush(dark, "WxMainBrush", 0x20, 0x20, 0x20, Scale(panelA, dim));
        SetBrush(dark, "WxHeaderBrush", 0x28, 0x28, 0x28, Scale(panelA, dim));
        SetBrush(dark, "WxBubbleInBrush", 0x33, 0x33, 0x33, bubbleA);
        SetBrush(dark, "WxBubbleOutBrush", bubble.R, bubble.G, bubble.B, bubbleA);
        SetBrush(dark, "WxDaySepBrush", 0x3A, 0x3A, 0x3A, (byte)Math.Max(150, bubbleA - 60));
    }

    private static byte Scale(byte alpha, double factor)
        => (byte)Math.Clamp((int)Math.Round(alpha * factor), 40, 255);

    private static void SetBrush(ResourceDictionary dict, string key, byte r, byte g, byte b, byte a)
    {
        dict[key] = new SolidColorBrush(new Windows.UI.Color { A = a, R = r, G = g, B = b });
    }

    // ---------- wallpaper / backdrop image ----------

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, StringBuilder pvParam, uint fWinIni);

    private const uint SpiGetDesktopWallpaper = 0x0014;

    /// <summary>当前桌面壁纸路径（Mica / 液态玻璃模式的取材来源）。</summary>
    public static string GetDesktopWallpaper()
    {
        try
        {
            var sb = new StringBuilder(520);
            return SystemParametersInfoW(SpiGetDesktopWallpaper, (uint)sb.Capacity, sb, 0) && sb.Length > 0
                ? sb.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 生成当前模式的全窗口背景图（带磁盘缓存）：
    ///   mica：壁纸 极强模糊 + 降饱和 45% + 压暗 30%
    ///   acrylic：壁纸 中度模糊 + 白雾 18% + 提亮 6%（液态玻璃）
    ///   image：自定义图（可选模糊 + 压暗）
    /// 壁纸不可用时退化为柔和渐变底图。返回 null 表示无图可用。
    /// </summary>
    public static Task<string> BuildBackdropImageAsync(AppearanceSettings s)
    {
        if (s.Mode == "image")
        {
            if (string.IsNullOrEmpty(s.ImagePath) || !File.Exists(s.ImagePath)) return Task.FromResult<string>(null);
            var factor = s.Blur switch { 0 => 0, 1 => 10, 2 => 20, 3 => 32, _ => 48 };
            return ProcessImageAsync(s.ImagePath, factor, dim: s.Dim, sat: 1.0f, haze: 0f, bright: 1.0f,
                keySuffix: $"image|{s.Blur}|{s.Dim}");
        }
        var wallpaper = GetDesktopWallpaper();
        if (string.IsNullOrEmpty(wallpaper) || !File.Exists(wallpaper))
        {
            return Task.FromResult(GradientFile(s.Mode));
        }
        if (s.Mode == "mica")
        {
            return ProcessImageAsync(wallpaper, 64, dim: 30, sat: 0.45f, haze: 0f, bright: 1.0f,
                keySuffix: "mica");
        }
        return ProcessImageAsync(wallpaper, 20, dim: 0, sat: 0.9f, haze: 0.18f, bright: 1.06f,
            keySuffix: "acrylic");
    }

    /// <summary>降采样-升采样磨砂模糊 + ColorMatrix（饱和度/压暗/白雾/亮度）处理。</summary>
    private static Task<string> ProcessImageAsync(string sourcePath, int blurFactor, int dim,
        float sat, float haze, float bright, string keySuffix)
    {
        return Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(sourcePath);
                var cacheKey = $"{sourcePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{keySuffix}" +
                               $"|{blurFactor}|{dim}|{sat}|{haze}|{bright}";
                var cacheFile = Path.Combine(NativeTools.DataDir, "media-cache",
                    $"bg-{Md5Short(cacheKey)}.png");
                if (File.Exists(cacheFile)) return cacheFile;
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));

                using var source = new System.Drawing.Bitmap(sourcePath);
                int width = Math.Max(2, source.Width);
                int height = Math.Max(2, source.Height);
                System.Drawing.Bitmap drawn = source;

                // 1) 磨砂模糊：降采样 → 升采样
                if (blurFactor > 1)
                {
                    int smallW = Math.Max(2, width / blurFactor);
                    int smallH = Math.Max(2, height / blurFactor);
                    using var small = new System.Drawing.Bitmap(smallW, smallH);
                    using (var g = System.Drawing.Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(source, new System.Drawing.Rectangle(0, 0, smallW, smallH));
                    }
                    var blurred = new System.Drawing.Bitmap(width, height);
                    using (var g = System.Drawing.Graphics.FromImage(blurred))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        g.DrawImage(small, new System.Drawing.Rectangle(0, 0, width, height));
                    }
                    drawn = blurred;
                }

                // 2) 色调处理：饱和度 / 亮度 / 白雾 / 压暗
                bool needsMatrix = Math.Abs(sat - 1f) > 0.001f || Math.Abs(bright - 1f) > 0.001f
                    || haze > 0f || dim > 0;
                var output = drawn;
                if (needsMatrix)
                {
                    var matrix = ColorMatrixFor(sat, dim, haze, bright);
                    var processed = new System.Drawing.Bitmap(width, height);
                    using (var g = System.Drawing.Graphics.FromImage(processed))
                    {
                        using var attributes = new System.Drawing.Imaging.ImageAttributes();
                        attributes.SetColorMatrix(matrix);
                        g.DrawImage(drawn, new System.Drawing.Rectangle(0, 0, width, height),
                            0, 0, width, height, System.Drawing.GraphicsUnit.Pixel, attributes);
                    }
                    output = processed;
                }

                try
                {
                    output.Save(cacheFile, System.Drawing.Imaging.ImageFormat.Png);
                    return cacheFile;
                }
                finally
                {
                    if (!ReferenceEquals(output, drawn)) output.Dispose();
                    if (!ReferenceEquals(drawn, source)) drawn.Dispose();
                }
            }
            catch
            {
                return sourcePath; // 处理失败 → 原图
            }
        });
    }

    /// <summary>饱和度 + 压暗(%) + 白雾(alpha 0-1) + 亮度 合成矩阵。</summary>
    private static System.Drawing.Imaging.ColorMatrix ColorMatrixFor(float sat, int dimPercent, float haze, float bright)
    {
        const float lr = 0.3086f, lg = 0.6094f, lb = 0.0820f;
        float scale = bright * (1f - dimPercent / 100f);
        float offset = haze; // 白雾：向白色偏移 haze（0-1 → 0-255 由矩阵偏移列完成）
        var m = new float[5][];
        for (int i = 0; i < 5; i++) m[i] = new float[5];
        m[0][0] = (lr * (1f - sat) + sat) * scale;
        m[0][1] = lg * (1f - sat) * scale;
        m[0][2] = lb * (1f - sat) * scale;
        m[1][0] = lr * (1f - sat) * scale;
        m[1][1] = (lg * (1f - sat) + sat) * scale;
        m[1][2] = lb * (1f - sat) * scale;
        m[2][0] = lr * (1f - sat) * scale;
        m[2][1] = lg * (1f - sat) * scale;
        m[2][2] = (lb * (1f - sat) + sat) * scale;
        m[0][4] = offset * 255f;
        m[1][4] = offset * 255f;
        m[2][4] = offset * 255f;
        m[3][3] = 1f;
        m[4][4] = 1f;
        return new System.Drawing.Imaging.ColorMatrix(m);
    }

    /// <summary>壁纸/选图不可用时的兜底渐变底图。</summary>
    private static string GradientFile(string mode)
    {
        try
        {
            var file = Path.Combine(NativeTools.DataDir, "media-cache", $"bg-gradient-{mode}.png");
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using var bmp = new System.Drawing.Bitmap(64, 64);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var c1 = mode == "mica"
                    ? System.Drawing.Color.FromArgb(236, 239, 243)
                    : System.Drawing.Color.FromArgb(244, 248, 252);
                var c2 = mode == "mica"
                    ? System.Drawing.Color.FromArgb(216, 222, 231)
                    : System.Drawing.Color.FromArgb(224, 233, 243);
                using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(0, 0, 64, 64), c1, c2, 55f);
                g.FillRectangle(brush, 0, 0, 64, 64);
            }
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static string Md5Short(string text)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
