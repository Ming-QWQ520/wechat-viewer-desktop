using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZstdSharp;

namespace WeChatViewer.Desktop.Services;

/// <summary>Utility helpers ported from app/wechat.mjs.</summary>
public static partial class TextUtils
{
    private static readonly byte[] ZstdMagic = { 0x28, 0xb5, 0x2f, 0xfd };

    public static bool IsZstd(byte[] buf)
        => buf != null && buf.Length >= 4 && buf[0] == ZstdMagic[0] && buf[1] == ZstdMagic[1]
            && buf[2] == ZstdMagic[2] && buf[3] == ZstdMagic[3];

    /// <summary>Decode message_content: zstd-decompress when needed, then UTF-8.</summary>
    public static string DecodeContentBuffer(object value)
    {
        if (value == null) return "";
        byte[] buf = value switch
        {
            byte[] b => b,
            System.Collections.Generic.IReadOnlyList<byte> list => ToBytes(list),
            string s => Encoding.UTF8.GetBytes(s),
            _ => ToBytes((System.Collections.Generic.IReadOnlyList<byte>)value)
        };
        if (IsZstd(buf))
        {
            try
            {
                long size = (long)Decompressor.GetDecompressedSize(buf.AsSpan());
                if (size > 0 && size <= 512L * 1024 * 1024)
                {
                    var decompressor = new Decompressor();
                    var dst = new byte[size];
                    int written = decompressor.Unwrap(buf, dst.AsSpan());
                    decompressor.Dispose();
                    return Encoding.UTF8.GetString(dst, 0, written);
                }
                // Streaming frame without a recorded size: unwrap into pooled memory and copy.
                var fallback = new Decompressor();
                var span = fallback.Unwrap(buf);
                var bytes = span.ToArray();
                fallback.Dispose();
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                try { return Encoding.UTF8.GetString(buf); } catch { return ""; }
            }
        }
        try { return Encoding.UTF8.GetString(buf); } catch { return ""; }
    }

    private static byte[] ToBytes(System.Collections.Generic.IReadOnlyList<byte> list)
    {
        var result = new byte[list.Count];
        for (int i = 0; i < list.Count; i++) result[i] = list[i];
        return result;
    }

    public static string Md5Hex(string text)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static string Md5Hex(byte[] data)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public static string GetAttr(string xml, string name)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        var m = Regex.Match(xml, $"{Regex.Escape(name)}\\s*=\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string GetTag(string xml, string tag)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        var m = Regex.Match(xml, $"<{tag}(?:\\s[^>]*)?>([\\s\\S]*?)</{tag}>");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string GetCdata(string xml, string tag)
    {
        var inner = GetTag(xml, tag);
        if (inner == null) return null;
        var m = Regex.Match(inner, "<!\\[CDATA\\[([\\s\\S]*?)\\]\\]>");
        return (m.Success ? m.Groups[1].Value : inner).Trim();
    }

    public static string HtmlUnescape(string text)
    {
        if (text == null) return null;
        text = text
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'");
        text = Regex.Replace(text, "&#x([0-9a-fA-F]+);",
            m => char.ConvertFromUtf32(Convert.ToInt32(m.Groups[1].Value, 16)));
        text = Regex.Replace(text, "&#(\\d+);",
            m => char.ConvertFromUtf32((int)uint.Parse(m.Groups[1].Value)));
        return text.Replace("&amp;", "&");
    }

    /// <summary>Trim WeChat text noise but keep intentional line breaks.</summary>
    public static string NormalizeText(string text)
    {
        if (text == null) return "";
        text = text
            .Replace("\r\n", "\n").Replace("\r", "\n")
            .Replace("\u200b", "").Replace("\u200c", "").Replace("\u200d", "").Replace("\ufeff", "");
        text = SpaceRunRegex().Replace(text, " ");
        text = AroundNewlineRegex().Replace(text, "\n");
        text = BlankLinesRegex().Replace(text, "\n");
        return text.Trim(' ', '\n', '\t', '\u00a0', '\u2005', '\u3000');
    }

    [GeneratedRegex("[ \\t\\u00a0\\u2005\\u3000]+")]
    private static partial Regex SpaceRunRegex();

    [GeneratedRegex(" *\\n *")]
    private static partial Regex AroundNewlineRegex();

    [GeneratedRegex("\\n{2,}")]
    private static partial Regex BlankLinesRegex();

    public static string StripXml(string text)
    {
        if (text == null) return "";
        return Regex.Replace(Regex.Replace(text, "<[^>]+>", ""), "\\s+", " ").Trim();
    }

    public static IEnumerable<(string Type, string Value)> SplitUrls(string text)
    {
        var parts = new List<(string, string)>();
        var source = text ?? "";
        int last = 0;
        foreach (Match match in UrlRegex().Matches(source))
        {
            if (match.Index > last) parts.Add(("text", source.Substring(last, match.Index - last)));
            parts.Add(("url", match.Value));
            last = match.Index + match.Value.Length;
        }
        if (last < source.Length) parts.Add(("text", source.Substring(last)));
        return parts;
    }

    [GeneratedRegex("https?://[^\\s<>\"'\\u3000]+")]
    private static partial Regex UrlRegex();

    /// <summary>32-hex local media base name embedded in packed_info_data protobuf.</summary>
    public static string LocalMediaName(byte[] packed)
    {
        if (packed == null || packed.Length == 0) return null;
        var sb = new StringBuilder(packed.Length);
        foreach (var b in packed) sb.Append((char)b); // latin1 view
        var m = Regex.Match(sb.ToString(), "[0-9a-fA-F]{32}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private const long BaseTypeMod = 4294967296L;

    public static (long Base, long Sub) TypeParts(long localType)
    {
        long sub = (long)Math.Floor((decimal)localType / BaseTypeMod);
        long @base = localType - sub * BaseTypeMod;
        return (@base, sub);
    }

    public static string FormatBytes(long bytes)
    {
        double n = bytes;
        if (n < 1024) return $"{bytes} B";
        if (n < 1024 * 1024) return $"{(n / 1024).ToString("F1")} KB";
        if (n < 1024L * 1024 * 1024) return $"{(n / 1024 / 1024).ToString("F1")} MB";
        return $"{(n / 1024 / 1024 / 1024).ToString("F2")} GB";
    }

    public static string Pad2(long n) => n.ToString("00");

    public static string FormatDateTime(long unixSeconds)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return $"{d.Year}-{Pad2(d.Month)}-{Pad2(d.Day)} {Pad2(d.Hour)}:{Pad2(d.Minute)}:{Pad2(d.Second)}";
    }

    public static string FormatDay(long unixSeconds)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return $"{d.Year}-{Pad2(d.Month)}-{Pad2(d.Day)}";
    }

    public static string FormatClock(long unixSeconds)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return $"{Pad2(d.Hour)}:{Pad2(d.Minute)}:{Pad2(d.Second)}";
    }

    public static string SafeFileName(string name)
    {
        var cleaned = Regex.Replace(name ?? "chat", "[\\\\/:*?\"<>|\\s]+", "_");
        return cleaned.Length > 60 ? cleaned.Substring(0, 60) : cleaned;
    }
}
