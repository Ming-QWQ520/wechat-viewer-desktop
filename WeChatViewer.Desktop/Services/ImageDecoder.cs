using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// WeChat image decoding, ported from app/wechat.mjs.
/// - V2: header + AES-128-ECB segment + plaintext + XOR segment (account key)
/// - V1: like V2 with the fixed key "cfcd208495d565ef"
/// - Legacy: single-byte XOR (auto-detected)
/// </summary>
public static class ImageDecoder
{
    public static bool IsImageMagic(byte[] b)
    {
        if (b == null || b.Length < 4) return false;
        if (b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff) return true;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4e && b[3] == 0x47) return true;
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true;
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b.Length >= 12 && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return true;
        if (b[0] == 0x77 && b[1] == 0x78 && b[2] == 0x67 && b[3] == 0x66) return true; // wxgf
        return false;
    }

    /// <summary>Browser-renderable image formats only (wxgf containers are excluded).</summary>
    public static bool IsDisplayableImage(byte[] b)
    {
        if (b == null || b.Length < 4) return false;
        if (b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff) return true;
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4e && b[3] == 0x47) return true;
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true;
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b.Length >= 12 && Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return true;
        return false;
    }

    public static string ImageContentType(byte[] b)
    {
        if (b == null || b.Length < 2) return "application/octet-stream";
        if (b[0] == 0xff && b[1] == 0xd8) return "image/jpeg";
        if (b[0] == 0x89 && b[1] == 0x50) return "image/png";
        if (b[0] == 0x47 && b[1] == 0x49) return "image/gif";
        if (b[0] == 0x42 && b[1] == 0x4d) return "image/bmp";
        if (b[0] == 0x52 && b[1] == 0x49) return "image/webp";
        return "application/octet-stream";
    }

    /// <summary>Cut WeChat's 101-byte trailer (and similar padding) after the real image terminator.</summary>
    public static byte[] TrimImageBuffer(byte[] buf)
    {
        if (buf == null || buf.Length < 12) return null;
        if (buf[0] == 0xff && buf[1] == 0xd8)
        {
            for (int i = buf.Length - 2; i >= 2; i--)
            {
                if (buf[i] == 0xff && buf[i + 1] == 0xd9)
                {
                    var result = new byte[i + 2];
                    Array.Copy(buf, result, i + 2);
                    return result;
                }
            }
            return null;
        }
        if (buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4e && buf[3] == 0x47)
        {
            byte[] end = { 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42, 0x60, 0x82 };
            for (int i = buf.Length - end.Length; i >= 0; i--)
            {
                bool match = true;
                for (int j = 0; j < end.Length; j++)
                {
                    if (buf[i + j] != end[j]) { match = false; break; }
                }
                if (match)
                {
                    var result = new byte[i + end.Length];
                    Array.Copy(buf, result, i + end.Length);
                    return result;
                }
            }
            return null;
        }
        if (buf[0] == 0x47 && buf[1] == 0x49 && buf[2] == 0x46)
        {
            int stop = Math.Max(0, buf.Length - 8192);
            for (int i = buf.Length - 1; i >= stop; i--)
            {
                if (buf[i] == 0x3b)
                {
                    var result = new byte[i + 1];
                    Array.Copy(buf, result, i + 1);
                    return result;
                }
            }
            return null;
        }
        if (buf[0] == 0x52 && buf[1] == 0x49 && buf[2] == 0x46 && buf[3] == 0x46 && buf.Length >= 12)
        {
            uint size = BitConverter.ToUInt32(buf, 4) + 8;
            if (size <= (uint)buf.Length)
            {
                var result = new byte[size];
                Array.Copy(buf, result, size);
                return result;
            }
            return null;
        }
        return Encoding.ASCII.GetString(buf, 0, 4) == "wxgf" ? buf : null;
    }

    /// <summary>Validate PNG chunk structure and inflate IDAT data (supports APNG).</summary>
    public static bool ValidatePngBuffer(byte[] buf)
    {
        if (buf == null || buf.Length < 33 || buf[0] != 0x89 || buf[1] != 0x50 || buf[2] != 0x4e || buf[3] != 0x47)
            return false;
        int pos = 8;
        byte[] ihdr = null;
        byte[][] idatChunks = Array.Empty<byte[]>();
        int idatCount = 0;
        long totalIdat = 0;
        while (pos + 12 <= buf.Length)
        {
            uint length = ((uint)buf[pos] << 24) | ((uint)buf[pos + 1] << 16) | ((uint)buf[pos + 2] << 8) | buf[pos + 3];
            if (length > (uint)buf.Length || pos + 12 + (long)length > buf.Length) return false;
            string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
            if (type == "IHDR") { ihdr = new byte[length]; Array.Copy(buf, pos + 8, ihdr, 0, length); }
            else if (type == "IDAT")
            {
                if (idatCount == 0)
                {
                    idatChunks = new byte[8][];
                }
                else if (idatCount == idatChunks.Length)
                {
                    Array.Resize(ref idatChunks, idatChunks.Length * 2);
                }
                idatChunks[idatCount] = new byte[length];
                Array.Copy(buf, pos + 8, idatChunks[idatCount], 0, length);
                idatCount++;
                totalIdat += length;
            }
            else if (type == "IEND") break;
            pos += 12 + (int)length;
        }
        if (ihdr == null || ihdr.Length < 13 || idatCount == 0) return false;
        uint width = ((uint)ihdr[0] << 24) | ((uint)ihdr[1] << 16) | ((uint)ihdr[2] << 8) | ihdr[3];
        uint height = ((uint)ihdr[4] << 24) | ((uint)ihdr[5] << 16) | ((uint)ihdr[6] << 8) | ihdr[7];
        int bitDepth = ihdr[8];
        int colorType = ihdr[9];
        int interlace = ihdr[12];
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width == 0 || height == 0 || channels == 0) return false;
        try
        {
            var idat = new byte[totalIdat];
            long copied = 0;
            for (int i = 0; i < idatCount; i++)
            {
                Array.Copy(idatChunks[i], 0, idat, copied, idatChunks[i].Length);
                copied += idatChunks[i].Length;
            }
            using var input = new MemoryStream(idat);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var raw = new MemoryStream();
            zlib.CopyTo(raw);
            if (interlace == 0)
            {
                long bpp = (channels * bitDepth + 7) / 8;
                if (raw.Length < height * (1 + width * bpp)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] AesEcbDecrypt(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>Decode a WeChat 4.x image file. Returns decoded bytes or null.</summary>
    public static byte[] DecodeImageFile(byte[] buf, string aesKey = null, int xorKey = 0x8f)
    {
        if (buf == null) return null;
        if (buf.Length >= 15)
        {
            bool isV2 = buf[0] == 0x07 && buf[1] == 0x08 && buf[2] == 0x56 && buf[3] == 0x32 && buf[4] == 0x08 && buf[5] == 0x07;
            bool isV1 = buf[0] == 0x07 && buf[1] == 0x08 && buf[2] == 0x56 && buf[3] == 0x31 && buf[4] == 0x08 && buf[5] == 0x07;
            if (isV2 || isV1)
            {
                byte[] key = null;
                if (isV1) key = Encoding.ASCII.GetBytes("cfcd208495d565ef");
                else if (!string.IsNullOrEmpty(aesKey))
                {
                    var ascii = Encoding.ASCII.GetBytes(aesKey);
                    key = new byte[Math.Min(16, ascii.Length)];
                    Array.Copy(ascii, key, key.Length);
                }
                if (key == null || key.Length < 16) return null;
                uint aesSize = BitConverter.ToUInt32(buf, 6);
                uint xorSize = BitConverter.ToUInt32(buf, 10);
                long aligned = (aesSize + 15) / 16 * 16;
                long rawEnd = buf.Length - xorSize;
                if (aligned <= 0 || 15 + aligned > buf.Length || rawEnd < 15 + aligned) return null;
                try
                {
                    var aesCipher = new byte[aligned];
                    Array.Copy(buf, 15, aesCipher, 0, aligned);
                    var aesPlain = AesEcbDecrypt(key, aesCipher);
                    if (aesSize == 0 || aesSize > (uint)aesPlain.Length) return null;
                    var raw = new byte[Math.Max(0, rawEnd - (15 + aligned))];
                    Array.Copy(buf, 15 + aligned, raw, 0, raw.Length);
                    var xorPart = new byte[xorSize];
                    for (long i = 0; i < xorSize; i++) xorPart[i] = (byte)(buf[rawEnd + i] ^ xorKey);
                    var output = new byte[aesSize + raw.Length + xorPart.Length];
                    Array.Copy(aesPlain, 0, output, 0, aesSize);
                    Array.Copy(raw, 0, output, aesSize, raw.Length);
                    Array.Copy(xorPart, 0, output, aesSize + raw.Length, xorPart.Length);
                    return IsImageMagic(output) ? TrimImageBuffer(output) : null;
                }
                catch
                {
                    return null;
                }
            }
        }
        if (IsImageMagic(buf)) return TrimImageBuffer(buf);
        byte[][] magics =
        {
            new byte[] { 0xff, 0xd8, 0xff },
            new byte[] { 0x89, 0x50, 0x4e, 0x47 },
            new byte[] { 0x47, 0x49, 0x46, 0x38 },
            new byte[] { 0x52, 0x49, 0x46, 0x46 },
        };
        foreach (var magic in magics)
        {
            int keyByte = buf[0] ^ magic[0];
            bool ok = true;
            for (int i = 1; i < magic.Length && i < buf.Length; i++)
            {
                if ((buf[i] ^ keyByte) != magic[i]) { ok = false; break; }
            }
            if (ok)
            {
                var output = new byte[buf.Length];
                for (int i = 0; i < buf.Length; i++) output[i] = (byte)(buf[i] ^ keyByte);
                if (IsImageMagic(output)) return TrimImageBuffer(output);
            }
        }
        var decoded = new byte[buf.Length];
        for (int i = 0; i < buf.Length; i++) decoded[i] = (byte)(buf[i] ^ 0x8f);
        return IsImageMagic(decoded) ? TrimImageBuffer(decoded) : null;
    }

    /// <summary>Decrypt a WeChat 4.x emoticon file: AES-128-CBC with key = IV. Plain images pass through.</summary>
    public static byte[] DecodeEmoticonFile(byte[] buf, byte[] key)
    {
        if (IsDisplayableImage(buf)) return buf;
        if (key == null || key.Length != 16 || buf == null || buf.Length == 0) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = key;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(buf, 0, buf.Length);
            return IsImageMagic(plain) ? plain : null;
        }
        catch
        {
            return null;
        }
    }
}
