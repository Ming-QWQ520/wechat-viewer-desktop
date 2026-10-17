using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WeChatViewer.Desktop.Services;

/// <summary>外观设置：背景模式 / 自定义背景图 / 模糊 / 压暗 / 面板透明度。</summary>
public class AppearanceSettings
{
    /// <summary>mica | acrylic | image</summary>
    public string Mode { get; set; } = "acrylic";
    public string ImagePath { get; set; } = "";
    /// <summary>0=原图，1-4 模糊级别（降采样比例）</summary>
    public int Blur { get; set; } = 2;
    /// <summary>0-70，背景压暗百分比</summary>
    public int Dim { get; set; } = 30;
    /// <summary>50-100，侧栏/主区面板不透明度百分比（数值越小越通透）</summary>
    public int PanelOpacity { get; set; } = 85;
}

/// <summary>
/// 窗口外观：切换 MicaBackdrop / DesktopAcrylicBackdrop（液态玻璃）/ 自定义全屏背景图；
/// 按面板透明度重写主题画刷；自定义图可用降采样-升采样生成磨砂模糊版本。
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
        s.Blur = Math.Clamp(s.Blur, 0, 4);
        s.Dim = Math.Clamp(s.Dim, 0, 70);
        s.PanelOpacity = Math.Clamp(s.PanelOpacity, 50, 100);
        if (s.Mode == "image" && (string.IsNullOrEmpty(s.ImagePath) || !File.Exists(s.ImagePath))) s.Mode = "acrylic";
        return s;
    }

    // ---------- backdrop & brushes ----------

    private static byte PanelAlpha(AppearanceSettings s) => (byte)(255 * s.PanelOpacity / 100);

    /// <summary>按模式设置 SystemBackdrop 与面板画刷（亮/暗主题字典同步改写）。</summary>
    public static void Apply(AppearanceSettings s, Window window)
    {
        switch (s.Mode)
        {
            case "mica":
                window.SystemBackdrop = new MicaBackdrop();
                break;
            case "acrylic":
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                break;
            case "image":
                window.SystemBackdrop = null; // 背景图层铺满整个窗口
                break;
        }
        ApplyPanelBrushes(s);
    }

    /// <summary>重写 Wx 主题画刷：面板按透明度，气泡保持高可读性（不低于 88%）。</summary>
    private static void ApplyPanelBrushes(AppearanceSettings s)
    {
        var resources = Application.Current.Resources;
        var panelA = PanelAlpha(s);
        // 气泡/引用块保持接近不透明，避免文字压在背景上难以阅读
        var bubbleA = (byte)Math.Max(226, (int)panelA);
        var dim = s.Mode == "image" ? 0.96 : 0.92; // 通透模式下再略降
        var light = (ResourceDictionary)resources.ThemeDictionaries["Light"];
        var dark = (ResourceDictionary)resources.ThemeDictionaries["Dark"];

        SetBrush(light, "WxSidebarBrush", 0xED, 0xED, 0xE9, Scale(panelA, dim));
        SetBrush(light, "WxMainBrush", 0xF7, 0xF7, 0xF5, Scale(panelA, dim));
        SetBrush(light, "WxHeaderBrush", 0xF7, 0xF7, 0xF7, Scale(panelA, dim));
        SetBrush(light, "WxBubbleInBrush", 0xFF, 0xFF, 0xFF, bubbleA);
        SetBrush(light, "WxDaySepBrush", 0xE4, 0xE4, 0xE4, (byte)Math.Max(150, bubbleA - 60));

        SetBrush(dark, "WxSidebarBrush", 0x25, 0x25, 0x25, Scale(panelA, dim));
        SetBrush(dark, "WxMainBrush", 0x20, 0x20, 0x20, Scale(panelA, dim));
        SetBrush(dark, "WxHeaderBrush", 0x28, 0x28, 0x28, Scale(panelA, dim));
        SetBrush(dark, "WxBubbleInBrush", 0x33, 0x33, 0x33, bubbleA);
        SetBrush(dark, "WxDaySepBrush", 0x3A, 0x3A, 0x3A, (byte)Math.Max(150, bubbleA - 60));
    }

    private static byte Scale(byte alpha, double factor)
        => (byte)Math.Clamp((int)Math.Round(alpha * factor), 40, 255);

    private static void SetBrush(ResourceDictionary dict, string key, byte r, byte g, byte b, byte a)
    {
        dict[key] = new SolidColorBrush(new Windows.UI.Color { A = a, R = r, G = g, B = b });
    }

    // ---------- custom background ----------

    /// <summary>生成（并缓存）降采样-升采样的磨砂模糊背景；level 0 返回原图。</summary>
    public static Task<string> BuildBlurredImageAsync(string sourcePath, int level)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath) || level <= 0)
            return Task.FromResult(sourcePath);
        return Task.Run(() =>
        {
            try
            {
                var info = new FileInfo(sourcePath);
                var cacheKey = $"{sourcePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{level}";
                var cacheFile = Path.Combine(NativeTools.DataDir, "media-cache",
                    $"bg-{Md5Short(cacheKey)}.png");
                if (File.Exists(cacheFile)) return cacheFile;
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));

                using var source = new System.Drawing.Bitmap(sourcePath);
                int factor = level switch { 1 => 10, 2 => 20, 3 => 32, _ => 48 };
                int smallW = Math.Max(2, source.Width / factor);
                int smallH = Math.Max(2, source.Height / factor);
                using var small = new System.Drawing.Bitmap(smallW, smallH);
                using (var g = System.Drawing.Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new System.Drawing.Rectangle(0, 0, smallW, smallH));
                }
                using var target = new System.Drawing.Bitmap(source.Width, source.Height);
                using (var g = System.Drawing.Graphics.FromImage(target))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(small, new System.Drawing.Rectangle(0, 0, source.Width, source.Height));
                }
                target.Save(cacheFile, System.Drawing.Imaging.ImageFormat.Png);
                return cacheFile;
            }
            catch
            {
                return sourcePath; // 模糊失败 → 原图
            }
        });
    }

    private static string Md5Short(string text)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
