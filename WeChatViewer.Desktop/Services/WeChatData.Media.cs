using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WeChatViewer.Desktop.Models;

namespace WeChatViewer.Desktop.Services;

public sealed partial class WeChatData
{
    // ---------- media resolution ----------

    private string ChatAttachDir(string chatUsername) => Path.Combine(_msgDir, "attach", TextUtils.Md5Hex(chatUsername));

    private string[] ListSubdirs(string dir)
    {
        if (_subdirCache.TryGetValue(dir, out var cached)) return cached;
        string[] result;
        try
        {
            result = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).ToArray();
        }
        catch
        {
            result = Array.Empty<string>();
        }
        _subdirCache[dir] = result;
        return result;
    }

    private string[] ImageCandidates(string chatUsername, string localName)
    {
        if (string.IsNullOrEmpty(localName)) return Array.Empty<string>();
        var cacheKey = $"{chatUsername}\0{localName}";
        if (_imageCandidateCache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.Time < TimeSpan.FromSeconds(60))
        {
            return cached.Files;
        }
        var basePath = ChatAttachDir(chatUsername);
        string[] variants = { "_h", "", "_W", "_t_W", "_t" };
        var result = new List<string>();
        foreach (var variant in variants)
        {
            foreach (var month in ListSubdirs(basePath))
            {
                foreach (var ext in new[] { ".dat", ".jpg", ".png" })
                {
                    var file = Path.Combine(basePath, month, "Img", $"{localName}{variant}{ext}");
                    if (File.Exists(file)) result.Add(file);
                }
            }
        }
        _imageCandidateCache[cacheKey] = (result.ToArray(), DateTime.UtcNow);
        return result.ToArray();
    }

    public string FindImageFile(string chatUsername, string localName)
        => ImageCandidates(chatUsername, localName).FirstOrDefault();

    /// <summary>Validate a decoded image, rejecting truncated/corrupt cache files.</summary>
    /// Returns null when validation is impossible (missing tool).
    private bool? ValidateImage(string sourceKey, byte[] buffer)
    {
        if (_validationCache.TryGetValue(sourceKey, out var cached)) return cached;
        var type = ImageDecoder.ImageContentType(buffer);
        bool? valid;
        if (type == "image/png")
        {
            valid = ImageDecoder.ValidatePngBuffer(buffer);
        }
        else if (type is "image/jpeg" or "image/gif")
        {
            if (!File.Exists(_imgcheckPath)) return null;
            var temp = Path.Combine(_validationDir, TextUtils.Md5Hex(sourceKey) + ".img");
            try
            {
                Directory.CreateDirectory(_validationDir);
                File.WriteAllBytes(temp, buffer);
                valid = NativeTools.ImageCheck(temp);
            }
            catch
            {
                valid = false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        else
        {
            valid = null;
        }
        if (valid == null) return null;
        _validationCache[sourceKey] = valid.Value;
        ScheduleValidationSave();
        return valid;
    }

    private void ScheduleValidationSave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_validationFile));
            File.WriteAllText(_validationFile,
                System.Text.Json.JsonSerializer.Serialize(_validationCache));
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Prefer a complete _h original, then the delivered mid-size file, then
    /// thumbnails; corrupted files are rejected and fall back to smaller variants.
    /// </summary>
    public DecodedMedia ReadBestImage(string chatUsername, string localName)
    {
        foreach (var file in ImageCandidates(chatUsername, localName))
        {
            byte[] raw;
            try
            {
                raw = File.ReadAllBytes(file);
            }
            catch
            {
                continue;
            }
            byte[] decoded = raw;
            if (file.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                decoded = ImageDecoder.DecodeImageFile(raw, ImageAesKey, ImageXorKey);
                if (decoded == null) continue;
            }
            var clean = ImageDecoder.TrimImageBuffer(decoded);
            if (clean == null || !ImageDecoder.IsDisplayableImage(clean)) continue;
            long size;
            DateTime mtime;
            try
            {
                var fi = new FileInfo(file);
                size = fi.Length;
                mtime = fi.LastWriteTimeUtc;
            }
            catch
            {
                continue;
            }
            var sourceKey = $"{file}|{size}|{mtime.Ticks}";
            var valid = ValidateImage(sourceKey, clean);
            if (valid == false) continue;
            string variant = "";
            var nameNoExt = Path.GetFileNameWithoutExtension(file);
            if (nameNoExt.EndsWith("_h")) variant = "_h";
            else if (nameNoEndsWith(nameNoExt, localName)) variant = "";
            else if (nameNoExt.EndsWith("_W")) variant = "_W";
            else if (nameNoExt.EndsWith("_t_W")) variant = "_t_W";
            else if (nameNoExt.EndsWith("_t")) variant = "_t";
            return new DecodedMedia
            {
                Buffer = clean,
                ContentType = ImageDecoder.ImageContentType(clean),
                Variant = variant,
                Preview = variant.Contains("_t"),
                File = file,
            };
        }
        return null;
    }

    private static bool nameNoEndsWith(string name, string localName)
        => localName != null && name.Equals(localName, StringComparison.OrdinalIgnoreCase);

    public Task<DecodedMedia> ReadBestImageAsync(string chatUsername, string localName)
        => Task.Run(() => ReadBestImage(chatUsername, localName));

    // ---------- video / files ----------

    private (Dictionary<string, string> Thumb, Dictionary<string, string> Video) VideoAssetsIndex()
    {
        if (_videoIndex != null) return _videoIndex.Value;
        var thumb = new Dictionary<string, string>();
        var video = new Dictionary<string, string>();
        var videoDir = Path.Combine(_msgDir, "video");
        foreach (var month in ListSubdirs(videoDir))
        {
            string[] entries;
            try
            {
                entries = Directory.EnumerateFiles(Path.Combine(videoDir, month)).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.EndsWith("_thumb.jpg", StringComparison.Ordinal))
                {
                    var key = name[..^"_thumb.jpg".Length];
                    if (!thumb.ContainsKey(key)) thumb[key] = entry;
                }
                else if (name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    var key = name[..^4];
                    if (!video.ContainsKey(key)) video[key] = entry;
                }
            }
        }
        _videoIndex = (thumb, video);
        return _videoIndex.Value;
    }

    public (string Thumb, string Video) FindVideoAssets(string localName)
    {
        if (string.IsNullOrEmpty(localName)) return (null, null);
        var index = VideoAssetsIndex();
        index.Thumb.TryGetValue(localName, out var thumb);
        index.Video.TryGetValue(localName, out var vid);
        return (thumb, vid);
    }

    private (Dictionary<string, string> Exact, List<(string Name, string File)> All) FileIndexBuild()
    {
        if (_fileIndex != null) return _fileIndex.Value;
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = new List<(string, string)>();
        var fileDir = Path.Combine(_msgDir, "file");
        foreach (var month in ListSubdirs(fileDir))
        {
            var dir = Path.Combine(fileDir, month);
            string[] entries;
            try
            {
                entries = Directory.EnumerateFiles(dir).ToArray();
            }
            catch
            {
                continue;
            }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (!exact.ContainsKey(name)) exact[name] = entry;
                all.Add((name, entry));
            }
        }
        _fileIndex = (exact, all);
        return _fileIndex.Value;
    }

    public string FindFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var index = FileIndexBuild();
        if (index.Exact.TryGetValue(fileName, out var exact)) return exact;
        foreach (var entry in index.All)
        {
            if (entry.Name.Contains(fileName, StringComparison.Ordinal)
                || fileName.Contains(entry.Name, StringComparison.Ordinal))
            {
                return entry.File;
            }
        }
        return null;
    }

    // ---------- voice ----------

    private Dictionary<string, long> MediaDbNameMap(string rel)
    {
        if (!_mediaName2id.TryGetValue(rel, out var map))
        {
            map = new Dictionary<string, long>();
            var db = Db(rel);
            foreach (var row in Query(db, "select rowid as id, user_name from Name2Id"))
            {
                map[row["user_name"] as string] = ToLong(row["id"]);
            }
            _mediaName2id[rel] = map;
        }
        return map;
    }

    public byte[] FindVoiceData(string chatUsername, long localId, long? createTime = null)
    {
        (long Distance, byte[] Data)? best = null;
        foreach (var rel in new[] { "message/media_0.db", "message/media_1.db" })
        {
            if (!File.Exists(Path.Combine(_decDir, rel))) continue;
            var map = MediaDbNameMap(rel);
            if (!map.TryGetValue(chatUsername, out var chatNameId)) continue;
            var db = Db(rel);
            var rows = Query(db,
                "select voice_data, create_time from VoiceInfo where chat_name_id = ? and local_id = ? order by create_time desc",
                chatNameId, localId);
            foreach (var row in rows)
            {
                if (row["voice_data"] is not byte[] data || data.Length == 0) continue;
                long distance = createTime == null ? 0 : Math.Abs(ToLong(row["create_time"]) - createTime.Value);
                if (best == null || distance < best.Value.Distance) best = (distance, data);
            }
        }
        return best?.Data;
    }

    /// <summary>Decode a voice message to WAV (cached on disk), ready for playback.</summary>
    public async Task<string> GetVoiceWavAsync(string chatUsername, VoicePayload voice)
    {
        var silk = FindVoiceData(chatUsername, voice.LocalId, voice.CreateTime);
        if (silk == null) return null;
        var cachePath = Path.Combine(_dataDir, "media-cache", "voice",
            Convert.ToHexString(Encoding.UTF8.GetBytes(chatUsername))[..64],
            $"{voice.LocalId}_{voice.CreateTime}.wav");
        await SilkVoiceDecoder.SilkToWavAsync(silk, cachePath);
        return cachePath;
    }

    // ---------- avatars ----------

    public DecodedMedia Avatar(string username)
    {
        if (string.IsNullOrEmpty(username)) return null;
        if (_avatarCache.TryGetValue(username, out var cached)) return cached;
        DecodedMedia result = null;
        try
        {
            var db = Db("head_image/head_image.db");
            var row = Query(db, "select image_buffer from head_image where username = ?", username);
            if (row.Count > 0 && row[0]["image_buffer"] is byte[] buffer && buffer.Length > 0
                && ImageDecoder.IsDisplayableImage(buffer))
            {
                result = new DecodedMedia { Buffer = buffer, ContentType = ImageDecoder.ImageContentType(buffer) };
            }
        }
        catch (Exception error) when (error is not FileNotFoundException)
        {
            // head_image 数据库缺失属正常降级（显示彩色首字），不刷日志；
            // 其他异常（损坏等）记录一次便于排查
            StartupLogger.Log($"[avatar] {username} 头像查询失败: {error.Message}");
        }
        _avatarCache[username] = result;
        return result;
    }

    private async Task<DecodedMedia> LoadCachedAvatarAsync(string username, string cacheFile)
    {
        try
        {
            var buffer = await File.ReadAllBytesAsync(cacheFile);
            if (ImageDecoder.IsDisplayableImage(buffer))
            {
                var result = new DecodedMedia
                {
                    Buffer = buffer,
                    ContentType = ImageDecoder.ImageContentType(buffer),
                    File = cacheFile,
                };
                _avatarCache[username] = result;
                return result;
            }
        }
        catch
        {
            // fall through to the network path
        }
        return null;
    }

    /// <summary>Load an avatar blob, falling back to the contact's qlogo URL (cached on disk).</summary>
    public async Task<DecodedMedia> ResolveAvatarAsync(string username)
    {
        var local = Avatar(username);
        if (local != null) return local;
        if (string.IsNullOrEmpty(username)) return null;
        var cacheFile = Path.Combine(_avatarMediaDir, $"{TextUtils.Md5Hex(username)}.img");
        if (File.Exists(cacheFile))
        {
            var cached = await LoadCachedAvatarAsync(username, cacheFile);
            if (cached != null) return cached;
        }
        var url = _contacts.TryGetValue(username, out var contact) ? contact.BigHeadUrl : null;
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        var host = parsed.Host.ToLowerInvariant();
        if (!(host == "wx.qlogo.cn" || host.EndsWith(".qlogo.cn"))) return null;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await Http.Value.GetAsync(parsed, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            var buffer = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (buffer.Length < 100 || buffer.Length > 2 * 1024 * 1024 || !ImageDecoder.IsDisplayableImage(buffer))
            {
                return null;
            }
            Directory.CreateDirectory(_avatarMediaDir);
            await File.WriteAllBytesAsync(cacheFile, buffer);
            var result = new DecodedMedia
            {
                Buffer = buffer,
                ContentType = ImageDecoder.ImageContentType(buffer),
                File = cacheFile,
            };
            _avatarCache[username] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    // ---------- stickers ----------

    public string StickerThumbPath(string md5Hex)
    {
        var key = (md5Hex ?? "").ToLowerInvariant();
        if (!Regex.IsMatch(key, "^[0-9a-f]{32}$")) return null;
        var file = Path.Combine(_stickerThumbDir, $"{key}.png");
        return File.Exists(file) ? file : null;
    }

    private string[] StickerCandidates(string md5Hex)
    {
        if (string.IsNullOrEmpty(md5Hex) || !Regex.IsMatch(md5Hex, "^[0-9a-fA-F]{32}$")) return Array.Empty<string>();
        var key = md5Hex.ToLowerInvariant();
        var sub = key[..2];
        return new[]
        {
            Path.Combine(_emoticonDir, "Thumb", sub, $"{key}.thumb"),
            Path.Combine(_emoticonDir, "Persist", sub, key),
            Path.Combine(_emoticonDir, "ThumbStore", sub, $"{key}.thumb"),
            Path.Combine(_emoticonDir, "PersistStore", sub, key),
        }.Where(File.Exists).ToArray();
    }

    public DecodedMedia ReadSticker(string md5Hex)
    {
        var key = (md5Hex ?? "").ToLowerInvariant();
        if (_stickerCache.TryGetValue(key, out var cached)) return cached;
        DecodedMedia result = null;
        var downloaded = FindDownloadedSticker(key);
        if (downloaded != null) result = downloaded;
        if (result == null)
        {
            foreach (var file in StickerCandidates(key))
            {
                byte[] buffer;
                try
                {
                    buffer = File.ReadAllBytes(file);
                }
                catch
                {
                    continue;
                }
                if (!ImageDecoder.IsDisplayableImage(buffer) && EmoticonAesKey != null)
                {
                    var decoded = ImageDecoder.DecodeEmoticonFile(buffer, EmoticonAesKey);
                    if (decoded != null) buffer = decoded;
                }
                if (!ImageDecoder.IsDisplayableImage(buffer)) continue;
                result = new DecodedMedia
                {
                    Buffer = buffer,
                    ContentType = ImageDecoder.ImageContentType(buffer),
                    File = file,
                };
                break;
            }
        }
        _stickerCache[key] = result;
        return result;
    }

    private DecodedMedia FindDownloadedSticker(string md5Hex)
    {
        if (string.IsNullOrEmpty(md5Hex) || !Regex.IsMatch(md5Hex, "^[0-9a-f]{32}$")) return null;
        var dir = Path.Combine(_stickerDownloadDir, md5Hex[..2]);
        foreach (var ext in new[] { "gif", "png", "jpg", "webp" })
        {
            var file = Path.Combine(dir, $"{md5Hex}.{ext}");
            if (!File.Exists(file)) continue;
            try
            {
                var buffer = File.ReadAllBytes(file);
                if (ImageDecoder.IsDisplayableImage(buffer))
                {
                    return new DecodedMedia
                    {
                        Buffer = buffer,
                        ContentType = ImageDecoder.ImageContentType(buffer),
                        File = file,
                    };
                }
            }
            catch { }
        }
        return null;
    }

    private static bool IsStickerCdnHost(string hostname)
    {
        var host = (hostname ?? "").ToLowerInvariant();
        // 腾讯系 CDN 全域放行：tc.qq.com / qpic.cn / gtimg 系列 / qlogo.cn 等，
        // 微信 4.x 表情 cdnurl 可能落在 mmbiz.qpic.cn、vweixinf.tc.qq.com、giffix.gtimg.cn 等多个域。
        return host.EndsWith(".qq.com") || host == "qq.com"
            || host.EndsWith(".qpic.cn") || host == "qpic.cn"
            || host.EndsWith(".gtimg.com") || host.EndsWith(".gtimg.cn")
            || host.EndsWith(".qlogo.cn") || host == "qlogo.cn";
    }

    /// <summary>
    /// Download a sticker that WeChat never cached locally from the CDN URL recorded
    /// in the message. Only Tencent CDN hosts are accepted; MD5 must match.
    /// encrypturl 加密变体在下载后用 aeskey 做 AES-128-CBC 解密（key=IV 与首块 IV 两种方案都尝试）。
    /// </summary>
    public async Task<DecodedMedia> DownloadStickerAsync(string md5Hex, string url, string aesKeyHex = null)
    {
        var key = (md5Hex ?? "").ToLowerInvariant();
        if (!Regex.IsMatch(key, "^[0-9a-f]{32}$") || string.IsNullOrEmpty(url)) return null;
        var cached = FindDownloadedSticker(key);
        if (cached != null)
        {
            _stickerCache[key] = cached;
            return cached;
        }
        var cleaned = TextUtils.HtmlUnescape(url);
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var target)) return null;
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return null;
        if (!IsStickerCdnHost(target.Host)) return null;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            request.Headers.Referrer = new Uri("https://wx.qq.com/");
            using var response = await Http.Value.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            if (!string.IsNullOrEmpty(response.Headers.Location?.Host)
                && !IsStickerCdnHost(response.Headers.Location.Host))
            {
                return null;
            }
            var buffer = await response.Content.ReadAsByteArrayAsync(cts.Token);
            if (buffer.Length == 0 || buffer.Length > 10 * 1024 * 1024) return null;
            var decoded = DecodeDownloadedSticker(buffer, key, aesKeyHex);
            if (decoded == null) return null;
            var ext = decoded[0] switch
            {
                0x89 => "png",
                0x47 => "gif",
                0x52 => "webp",
                _ => "jpg",
            };
            var file = Path.Combine(_stickerDownloadDir, key[..2], $"{key}.{ext}");
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            await File.WriteAllBytesAsync(file, decoded);
            var result = new DecodedMedia
            {
                Buffer = decoded,
                ContentType = ImageDecoder.ImageContentType(decoded),
                File = file,
            };
            _stickerCache[key] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>明文直接通过 MD5 校验；否则尝试 aeskey 解密变体，再验 MD5。</summary>
    private static byte[] DecodeDownloadedSticker(byte[] buffer, string md5Hex, string aesKeyHex)
    {
        if (TextUtils.Md5Hex(buffer) == md5Hex && ImageDecoder.IsDisplayableImage(buffer)) return buffer;
        if (string.IsNullOrEmpty(aesKeyHex) || aesKeyHex.Length != 32) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(aesKeyHex, "^[0-9a-fA-F]{32}$")) return null;
        var keyBytes = Convert.FromHexString(aesKeyHex);
        // 方案 1：key = IV（与本地表情文件一致的方案）
        var plain = ImageDecoder.DecodeEmoticonFile(buffer, keyBytes);
        if (plain != null && TextUtils.Md5Hex(plain) == md5Hex) return plain;
        // 方案 2：IV 取密文首 16 字节
        if (buffer.Length > 16)
        {
            var rest = new byte[buffer.Length - 16];
            Array.Copy(buffer, 16, rest, 0, rest.Length);
            var ivPrefixed = ImageDecoder.AesCbcDecrypt(rest, keyBytes, buffer.AsSpan(0, 16).ToArray());
            if (ivPrefixed != null && TextUtils.Md5Hex(ivPrefixed) == md5Hex) return ivPrefixed;
        }
        return null;
    }

    /// <summary>120px PNG thumbnail for exports, generated on demand (System.Drawing).</summary>
    public string EnsureStickerThumbnail(string md5Hex, StickerThumbs thumbs)
    {
        var existing = StickerThumbPath(md5Hex);
        if (existing != null) return existing;
        var sticker = ReadSticker(md5Hex);
        if (sticker?.File == null) return null;
        try
        {
            Directory.CreateDirectory(_stickerThumbDir);
            return thumbs.CreateThumbnail(sticker.File,
                Path.Combine(_stickerThumbDir, $"{md5Hex.ToLowerInvariant()}.png"), 120);
        }
        catch
        {
            return null;
        }
    }
}
