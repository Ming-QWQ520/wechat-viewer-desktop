using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// Incremental database decryption, ported from app/decrypt.mjs.
/// Decrypts every database listed in keys.json into data/decrypted,
/// re-decrypting only when the source size or mtime changed.
/// </summary>
public class DatabaseDecryptor
{
    private readonly string _dataDir;
    private readonly string _keysPath;
    private readonly string _outDir;
    private readonly string _manifestPath;

    public DatabaseDecryptor(string dataDir)
    {
        _dataDir = dataDir;
        _keysPath = Path.Combine(dataDir, "keys.json");
        _outDir = Path.Combine(dataDir, "decrypted");
        _manifestPath = Path.Combine(dataDir, "decrypt-manifest.json");
    }

    private class ManifestEntry
    {
        public string fingerprint { get; set; }
        public string key { get; set; }
        public int pages { get; set; }
        public int reserve { get; set; }
    }

    private Dictionary<string, ManifestEntry> ReadManifest()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ManifestEntry>>(File.ReadAllText(_manifestPath))
                   ?? new Dictionary<string, ManifestEntry>();
        }
        catch
        {
            return new Dictionary<string, ManifestEntry>();
        }
    }

    private void WriteManifest(Dictionary<string, ManifestEntry> manifest)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(_manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public class DecryptStats
    {
        public int Decrypted { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<string> FailedFiles { get; } = new();
    }

    /// <summary>Action<relativePath, message> invoked for progress logging.</summary>
    public event Action<string> Log;

    private void LogLine(string message) => Log?.Invoke(message);

    public DecryptStats DecryptAll(bool force = false)
    {
        var keys = KeysFile.Load(_keysPath);
        var manifest = ReadManifest();
        var stats = new DecryptStats();

        if (keys.Keys != null)
        {
            foreach (var (rel, info) in keys.Keys)
            {
                var src = Path.Combine(keys.DbDir, rel);
                var dst = Path.Combine(_outDir, rel);
                if (!File.Exists(src))
                {
                    stats.Failed++;
                    stats.FailedFiles.Add(rel + " (missing)");
                    continue;
                }
                var fi = new FileInfo(src);
                string fingerprint = $"{fi.Length}:{(long)DateTimeOffset.FromFileTime(fi.LastWriteTimeUtc.ToFileTime()).ToUnixTimeMilliseconds()}";
                manifest.TryGetValue(rel, out var prev);
                bool upToDate =
                    !force && prev != null &&
                    prev.fingerprint == fingerprint &&
                    prev.key == info.EncKey &&
                    File.Exists(dst) &&
                    new FileInfo(dst).Length == (long)Math.Ceiling((decimal)fi.Length / 4096) * 4096;

                if (upToDate)
                {
                    stats.Skipped++;
                    continue;
                }

                try
                {
                    var result = SqlCipherDecryptor.DecryptDatabase(src, dst, info.EncKey);
                    manifest[rel] = new ManifestEntry
                    {
                        fingerprint = fingerprint,
                        key = info.EncKey,
                        pages = result.Pages,
                        reserve = result.Reserve,
                    };
                    stats.Decrypted++;
                    LogLine($"[decrypt] {rel} ({result.Pages} pages, reserve {result.Reserve})");
                }
                catch (Exception error)
                {
                    stats.Failed++;
                    stats.FailedFiles.Add($"{rel}: {error.Message}");
                    LogLine($"[decrypt] FAILED {rel}: {error.Message}");
                }
            }
        }
        WriteManifest(manifest);
        LogLine($"[decrypt] done: {stats.Decrypted} decrypted, {stats.Skipped} unchanged, {stats.Failed} failed");
        return stats;
    }
}
