using System;
using System.IO;
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
            if (!result.Success || !File.Exists(pcmFile))
                throw new InvalidDataException("silkdec failed: " + result.Output.Trim());
            return await File.ReadAllBytesAsync(pcmFile);
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
        if (File.Exists(cachePath)) return await File.ReadAllBytesAsync(cachePath);
        byte[] pcm;
        try
        {
            pcm = await DecodeSilkAsync(silk);
        }
        catch
        {
            // Some records carry a 3-byte 0x02 '#!' prefix already; retry raw payload.
            if (silk.Length <= 3) throw;
            pcm = await DecodeSilkAsync(silk.AsSpan(3).ToArray());
        }
        var wav = PcmToWav(pcm, 24000);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
        var tmp = cachePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, wav);
        if (File.Exists(cachePath)) File.Delete(cachePath);
        File.Move(tmp, cachePath);
        return wav;
    }
}
