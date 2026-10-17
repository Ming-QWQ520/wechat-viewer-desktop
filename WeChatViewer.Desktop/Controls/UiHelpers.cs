using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WeChatViewer.Desktop.Controls;

/// <summary>Bitmap decoding + small LRU cache for message media (decoded once per image).</summary>
public static class UiHelpers
{
    /// <summary>Deterministic pastel background for avatar fallbacks (hsl(hash,45%,55%)).</summary>
    public static Microsoft.UI.Color AvatarColor(string name)
    {
        long hash = 0;
        foreach (var ch in name ?? "") hash = (hash * 31 + ch) % 360;
        return FromHsl((float)hash, 0.45f, 0.55f);
    }

    public static Microsoft.UI.Color FromHsl(float h, float s, float l)
    {
        h = ((h % 360f) + 360f) % 360f / 360f;
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return Microsoft.UI.ColorHelper.FromArgb(255,
            (byte)Math.Clamp((int)Math.Round(r * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g * 255), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b * 255), 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    public static string AvatarColorHex(string name)
    {
        long hash = 0;
        foreach (var ch in name ?? "") hash = (hash * 31 + ch) % 360;
        return $"hsl({hash}, 45%, 55%)";
    }

    public static string Initial(string name)
    {
        var text = (name ?? "?").Trim();
        if (text.Length == 0) return "?";
        return char.IsSurrogateHigh(text[0]) && text.Length > 1 ? text.Substring(0, 2) : text[..1];
    }

    public static async Task<BitmapImage> BitmapFromBytesAsync(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes).AsRandomAccessStream();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    public static async Task<BitmapImage> BitmapFromFileAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var bitmap = new BitmapImage();
        using var stream = File.OpenRead(path).AsRandomAccessStream();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    // ----- simple LRU cache for decoded message images -----

    private class LruCache
    {
        private readonly ConcurrentDictionary<string, LinkedListNode<(string Key, object Value)>> _map = new();
        private readonly LinkedList<(string Key, object Value)> _order = new();
        private readonly object _lock = new();
        private readonly int _capacity;

        public LruCache(int capacity) => _capacity = capacity;

        public object Get(string key)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            lock (_lock)
            {
                _order.Remove(node);
                _order.AddFirst(node);
            }
            return node.Value.Value;
        }

        public void Set(string key, object value)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    _order.Remove(existing);
                    _map.TryRemove(key, out _);
                }
                var node = new LinkedListNode<(string, object)>((key, value));
                _order.AddFirst(node);
                _map[key] = node;
                while (_order.Count > _capacity)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.TryRemove(last.Value.Key, out _);
                }
            }
        }
    }

    private static readonly LruCache ImageCache = new(400);

    /// <summary>Get or decode a message image (bytes), cached by key.</summary>
    public static async Task<ImageSource> GetMessageImageAsync(string cacheKey, Func<Task<byte[]>> loader)
    {
        if (ImageCache.Get(cacheKey) is ImageSource cached) return cached;
        try
        {
            var bytes = await loader();
            if (bytes == null) return null;
            var bitmap = await BitmapFromBytesAsync(bytes);
            if (bitmap != null) ImageCache.Set(cacheKey, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Get or decode a message image (file), cached by path.</summary>
    public static async Task<ImageSource> GetFileImageAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (ImageCache.Get(path) is ImageSource cached) return cached;
        try
        {
            var bitmap = await BitmapFromFileAsync(path);
            if (bitmap != null) ImageCache.Set(path, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
