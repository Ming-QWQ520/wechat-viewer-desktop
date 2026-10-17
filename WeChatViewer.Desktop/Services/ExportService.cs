using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WeChatViewer.Desktop.Models;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// HTML / TXT / CSV export, ported from app/export.mjs. The HTML export is a
/// self-contained single file with WeChat bubble styling, embedded media and
/// the same inline search script as the original.
/// </summary>
public class ExportService
{
    private const long MaxEmbedItem = 4 * 1024 * 1024;
    private const long DefaultEmbedBudget = 120 * 1024 * 1024;
    private const long DefaultStickerBudget = 150 * 1024 * 1024;

    private readonly WeChatData _data;
    private readonly StickerThumbs _thumbs = new();
    private readonly string _cacheDir;

    public ExportService(WeChatData data, string cacheDir)
    {
        _data = data;
        _cacheDir = cacheDir;
    }

    public event Action<long, long> Progress; // (done, total)

    // ---------- helpers ----------

    private static string EscapeHtml(string text) => (text ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string LinksToHtml(string text)
    {
        var sb = new StringBuilder();
        foreach (var (type, value) in TextUtils.SplitUrls(text))
        {
            if (type == "url")
            {
                sb.Append($"<a href=\"{EscapeHtml(value)}\" target=\"_blank\" rel=\"noreferrer\">{EscapeHtml(value)}</a>");
            }
            else
            {
                sb.Append(EscapeHtml(value));
            }
        }
        return sb.ToString();
    }

    private static string AvatarColor(string name)
    {
        long hash = 0;
        foreach (var ch in name ?? "") hash = (hash * 31 + ch) % 360;
        return $"hsl({hash}, 45%, 55%)";
    }

    private static string Initial(string name)
    {
        var text = (name ?? "?").Trim();
        return text.Length > 0 ? text.Substring(0, char.IsHighSurrogate(text[0]) && text.Length > 1 ? 2 : 1) : "?";
    }

    // ---------- media resolution ----------

    private class MediaResult
    {
        public string ImageUri, ImageType, VoiceUri, VoicePath, FilePath, VideoPath, ThumbUri, StickerPath, StickerClass, VoiceError;
        public long? VoiceDuration;
    }

    private async Task<MediaResult> ResolveMediaAsync(string username, Message message, Budget budget)
    {
        var output = new MediaResult();
        if (message.Kind == "image" && message.Image?.LocalName != null)
        {
            var decoded = await _data.ReadBestImageAsync(username, message.Image.LocalName);
            if (decoded?.Buffer != null && decoded.Buffer.Length <= MaxEmbedItem && budget.Remaining >= decoded.Buffer.Length)
            {
                budget.Remaining -= decoded.Buffer.Length;
                output.ImageUri = ToDataUri(decoded.Buffer, decoded.ContentType);
                output.ImageType = decoded.ContentType;
            }
        }
        else if (message.Kind == "voice")
        {
            try
            {
                var silk = _data.FindVoiceData(username, message.Voice.LocalId, message.Voice.CreateTime);
                if (silk != null)
                {
                    var hash = Md5Safe(username);
                    var cachePath = Path.Combine(_cacheDir, "voice", hash, $"{message.Voice.LocalId}_{message.Voice.CreateTime}.wav");
                    var wav = await SilkVoiceDecoder.SilkToWavAsync(silk, cachePath);
                    if (wav.Length <= MaxEmbedItem && budget.Remaining >= wav.Length)
                    {
                        budget.Remaining -= wav.Length;
                        output.VoiceUri = ToDataUri(wav, "audio/wav");
                    }
                    else
                    {
                        output.VoicePath = cachePath;
                    }
                    output.VoiceDuration = message.Voice.DurationMs;
                }
            }
            catch (Exception error)
            {
                output.VoiceError = error.Message;
            }
        }
        else if (message.Kind == "video" && message.Video?.LocalName != null)
        {
            var (thumb, video) = _data.FindVideoAssets(message.Video.LocalName);
            if (thumb != null)
            {
                try
                {
                    var buf = await File.ReadAllBytesAsync(thumb);
                    if (buf.Length <= MaxEmbedItem && budget.Remaining >= buf.Length)
                    {
                        budget.Remaining -= buf.Length;
                        output.ThumbUri = ToDataUri(buf, "image/jpeg");
                    }
                }
                catch { }
            }
            output.VideoPath = video;
        }
        else if (message.Kind == "file" && message.File?.Name != null)
        {
            output.FilePath = _data.FindFile(message.File.Name);
        }
        else if (message.Kind == "sticker")
        {
            var sticker = _data.ReadSticker(message.Sticker?.Md5 ?? message.Sticker?.ThumbMd5);
            if (sticker != null) output.StickerPath = sticker.File;
        }
        return output;
    }

    private static string Md5Safe(string username)
    {
        uint h = 0;
        foreach (var ch in username) h = (uint)(h * 33 + ch);
        return h.ToString("x");
    }

    private static string ToDataUri(byte[] buffer, string contentType)
        => $"data:{contentType};base64,{Convert.ToBase64String(buffer)}";

    /// <summary>Cheap resolution for TXT/CSV: record on-disk locations only, no decoding.</summary>
    private string ResolveMediaPaths(Message message)
    {
        return message.Kind switch
        {
            "image" => _data.FindImageFile(ResolveMediaPathsChat, message.Image?.LocalName) ?? "",
            "sticker" => _data.ReadSticker(message.Sticker?.Md5 ?? message.Sticker?.ThumbMd5)?.File ?? "",
            "video" => _data.FindVideoAssets(message.Video?.LocalName).Video ?? "",
            "file" => _data.FindFile(message.File?.Name) ?? "",
            _ => "",
        };
    }

    private string ResolveMediaPathsChat; // chat username context for ResolveMediaPaths

    // ---------- plain text ----------

    private static string MessagePlainText(Message message)
    {
        switch (message.Kind)
        {
            case "text": return message.Text;
            case "image": return "[图片]";
            case "voice": return $"[语音 {(message.Voice.DurationMs / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}″]";
            case "video": return $"[视频 {message.Video.DurationSec}″]";
            case "file": return $"[文件 {message.File.Name}]";
            case "link": return $"[链接] {message.Link.Title}{(message.Link.Url is { Length: > 0 } ? $" {message.Link.Url}" : "")}";
            case "sticker": return "[表情]";
            case "quote": return $"[引用{(message.Quote?.Sender is { Length: > 0 } s ? $" {s}" : "")}] {message.Quote?.Text ?? ""}{(message.Text is { Length: > 0 } t ? $"\n{t}" : "")}";
            case "chatRecord": return $"[聊天记录] {message.Link?.Title ?? message.Text ?? ""}";
            case "pat": return string.IsNullOrEmpty(message.Text) ? "拍了拍" : message.Text;
            case "location": return $"[位置] {message.Location?.Label ?? message.Location?.PoiName ?? ""}";
            case "card": return $"[名片] {message.Card?.Nickname ?? message.Card?.Username ?? ""}";
            case "voip": return $"[通话] {message.Text}";
            case "system": return $"[系统] {message.System}";
            default: return string.IsNullOrEmpty(message.Text) ? "[消息]" : message.Text;
        }
    }

    // ---------- image dimensions ----------

    private static (int Width, int Height)? ImageDimensions(byte[] buffer)
    {
        if (buffer == null) return null;
        if (buffer.Length >= 24 && buffer[0] == 0x89 && buffer[1] == 0x50)
        {
            int width = (buffer[16] << 24) | (buffer[17] << 16) | (buffer[18] << 8) | buffer[19];
            int height = (buffer[20] << 24) | (buffer[21] << 16) | (buffer[22] << 8) | buffer[23];
            return (width, height);
        }
        if (buffer.Length >= 10 && buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46)
        {
            int width = buffer[6] | (buffer[7] << 8);
            int height = buffer[8] | (buffer[9] << 8);
            return (width, height);
        }
        if (buffer.Length >= 4 && buffer[0] == 0xff && buffer[1] == 0xd8)
        {
            int offset = 2;
            while (offset + 9 < buffer.Length)
            {
                if (buffer[offset] != 0xff)
                {
                    offset++;
                    continue;
                }
                var marker = buffer[offset + 1];
                if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
                {
                    int height = (buffer[offset + 5] << 8) | buffer[offset + 6];
                    int width = (buffer[offset + 7] << 8) | buffer[offset + 8];
                    return (width, height);
                }
                offset += 2 + ((buffer[offset + 2] << 8) | buffer[offset + 3]);
            }
        }
        return null;
    }

    private static (int Width, int Height) ScaledDimensions(byte[] buffer, int maxSize)
    {
        var dims = ImageDimensions(buffer);
        if (dims == null || dims.Value.Width == 0 || dims.Value.Height == 0) return (100, 100);
        var scale = Math.Min(1.0, maxSize / (double)Math.Max(dims.Value.Width, dims.Value.Height));
        return (Math.Max(1, (int)Math.Round(dims.Value.Width * scale)),
                Math.Max(1, (int)Math.Round(dims.Value.Height * scale)));
    }

    // ---------- HTML export ----------

    private class Budget
    {
        public long Remaining;
        public long StickerRemaining;
    }

    private class StickerStyle
    {
        public string ClassName, Uri;
        public int Width, Height;
    }

    private string BubbleHtml(Message message, MediaResult media, bool group, string avatarClass, HashSet<string> quoteTargets)
    {
        var serverId = Regex.IsMatch(message.ServerId ?? "", @"^\d+$") ? message.ServerId : "";
        var rowId = serverId.Length > 0 ? $" id=\"m-{serverId}\"" : "";
        if (message.Kind == "system")
        {
            return $"<div class=\"wx-system\"{rowId} data-kind=\"system\">{LinksToHtml(message.System ?? "")}<div class=\"wx-time wx-time-center\">{TextUtils.FormatDateTime(message.Time)}</div></div>";
        }
        if (message.Kind == "pat")
        {
            return $"<div class=\"wx-system wx-pat\"{rowId} data-kind=\"pat\">{EscapeHtml(string.IsNullOrEmpty(message.Text) ? "拍了拍" : message.Text)}<div class=\"wx-time wx-time-center\">{TextUtils.FormatDateTime(message.Time)}</div></div>";
        }
        var cls = message.IsSelf ? "out" : "in";
        var nameHtml = !message.IsSelf && group ? $"<div class=\"wx-name\">{EscapeHtml(message.SenderName)}</div>" : "";
        var avatarInner = avatarClass == null ? EscapeHtml(Initial(message.SenderName)) : "";
        var avatarStyle = avatarClass == null ? $" style=\"background:{AvatarColor(message.SenderName)}\"" : "";
        var avatarExtraClass = avatarClass == null ? "" : $" {avatarClass}";

        string inner;
        switch (message.Kind)
        {
            case "text":
                inner = $"<div class=\"wx-text\">{LinksToHtml(message.Text)}</div>";
                break;
            case "image":
                inner = media.ImageUri != null
                    ? $"<a class=\"wx-img\" href=\"{media.ImageUri}\" target=\"_blank\"><img src=\"{media.ImageUri}\" alt=\"图片\"></a>"
                    : "<div class=\"wx-text\">[图片]</div>";
                break;
            case "voice":
            {
                var seconds = Math.Max(1, (long)Math.Round(message.Voice.DurationMs / 1000.0));
                var width = Math.Min(240, 64 + seconds * 6);
                inner = media.VoiceUri != null
                    ? $"<div class=\"wx-voice\" style=\"width:{width}px\"><button class=\"wx-voice-btn\" type=\"button\" aria-label=\"播放语音\">▶</button><span class=\"wx-voice-wave\" aria-hidden=\"true\"><i></i><i></i><i></i></span><span class=\"wx-voice-dur\">{seconds}″</span><audio preload=\"none\" src=\"{media.VoiceUri}\"></audio></div>"
                    : $"<div class=\"wx-text\">[语音 {seconds}″]{(media.VoicePath != null ? $" <span class=\"wx-sub\">{EscapeHtml(media.VoicePath)}</span>" : "")}</div>";
                break;
            }
            case "video":
            {
                var dur = message.Video.DurationSec;
                var thumb = media.ThumbUri != null
                    ? $"<img src=\"{media.ThumbUri}\" alt=\"视频\">"
                    : "<div class=\"wx-video-placeholder\">▶</div>";
                var src = media.VideoPath != null ? $"<div class=\"wx-sub\">视频文件: {EscapeHtml(media.VideoPath)}</div>" : "";
                inner = $"<div class=\"wx-video\"><a class=\"wx-video-link\" href=\"{(media.VideoPath != null ? EscapeHtml(media.VideoPath) : "#")}\" target=\"_blank\">{thumb}<span class=\"wx-video-dur\">{dur}″</span></a>{src}</div>";
                break;
            }
            case "file":
                inner = $"<div class=\"wx-file\"><div class=\"wx-file-icon\">📄</div><div class=\"wx-file-body\"><div class=\"wx-file-name\">{EscapeHtml(message.File.Name)}</div><div class=\"wx-sub\">{TextUtils.FormatBytes(message.File.Size)}{(media.FilePath != null ? $" · {EscapeHtml(media.FilePath)}" : "")}</div></div></div>";
                break;
            case "link":
                inner = $"<div class=\"wx-link\"><div class=\"wx-link-body\"><div class=\"wx-link-title\">{EscapeHtml(message.Link.Title)}</div><div class=\"wx-link-desc\">{EscapeHtml(message.Link.Description ?? "")}</div><div class=\"wx-link-url\">{EscapeHtml(message.Link.Url ?? "")}</div></div></div>";
                break;
            case "sticker":
                inner = media.StickerClass != null
                    ? $"<span class=\"wx-sticker-img {media.StickerClass}\"></span>"
                    : $"<div class=\"wx-text\">[表情]{(media.StickerPath != null ? $" <span class=\"wx-sub\">{EscapeHtml(media.StickerPath)}</span>" : "")}</div>";
                break;
            case "quote":
                inner = $"<div class=\"wx-text\">{LinksToHtml(message.Text ?? "")}</div>";
                break;
            case "chatRecord":
                inner = $"<div class=\"wx-file\"><div class=\"wx-file-icon\">💬</div><div class=\"wx-file-body\"><div class=\"wx-file-name\">{EscapeHtml(message.Link?.Title ?? message.Text ?? "聊天记录")}</div><div class=\"wx-sub\">聊天记录</div></div></div>";
                break;
            case "location":
                inner = $"<div class=\"wx-text\">[位置] {EscapeHtml(message.Location?.Label ?? "")}{(message.Location?.PoiName is { Length: > 0 } p ? $" · {EscapeHtml(p)}" : "")}</div>";
                break;
            case "card":
                inner = $"<div class=\"wx-file\"><div class=\"wx-file-icon\">👤</div><div class=\"wx-file-body\"><div class=\"wx-file-name\">{EscapeHtml(message.Card?.Nickname ?? message.Card?.Username ?? "名片")}</div><div class=\"wx-sub\">{EscapeHtml(message.Card?.Username ?? "")}</div></div></div>";
                break;
            case "voip":
                inner = $"<div class=\"wx-text\">[通话] {EscapeHtml(message.Text ?? "")}</div>";
                break;
            default:
                inner = $"<div class=\"wx-text\">{EscapeHtml(string.IsNullOrEmpty(message.Text) ? "[消息]" : message.Text)}</div>";
                break;
        }

        string quoteHtml = "";
        if (message.Kind == "quote" && message.Quote != null)
        {
            var sid = message.Quote.SvrId ?? "";
            var linkable = sid.Length > 0 && quoteTargets != null && quoteTargets.Contains(sid);
            var tag = linkable ? "a" : "div";
            var attrs = linkable ? $" href=\"#m-{sid}\"" : "";
            var name = message.Quote.Sender is { Length: > 0 }
                ? $"<span class=\"wx-quote-name\">{EscapeHtml(message.Quote.Sender)}：</span>"
                : "";
            quoteHtml = $"<{tag} class=\"wx-quote{(linkable ? " wx-quote-link" : "")}\"{attrs} title=\"{EscapeHtml(message.Quote.Text ?? "")}\">{name}{EscapeHtml(message.Quote.Text ?? "")}</{tag}>";
        }

        return $"<div class=\"wx-row {cls}\"{rowId} data-kind=\"{EscapeHtml(message.Kind)}\">\n" +
               $"  <div class=\"wx-avatar{avatarExtraClass}\"{avatarStyle}>{avatarInner}</div>\n" +
               $"  <div class=\"wx-bubble-wrap\">{nameHtml}<div class=\"wx-bubble {message.Kind}\">{inner}</div>{quoteHtml}<div class=\"wx-time\">{TextUtils.FormatDateTime(message.Time)}</div></div>\n" +
               $"</div>";
    }

    private const string HtmlStyle = """
:root { color-scheme: light; }
* { box-sizing: border-box; }
body { margin: 0; background: #ededed; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Microsoft YaHei", sans-serif; color: #191919; }
.wx-shell { max-width: 760px; margin: 0 auto; padding: 0 0 40px; }
.wx-header { position: sticky; top: 0; z-index: 5; background: #f7f7f7; border-bottom: 1px solid #e0e0e0; padding: 14px 18px; }
.wx-head { display: flex; align-items: center; gap: 10px; }
.wx-header-avatar { width: 44px; height: 44px; border-radius: 6px; object-fit: cover; background: #ddd; flex: 0 0 44px; }
.wx-header h1 { margin: 0; font-size: 18px; font-weight: 600; }
.wx-header .wx-meta { margin-top: 4px; color: #888; font-size: 12px; }
.wx-search { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-top: 10px; }
.wx-search input { flex: 1; min-width: 180px; border: 1px solid #ccc; border-radius: 6px; padding: 6px 10px; font-size: 13px; }
.wx-search select { border: 1px solid #ccc; border-radius: 6px; padding: 6px 8px; font-size: 13px; background: #fff; }
.wx-search button { border: 1px solid #ccc; background: #fff; border-radius: 6px; padding: 6px 10px; font-size: 12px; cursor: pointer; }
.wx-search-count { color: #888; font-size: 12px; }
.wx-row.hidden { display: none; }
.wx-chat { padding: 18px 16px; }
.wx-day { text-align: center; color: #9a9a9a; font-size: 12px; margin: 18px 0 12px; }
.wx-row { display: flex; align-items: flex-start; margin: 10px 0; gap: 8px; }
.wx-row.in { flex-direction: row; }
.wx-row.out { flex-direction: row-reverse; }
.wx-avatar { width: 36px; height: 36px; border-radius: 6px; color: #fff; display: flex; align-items: center; justify-content: center; font-size: 15px; font-weight: 600; flex: 0 0 36px; position: relative; overflow: hidden; background-size: cover; background-position: center; }
.wx-avatar img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
.wx-bubble-wrap { max-width: 68%; display: flex; flex-direction: column; align-items: flex-start; }
.wx-row.out .wx-bubble-wrap { align-items: flex-end; }
.wx-time { margin-top: 4px; color: #a8a8a8; font-size: 11px; line-height: 1.3; }
.wx-time-center { text-align: center; }
.wx-row.out .wx-time { text-align: right; }
.wx-name { font-size: 12px; color: #8a8a8a; margin: 0 0 4px 2px; }
.wx-bubble { padding: 9px 12px; border-radius: 6px; position: relative; word-break: break-word; white-space: pre-wrap; font-size: 14px; line-height: 1.5; }
.wx-row.in .wx-bubble { background: #fff; }
.wx-row.out .wx-bubble { background: #95ec69; }
.wx-text a { color: #576b95; }
.wx-bubble.image { padding: 3px; background: transparent !important; }
.wx-bubble.sticker { padding: 0; background: transparent !important; }
.wx-sticker-img { display: block; background-size: contain; background-repeat: no-repeat; background-position: center; }
.wx-img img { display: block; max-width: 240px; max-height: 320px; border-radius: 4px; }
.wx-voice { display: flex; align-items: center; gap: 9px; min-height: 26px; }
.wx-row.out .wx-voice { flex-direction: row-reverse; }
.wx-voice audio { display: none; }
.wx-voice-btn { width: 26px; height: 26px; border-radius: 50%; border: none; background: rgba(0,0,0,.12); color: inherit; cursor: pointer; font-size: 11px; line-height: 26px; padding: 0; flex: 0 0 26px; }
.wx-voice-wave { display: inline-flex; align-items: flex-end; gap: 2px; height: 15px; }
.wx-voice-wave i { display: block; width: 3px; height: 8px; border-radius: 2px; background: currentColor; opacity: .75; }
.wx-voice-wave i:nth-child(2) { height: 14px; }
.wx-voice-wave i:nth-child(3) { height: 11px; }
.wx-voice.playing .wx-voice-btn { background: rgba(7,193,96,.85); color: #fff; }
.wx-voice.playing .wx-voice-wave i { animation: wxVoicePulse .7s ease-in-out infinite alternate; }
.wx-voice.playing .wx-voice-wave i:nth-child(2) { animation-delay: .15s; }
.wx-voice.playing .wx-voice-wave i:nth-child(3) { animation-delay: .3s; }
.wx-voice-dur { font-size: 13px; opacity: .8; }
@keyframes wxVoicePulse { from { transform: scaleY(.45); } to { transform: scaleY(1.15); } }
.wx-quote { display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; margin-top: 6px; background: #d9d9d9; color: #555; border-radius: 6px; padding: 7px 9px; font-size: 12px; line-height: 1.5; white-space: pre-wrap; word-break: break-word; }
.wx-row.out .wx-quote { background: rgba(0,0,0,.28); color: #f2f2f2; }
.wx-quote-link { text-decoration: none; cursor: pointer; }
.wx-quote-link:hover { filter: brightness(.97); }
.wx-quote-name { font-weight: 600; margin-right: 4px; }
.wx-row { scroll-margin-top: 150px; }
.wx-row:target .wx-bubble, .wx-system:target { box-shadow: 0 0 0 2px #07c160; }
.wx-video img { display: block; max-width: 260px; border-radius: 4px; }
.wx-video-placeholder { width: 220px; height: 140px; background: #333; color: #fff; display: flex; align-items: center; justify-content: center; border-radius: 4px; font-size: 28px; }
.wx-video-link { position: relative; display: inline-block; }
.wx-video-dur { position: absolute; right: 6px; bottom: 6px; background: rgba(0,0,0,.6); color: #fff; font-size: 12px; padding: 1px 6px; border-radius: 8px; }
.wx-file { display: flex; gap: 10px; align-items: center; min-width: 180px; }
.wx-file-icon { font-size: 26px; }
.wx-file-name { font-size: 14px; font-weight: 500; }
.wx-sub { color: #9a9a9a; font-size: 12px; margin-top: 2px; }
.wx-link { min-width: 220px; max-width: 320px; }
.wx-link-title { font-weight: 600; margin-bottom: 4px; }
.wx-link-desc { color: #888; font-size: 12px; }
.wx-link-url { color: #9a9a9a; font-size: 11px; margin-top: 4px; word-break: break-all; }
.wx-system { text-align: center; color: #9a9a9a; font-size: 12px; margin: 12px auto; max-width: 80%; }
.wx-pat { color: #9a9a9a; }
.wx-footer { text-align: center; color: #b0b0b0; font-size: 12px; margin-top: 30px; }
""";

    private const string ExportScript = """
(function () {
  var rows = Array.prototype.slice.call(document.querySelectorAll('[data-kind]'));
  var input = document.getElementById('wx-search-input');
  var kind = document.getElementById('wx-search-kind');
  var count = document.getElementById('wx-search-count');
  var clear = document.getElementById('wx-search-clear');
  var cache = new Map();
  function textOf(el) {
    var text = cache.get(el);
    if (text === undefined) {
      var bubble = el.querySelector('.wx-bubble');
      var quote = el.querySelector('.wx-quote');
      text = ((bubble ? bubble.textContent : '') + ' ' + (quote ? quote.textContent : (el.textContent || ''))).toLowerCase();
      cache.set(el, text);
    }
    return text;
  }
  function apply() {
    var q = input.value.trim().toLowerCase();
    var k = kind.value;
    var shown = 0;
    for (var i = 0; i < rows.length; i++) {
      var el = rows[i];
      var okKind = !k || el.getAttribute('data-kind') === k;
      var okText = !q || textOf(el).indexOf(q) !== -1;
      var show = okKind && okText;
      el.classList.toggle('hidden', !show);
      if (show) shown++;
    }
    count.textContent = (q || k) ? ('找到 ' + shown + ' 条') : ('共 ' + rows.length + ' 条');
  }
  var timer = null;
  input.addEventListener('input', function () { clearTimeout(timer); timer = setTimeout(apply, 180); });
  kind.addEventListener('change', apply);
  clear.addEventListener('click', function () { input.value = ''; kind.value = ''; apply(); });
  count.textContent = '共 ' + rows.length + ' 条';

  var current = null;
  document.querySelectorAll('.wx-voice').forEach(function (voice) {
    var audio = voice.querySelector('audio');
    var button = voice.querySelector('.wx-voice-btn');
    if (!audio || !button) return;
    button.addEventListener('click', function () {
      if (current && current !== audio) {
        current.pause();
        document.querySelectorAll('.wx-voice.playing').forEach(function (v) { v.classList.remove('playing'); });
      }
      if (audio.paused) {
        audio.play();
        voice.classList.add('playing');
        current = audio;
      } else {
        audio.pause();
        voice.classList.remove('playing');
      }
    });
    audio.addEventListener('ended', function () { voice.classList.remove('playing'); });
  });
})();
""";

    public string ExportChatFilename(string username, string format)
    {
        var name = _data.ContactInfo(username).DisplayName;
        var ext = format switch { "html" => "html", "csv" => "csv", _ => "txt" };
        return $"chat-{TextUtils.SafeFileName(name)}-{TextUtils.FormatDay(DateTimeOffset.Now.ToUnixTimeSeconds())}.{ext}";
    }

    private static string HtmlHeader(string title, string name, int count, string exportTime, string avatarUri, string alias) =>
        $"""
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{title}</title><style>{HtmlStyle}</style></head>
<body><div class="wx-shell">
<div class="wx-header"><div class="wx-head">{(avatarUri != null ? $"<img class=\"wx-header-avatar\" src=\"{avatarUri}\" alt=\"\">" : "")}<div><h1>{EscapeHtml(name)}</h1><div class="wx-meta">{(alias is { Length: > 0 } ? $"微信号：{EscapeHtml(alias)} · " : "")}共 {count} 条消息 · 导出于 {exportTime}</div></div></div>
<div class="wx-search">
  <input id="wx-search-input" placeholder="搜索聊天记录" autocomplete="off">
  <select id="wx-search-kind">
    <option value="">全部类型</option>
    <option value="text">文字</option>
    <option value="image">图片</option>
    <option value="voice">语音</option>
    <option value="video">视频</option>
    <option value="file">文件</option>
    <option value="sticker">表情</option>
    <option value="link">链接</option>
    <option value="quote">引用</option>
    <option value="chatRecord">聊天记录</option>
    <option value="pat">拍一拍</option>
    <option value="system">系统</option>
  </select>
  <button id="wx-search-clear" type="button">清除</button>
  <span class="wx-search-count" id="wx-search-count"></span>
</div></div>
<div class="wx-chat">
""";

    /// <summary>Stream a WeChat-style HTML export to the given stream.</summary>
    public async Task ExportChatHtmlAsync(string username, Stream output, long? embedBudget = null, long? stickerBudget = null, CancellationToken cancellationToken = default)
    {
        var info = _data.ContactInfo(username);
        var name = info.DisplayName;
        var budget = new Budget
        {
            Remaining = embedBudget ?? DefaultEmbedBudget,
            StickerRemaining = stickerBudget ?? DefaultStickerBudget,
        };
        var messages = new List<Message>();
        _data.AllMessages(username, page => messages.AddRange(page));
        var exportTime = TextUtils.FormatDateTime(DateTimeOffset.Now.ToUnixTimeSeconds());
        var title = $"聊天记录 - {EscapeHtml(name)}";

        // Embed each unique sticker once as a CSS class so repeated stickers do
        // not multiply the HTML size.
        var stickerKeys = new SortedSet<string>();
        foreach (var message in messages)
        {
            if (message.Kind != "sticker") continue;
            var key = (message.Sticker?.Md5 ?? message.Sticker?.ThumbMd5 ?? "").ToLowerInvariant();
            if (Regex.IsMatch(key, "^[0-9a-f]{32}$")) stickerKeys.Add(key);
        }
        var stickerStyles = new Dictionary<string, StickerStyle>();
        foreach (var key in stickerKeys)
        {
            var sticker = _data.ReadSticker(key);
            if (sticker == null) continue;
            var buffer = sticker.Buffer;
            var contentType = sticker.ContentType;
            var thumb = _data.EnsureStickerThumbnail(key, _thumbs);
            if (thumb != null)
            {
                try
                {
                    buffer = await File.ReadAllBytesAsync(thumb, cancellationToken);
                    contentType = "image/png";
                }
                catch
                {
                    // keep the full-size sticker
                }
            }
            if (buffer.Length > MaxEmbedItem || buffer.Length > budget.StickerRemaining) continue;
            budget.StickerRemaining -= buffer.Length;
            var dims = ScaledDimensions(buffer, 120);
            stickerStyles[key] = new StickerStyle
            {
                ClassName = $"wx-st-{key}",
                Uri = ToDataUri(buffer, contentType),
                Width = dims.Width,
                Height = dims.Height,
            };
        }

        // Prefetch avatars (head_image.db, then the contact's qlogo URL) once per sender.
        await _data.ResolveAvatarAsync(username);
        var senders = new HashSet<string>();
        foreach (var message in messages)
        {
            if (message.Kind != "system") senders.Add(message.SenderUsername ?? username);
        }
        var senderList = senders.ToList();
        var avatarTasks = new List<Task>();
        foreach (var sender in senderList) avatarTasks.Add(_data.ResolveAvatarAsync(sender));
        await Task.WhenAll(avatarTasks);
        var chatAvatar = _data.Avatar(username);
        var chatAvatarUri = chatAvatar != null ? ToDataUri(chatAvatar.Buffer, chatAvatar.ContentType) : null;

        var avatarClasses = new Dictionary<string, string>();
        var avatarRules = new List<string>();
        foreach (var sender in senderList)
        {
            var avatar = _data.Avatar(sender);
            if (avatar == null) continue;
            var id = TextUtils.Md5Hex(sender);
            var className = $"wx-av-{id}";
            avatarClasses[sender] = className;
            avatarRules.Add($".{className}{{background-image:url(\"{ToDataUri(avatar.Buffer, avatar.ContentType)}\")}}");
        }

        using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 1 << 16, leaveOpen: true) { AutoFlush = false };
        await writer.WriteAsync(HtmlHeader(title, name, messages.Count, exportTime, chatAvatarUri, info.Alias ?? ""));

        var styleRules = new List<string>();
        foreach (var style in stickerStyles.Values)
        {
            styleRules.Add($".{style.ClassName}{{width:{style.Width}px;height:{style.Height}px;background-image:url(\"{style.Uri}\")}}");
        }
        styleRules.AddRange(avatarRules);
        if (styleRules.Count > 0)
        {
            await writer.WriteAsync($"<style>{string.Join("\n", styleRules)}</style>");
        }

        var quoteTargets = new HashSet<string>(
            messages.Select(m => m.ServerId ?? "").Where(id => Regex.IsMatch(id, @"^\d+$")));
        bool group = username.EndsWith("@chatroom", StringComparison.Ordinal);
        string lastDay = null;
        long done = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = "";
            var day = TextUtils.FormatDay(message.Time);
            if (day != lastDay)
            {
                chunk += $"<div class=\"wx-day\">{day}</div>\n";
                lastDay = day;
            }
            var media = await ResolveMediaAsync(username, message, budget);
            var stickerKey = (message.Sticker?.Md5 ?? message.Sticker?.ThumbMd5 ?? "").ToLowerInvariant();
            media.StickerClass = stickerStyles.TryGetValue(stickerKey, out var st) ? st.ClassName : null;
            var sender = message.SenderUsername ?? username;
            chunk += BubbleHtml(message, media, group, avatarClasses.TryGetValue(sender, out var av) ? av : null, quoteTargets);
            await writer.WriteAsync(chunk);
            done++;
            if (done % 200 == 0 || done == messages.Count) Progress?.Invoke(done, messages.Count);
        }
        await writer.WriteAsync("\n</div>\n<div class=\"wx-footer\">由 WeChatViewer 导出</div>\n</div><script>" + ExportScript + "</script></body></html>");
        await writer.FlushAsync(cancellationToken);
    }

    // ---------- TXT export ----------

    public async Task<string> ExportChatTxtAsync(string username)
    {
        var info = _data.ContactInfo(username);
        var name = info.DisplayName;
        var lines = new List<string>
        {
            $"聊天记录: {name}",
            $"导出时间: {TextUtils.FormatDateTime(DateTimeOffset.Now.ToUnixTimeSeconds())}",
            "",
        };
        int count = 0;
        _data.AllMessages(username, page =>
        {
            foreach (var message in page)
            {
                var time = TextUtils.FormatDateTime(message.Time);
                if (message.Kind == "system")
                {
                    lines.Add($"[{time}] {message.System ?? ""}");
                }
                else
                {
                    var text = MessagePlainText(message);
                    lines.Add($"[{time}] {message.SenderName}: {Regex.Replace(text ?? "", "\r?\n", " ")}");
                }
                count++;
            }
        });
        lines.Add("");
        lines.Add($"共 {count} 条消息");
        return string.Join("\r\n", lines);
    }

    // ---------- CSV export ----------

    private static string CsvEscape(string value)
    {
        var text = Regex.Replace(value ?? "", "\r?\n", " ");
        return Regex.IsMatch(text, "[\",]") ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    public async Task<string> ExportChatCsvAsync(string username)
    {
        ResolveMediaPathsChat = username;
        var info = _data.ContactInfo(username);
        var name = info.DisplayName;
        var rows = new List<string> { "时间,发送者,类型,内容,链接,媒体路径" };
        _data.AllMessages(username, page =>
        {
            foreach (var message in page)
            {
                var mediaPath = ResolveMediaPaths(message);
                string type = message.Kind;
                string content;
                string link = "";
                switch (message.Kind)
                {
                    case "text": content = message.Text; break;
                    case "image": content = "[图片]"; break;
                    case "voice": content = $"[语音 {(message.Voice.DurationMs / 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}s]"; break;
                    case "video": content = $"[视频 {message.Video.DurationSec}s]"; break;
                    case "file": content = $"[文件] {message.File.Name}"; break;
                    case "link": content = message.Link.Title ?? ""; link = message.Link.Url ?? ""; break;
                    case "sticker": content = "[表情]"; break;
                    case "quote": content = $"[引用{(message.Quote?.Sender is { Length: > 0 } s ? $" {s}" : "")}] {message.Quote?.Text ?? ""} {message.Text ?? ""}"; break;
                    case "chatRecord": content = $"[聊天记录] {message.Link?.Title ?? message.Text ?? ""}"; break;
                    case "pat": content = string.IsNullOrEmpty(message.Text) ? "拍了拍" : message.Text; break;
                    case "location": content = $"[位置] {message.Location?.Label ?? ""} {message.Location?.PoiName ?? ""}"; break;
                    case "card": content = $"[名片] {message.Card?.Nickname ?? message.Card?.Username ?? ""}"; break;
                    case "voip": content = $"[通话] {message.Text ?? ""}"; break;
                    case "system": content = message.System ?? ""; type = "system"; break;
                    default: content = message.Text ?? ""; break;
                }
                rows.Add(string.Join(",", new[]
                {
                    CsvEscape(TextUtils.FormatDateTime(message.Time)),
                    CsvEscape(message.SenderName),
                    CsvEscape(type),
                    CsvEscape(content),
                    CsvEscape(link),
                    CsvEscape(mediaPath ?? ""),
                }));
            }
        });
        return "\ufeff" + string.Join("\r\n", rows);
    }
}
