using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatViewer.Desktop.Services;

public class KeyRecord
{
    [JsonPropertyName("enc_key")] public string EncKey { get; set; }
    [JsonPropertyName("salt")] public string Salt { get; set; }
    [JsonPropertyName("size_mb")] public double SizeMb { get; set; }
}

public class KeysFile
{
    [JsonPropertyName("db_dir")] public string DbDir { get; set; }
    [JsonPropertyName("keys")] public Dictionary<string, KeyRecord> Keys { get; set; } = new();
    [JsonPropertyName("image_aes_key")] public string ImageAesKey { get; set; }
    [JsonPropertyName("image_xor_key")] public int ImageXorKey { get; set; } = 0x8f;
    [JsonPropertyName("emoticon_aes_key")] public string EmoticonAesKey { get; set; }
    [JsonPropertyName("account_seed")] public string AccountSeed { get; set; }

    public static KeysFile Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"keys.json not found: {path}. 请先在微信运行时提取数据库密钥（微信必须保持登录）。");
        return JsonSerializer.Deserialize<KeysFile>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
    }
}
