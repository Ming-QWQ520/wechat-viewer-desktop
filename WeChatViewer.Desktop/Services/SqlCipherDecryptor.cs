using System;
using System.IO;
using System.Security.Cryptography;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// SQLCipher 4 page decryption, ported from app/sqlcipher.mjs.
/// WeChat 4.x uses WCDB/SQLCipher 4: page size 4096, AES-256-CBC + HMAC
/// (reserve 80: nonce 16 + HMAC-SHA512 64; legacy 48/36 supported).
/// </summary>
public static class SqlCipherDecryptor
{
    public const int PageSize = 4096;
    public static readonly byte[] SqliteMagic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    private class HmacAlgo
    {
        public HashAlgorithmName Hash { get; }
        public int HmacSize { get; }
        public HmacAlgo(HashAlgorithmName hash, int hmacSize) { Hash = hash; HmacSize = hmacSize; }
    }

    private static readonly System.Collections.Generic.Dictionary<int, HmacAlgo> HmacAlgos = new()
    {
        { 80, new HmacAlgo(HashAlgorithmName.SHA512, 64) },
        { 48, new HmacAlgo(HashAlgorithmName.SHA256, 32) },
        { 36, new HmacAlgo(HashAlgorithmName.SHA1, 20) },
    };

    private static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int keylen, HashAlgorithmName hash)
    {
        using var derive = new Rfc2898DeriveBytes(password, salt, iterations, hash);
        return derive.GetBytes(keylen);
    }

    private static byte[] MacKeyFor(byte[] encKey, byte[] salt, HashAlgorithmName hash)
    {
        var macSalt = new byte[salt.Length];
        for (int i = 0; i < salt.Length; i++) macSalt[i] = (byte)(salt[i] ^ 0x3a);
        return Pbkdf2(encKey, macSalt, 2, 32, hash);
    }

    private static byte[] PageHmac(byte[] encKey, byte[] pageData, int pgno, int reserve, HmacAlgo algo)
    {
        int nonceStart = PageSize - reserve;
        var salt = new byte[16];
        Array.Copy(pageData, 0, salt, 0, 16);
        var macKey = MacKeyFor(encKey, salt, algo.Hash);
        int offset = pgno == 1 ? 16 : 0;
        int count = nonceStart + 16 - offset;
        byte[] pgnoBuf = BitConverter.GetBytes(pgno); // little-endian on all supported platforms
        byte[] digest;
        using (HMAC mac = algo.Hash == HashAlgorithmName.SHA512 ? new HMACSHA512(macKey)
             : algo.Hash == HashAlgorithmName.SHA256 ? new HMACSHA256(macKey)
             : new HMACSHA1(macKey))
        {
            mac.TransformBlock(pageData, offset, count, null, 0);
            mac.TransformFinalBlock(pgnoBuf, 0, pgnoBuf.Length);
            digest = mac.Hash;
        }
        var result = new byte[algo.HmacSize];
        Array.Copy(digest, result, Math.Min(algo.HmacSize, digest.Length));
        return result;
    }

    public static bool HeaderLooksValid(byte[] plain)
    {
        if (plain == null || plain.Length < 24) return false;
        int pageSize = (plain[16] << 8) | plain[17];
        bool validPageSize = pageSize >= 512 && pageSize <= 65536 && (pageSize & (pageSize - 1)) == 0;
        int wv = plain[18], rv = plain[19];
        return validPageSize && (wv == 1 || wv == 2) && (rv == 1 || rv == 2)
            && plain[21] == 0x40 && plain[22] == 0x20 && plain[23] == 0x20;
    }

    /// <summary>Decrypt page 1 and return the plaintext header (or null).</summary>
    private static byte[] TryHeader(byte[] encKey, byte[] page1, int reserve)
    {
        if (page1.Length < PageSize) return null;
        int nonceStart = PageSize - reserve;
        var nonce = new byte[16];
        Array.Copy(page1, nonceStart, nonce, 0, 16);
        int cipherLen = nonceStart - 16;
        if (cipherLen <= 0 || cipherLen % 16 != 0) return null;
        try
        {
            using var aes = Aes.Create();
            aes.Key = encKey;
            aes.IV = nonce;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            var cipher = new byte[cipherLen];
            Array.Copy(page1, 16, cipher, 0, cipherLen);
            var plain = dec.TransformFinalBlock(cipher, 0, cipherLen);
            var header = new byte[16 + plain.Length];
            Array.Copy(SqliteMagic, header, SqliteMagic.Length);
            Array.Copy(plain, 0, header, 16, plain.Length);
            return HeaderLooksValid(header) ? header : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Detect the SQLCipher page reserve size and verify the key via HMAC.</summary>
    public static (int Reserve, HashAlgorithmName Hash, int HmacSize)? DetectCipher(byte[] encKey, byte[] page1)
    {
        foreach (var reserve in new[] { 80, 48, 36 })
        {
            var algo = HmacAlgos[reserve];
            var header = TryHeader(encKey, page1, reserve);
            if (header == null) continue;
            var expected = PageHmac(encKey, page1, 1, reserve, algo);
            var stored = new byte[algo.HmacSize];
            Array.Copy(page1, PageSize - reserve + 16, stored, 0, algo.HmacSize);
            bool equal = expected.Length == stored.Length;
            for (int i = 0; equal && i < expected.Length; i++) equal = expected[i] == stored[i];
            if (equal) return (reserve, algo.Hash, algo.HmacSize);
        }
        return null;
    }

    /// <summary>Decrypt one encrypted SQLCipher database file to a plain SQLite file.</summary>
    public static (int Pages, int Reserve) DecryptDatabase(string srcPath, string dstPath, string encKeyHex)
    {
        var src = File.ReadAllBytes(srcPath);
        if (src.Length < PageSize) throw new InvalidDataException("file too small");
        var encKey = HexToBytes(encKeyHex);
        if (encKey.Length != 32) throw new InvalidDataException("bad key length");

        var page1 = new byte[PageSize];
        Array.Copy(src, page1, Math.Min(PageSize, src.Length));
        var cipher = DetectCipher(encKey, page1) ?? throw new InvalidDataException("key/page format did not verify");
        int reserve = cipher.Reserve;
        int payloadEnd = PageSize - reserve;
        int totalPages = (int)Math.Ceiling((decimal)src.Length / PageSize);
        var output = new byte[totalPages * PageSize];

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.Padding = PaddingMode.None;

        for (int pgno = 1; pgno <= totalPages; pgno++)
        {
            int start = (pgno - 1) * PageSize;
            int len = Math.Min(PageSize, src.Length - start);
            var padded = new byte[PageSize];
            Array.Copy(src, start, padded, 0, len);
            var nonce = new byte[16];
            Array.Copy(padded, payloadEnd, nonce, 0, 16);
            int offset = pgno == 1 ? 16 : 0;
            int cipherLen = payloadEnd - offset;
            if (cipherLen % 16 != 0) throw new InvalidDataException("misaligned cipher page");
            aes.IV = nonce;
            using var dec = aes.CreateDecryptor();
            var plain = dec.TransformFinalBlock(padded, offset, cipherLen);
            if (pgno == 1)
            {
                Array.Copy(SqliteMagic, 0, output, start, SqliteMagic.Length);
                Array.Copy(plain, 0, output, start + 16, Math.Min(plain.Length, PageSize - 16));
            }
            else
            {
                Array.Copy(plain, 0, output, start, Math.Min(plain.Length, PageSize));
            }
            // Reserved bytes stay zero: SQLite never uses them.
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dstPath));
        var tmp = dstPath + ".tmp";
        File.WriteAllBytes(tmp, output);
        if (File.Exists(dstPath)) File.Delete(dstPath);
        File.Move(tmp, dstPath);
        return (totalPages, reserve);
    }

    public static byte[] HexToBytes(string hex)
    {
        if (hex == null || hex.Length % 2 != 0) return Array.Empty<byte>();
        var result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = byte.Parse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        return result;
    }
}
