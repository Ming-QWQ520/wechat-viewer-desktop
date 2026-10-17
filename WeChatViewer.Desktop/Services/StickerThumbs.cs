using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// 120px PNG thumbnails for downloaded stickers, mirroring
/// tools/make-sticker-thumbs.ps1 (System.Drawing, Windows only).
/// </summary>
public class StickerThumbs
{
    /// <summary>Create a scaled PNG thumbnail; returns outPath on success, null on failure.</summary>
    public string CreateThumbnail(string sourceFile, string outPath, int maxSize = 120)
    {
        try
        {
            if (File.Exists(outPath)) return outPath;
            using var source = new Bitmap(sourceFile);
            var scale = Math.Min(1.0, maxSize / (double)Math.Max(source.Width, source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);
                if (ImageAnimator.CanAnimate(source))
                {
                    // Animated GIF: draw the first frame.
                    var dim = new FrameDimension(source.FrameDimensionsList[0]);
                    source.SelectActiveFrame(dim, 0);
                }
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            target.Save(outPath, ImageFormat.Png);
            return outPath;
        }
        catch
        {
            return null;
        }
    }
}
