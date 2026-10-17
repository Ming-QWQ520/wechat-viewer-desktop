using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// Convert WeChat SILK voice data to WAV (24000 Hz mono 16-bit) using the
/// bundled silkdec.exe (kn007/silk-v3-decoder, MIT), mirroring app/media.mjs.
/// Results are cached on disk like the original.
/// </summary>
public static class SilkVoiceDecoder
{
    public static void WrapWavHeader(Span<byte> header, int pcmLength, int sampleRate = 24000, int channels = 1, int bitsPerSample = 16)
    {
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);
        WriteAscii(header, 0, "RIFF");
        WriteU32(header, 4, (uint)(36 + pcmLength));
        WriteAscii(header, 8, "WAVE");
        WriteAscii(header, 12, "fmt ");
        WriteU32(header, 16, 16);
        WriteU16(header, 20, 1);
        WriteU16(header, 22, (ushort)channels);
        WriteU32(header, 24, (uint)sampleRate);
        WriteU32(header, 28, (uint)byteRate);
        WriteU16(header, 32, (ushort)blockAlign);
        WriteU16(header, 34, (ushort)bitsPerSample);
        WriteAscii(header, 36, "data");
        WriteU32(header, 40, (uint)pcmLength);
    }

    private static void WriteAscii(Span<byte> dst, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++) dst[offset + i] = (byte)text[i];
    }

    private static void WriteU16(Span<byte> dst, int offset, ushort value)
    {
        dst[offset] = (byte)(value & 0xff);
        dst[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(Span<byte> dst, int offset, uint value)
    {
        dst[offset] = (byte)(value & 0xff);
        dst[offset + 1] = (byte)((value >> 8) & 0xff);
        dst[offset + 2] = (byte)((value >> 16) & 0xff);
        dst[offset + 3] = (byte)((value >> 24) & 0xff);
    }

    public static byte[] PcmToWav(byte[] pcm, int sampleRate = 24000)
    {
        var wav = new byte[44 + pcm.Length];
        WrapWavHeader(wav, pcm.Length, sampleRate);
        Array.Copy(pcm, 0, wav, 44, pcm.Length);
        return wav;
    }

    private static readonly byte[] SilkMagic = Encoding.ASCII.GetBytes("#!SILK_V3");
    private static readonly byte[] AmrMagic = Encoding.ASCII.GetBytes("#!AMR");

    private static bool StartsWith(byte[] data, byte[] magic, int offset)
    {
        if (offset < 0 || offset + magic.Length > data.Length) return false;
        for (int i = 0; i < magic.Length; i++)
            if (data[offset + i] != magic[i]) return false;
        return true;
    }

    private static int IndexOfMagic(byte[] data)
    {
        for (int i = 0; i + SilkMagic.Length <= data.Length; i++)
            if (StartsWith(data, SilkMagic, i)) return i;
        return -1;
    }

    /// <summary>
    /// Normalize a WeChat voice blob into the exact bitstream silkdec accepts:
    /// [1 junk byte] + "#!SILK_V3" + frames(int16 length + payload).
    /// - WeChat blobs normally start with 0x02 + "#!SILK_V3" (already accepted as-is).
    /// - Some blobs carry an extra '\n' right after the magic; the decoder would consume
    ///   it as the first frame's length byte and produce garbage — strip it.
    /// - AMR voices ("#!AMR") cannot be handled by the SILK decoder → descriptive error.
    /// </summary>
    public static byte[] NormalizeSilk(byte[] silk)
    {
        if (silk == null || silk.Length == 0)
            throw new InvalidDataException("语音数据为空");
        // AMR detection
        if (silk.Length >= 5 && Encoding.ASCII.GetString(silk, 0, 5) == "#!AMR")
            throw new InvalidDataException("该语音为 AMR 格式（voice_format≠SILK），内置解码器暂不支持");

        int magic = IndexOfMagic(silk);
        if (magic < 0)
        {
            var head = new StringBuilder();
            for (int i = 0; i < Math.Min(16, silk.Length); i++) head.Append(silk[i].ToString("X2")).Append(' ');
            throw new InvalidDataException("未找到 #!SILK_V3 头（前16字节: " + head + "）");
        }

        // keep exactly one junk byte in front of the magic (decoder skips 1 byte)
        int junk = magic >= 1 ? magic - 1 : magic;         // position of the junk byte
        int body = magic + SilkMagic.Length;               // right after "#!SILK_V3"

        // A '\n' right after the magic may be a separator (standard silk files) — or a real
        // frame-length byte (a 10-byte first frame has low byte 0x0A). Decide by parsing the
        // frame chain: prefer the layout that walks cleanly to the end/terminator.
        bool hasNl = body < silk.Length && silk[body] == 0x0A;
        int stripped = hasNl ? body + 1 : body;
        int chosen;
        if (!hasNl) chosen = body;
        else if (StreamParses(silk, stripped)) chosen = stripped;
        else if (StreamParses(silk, body)) chosen = body;
        else chosen = stripped;                            // best effort

        var ms = new MemoryStream(silk.Length - magic + 1);
        if (magic == 0) ms.WriteByte(0x02);                // decoder needs 1 leading byte
        else ms.WriteByte(silk[junk]);
        ms.Write(SilkMagic, 0, SilkMagic.Length);
        ms.Write(silk, chosen, silk.Length - chosen);
        return ms.ToArray();
    }

    /// <summary>Walk the SILK frame chain (int16 length + payload) from pos; true if it lands
    /// exactly on the end or a 0xFFFF terminator at the end.</summary>
    private static bool StreamParses(byte[] data, int pos)
    {
        int end = data.Length;
        while (pos + 2 <= end)
        {
            int len = data[pos] | (data[pos + 1] << 8);    // little-endian int16
            if (len == 0xFFFF) return pos + 2 == end;      // terminator must end the stream
            if (len == 0 || len > 1024 || pos + 2 + len > end) return false;
            pos += 2 + len;
        }
        return pos == end;
    }

    private static async Task<byte[]> DecodeSilkAsync(byte[] silk)
    {
        if (!NativeTools.HasTool(NativeTools.SilkDecExe))
            throw new InvalidOperationException("tools\\silkdec.exe 缺失：语音解码组件未找到。");
        string tempDir = Path.Combine(Path.GetTempPath(), "wechat-viewer-silk");
        Directory.CreateDirectory(tempDir);
        string silkFile = Path.Combine(tempDir, Path.GetRandomFileName() + ".silk");
        string pcmFile = Path.Combine(tempDir, Path.GetRandomFileName() + ".pcm");
        try
        {
            await File.WriteAllBytesAsync(silkFile, silk);
            var result = await NativeTools.RunToolAsync(NativeTools.SilkDecExe,
                $"\"{silkFile}\" \"{pcmFile}\" -Fs_API 24000 -quiet", 120000);
            // Decoder.c always exits 0 — the real success signal is the PCM file.
            if (!File.Exists(pcmFile))
                throw new InvalidDataException("silkdec 未生成 PCM: " + result.Output.Trim());
            var pcm = await File.ReadAllBytesAsync(pcmFile);
            if (pcm.Length == 0)
                throw new InvalidDataException("silkdec 输出 PCM 为空: " + result.Output.Trim());
            return pcm;
        }
        finally
        {
            TryDelete(silkFile);
            TryDelete(pcmFile);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Decode SILK to WAV and cache to cachePath; returns the WAV bytes.</summary>
    public static async Task<byte[]> SilkToWavAsync(byte[] silk, string cachePath)
    {
        if (File.Exists(cachePath))
        {
            StartupLogger.Log($"[voice] 命中缓存 {Path.GetFileName(cachePath)} ({new FileInfo(cachePath).Length} B)");
            return await File.ReadAllBytesAsync(cachePath);
        }
        if (silk == null || silk.Length == 0)
            throw new InvalidDataException("语音数据为空（voice_data 缺失）");

        StartupLogger.Log($"[voice] 解码开始 silk={silk.Length}B head={Head(silk)}");
        // Attempt 1: normalized bitstream (handles 0x02 prefix, extra \n, stray leading bytes).
        // Attempt 2: raw payload as-is (belt & braces for unexpected layouts).
        byte[] pcm = null;
        Exception last = null;
        foreach (var (label, payload) in new (string, byte[])[] { ("normalized", NormalizeSilk(silk)), ("raw", silk) })
        {
            try
            {
                pcm = await DecodeSilkAsync(payload);
                StartupLogger.Log($"[voice] 解码成功 ({label}) pcm={pcm.Length}B");
                break;
            }
            catch (Exception error)
            {
                last = error;
                StartupLogger.Log($"[voice] 解码失败 ({label}): {error.Message}");
            }
        }
        if (pcm == null) throw last ?? new InvalidDataException("语音解码失败");

        var wav = PcmToWav(pcm, 24000);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
        var tmp = cachePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, wav);
        if (File.Exists(cachePath)) File.Delete(cachePath);
        File.Move(tmp, cachePath);
        StartupLogger.Log($"[voice] WAV 已缓存 {Path.GetFileName(cachePath)} ({wav.Length}B)");
        return wav;
    }

    private static string Head(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(12, data.Length); i++) sb.Append(data[i].ToString("X2")).Append(' ');
        return sb.ToString();
    }
}
