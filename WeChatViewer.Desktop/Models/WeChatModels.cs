using System;
using System.Collections.Generic;

namespace WeChatViewer.Desktop.Models;

/// <summary>Contact display info (contact table row).</summary>
public class ContactInfo
{
    public string Remark { get; set; } = "";
    public string Nick { get; set; } = "";
    public string Alias { get; set; } = "";
    public string BigHeadUrl { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsSelf { get; set; }
}

/// <summary>A row of the session list (SessionTable).</summary>
public class SessionInfo
{
    public string Username { get; set; }
    public string Name { get; set; }
    public string Alias { get; set; } = "";
    public bool IsGroup { get; set; }
    public bool IsSelf { get; set; }
    public long Unread { get; set; }
    public bool Hidden { get; set; }
    public string Summary { get; set; } = "";
    public string Draft { get; set; } = "";
    public long LastTimestamp { get; set; }
    public long SortTimestamp { get; set; }
}

/// <summary>Image attachment info parsed from message xml.</summary>
public class ImagePayload
{
    public string Md5 { get; set; }
    public string LocalName { get; set; }
    public long ThumbWidth { get; set; }
    public long ThumbHeight { get; set; }
    public long Length { get; set; }
    public long HdLength { get; set; }
}

public class VoicePayload
{
    public long DurationMs { get; set; }
    public long LocalId { get; set; }
    public long CreateTime { get; set; }
}

public class VideoPayload
{
    public string LocalName { get; set; }
    public long DurationSec { get; set; }
    public long Length { get; set; }
}

public class FilePayload
{
    public string Name { get; set; }
    public long Size { get; set; }
    public string Ext { get; set; } = "";
    public string Url { get; set; } = "";
}

public class LinkPayload
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public long AppType { get; set; }
}

public class StickerPayload
{
    public string Md5 { get; set; }
    public string ThumbMd5 { get; set; } = "";
    public long Length { get; set; }
    public string CdnUrl { get; set; } = "";
}

public class LocationPayload
{
    public string X { get; set; }
    public string Y { get; set; }
    public string Label { get; set; } = "";
    public string PoiName { get; set; } = "";
}

public class CardPayload
{
    public string Username { get; set; }
    public string Nickname { get; set; }
    public string Avatar { get; set; }
    public string Region { get; set; }
}

public class QuotePayload
{
    public string Sender { get; set; } = "";
    public string Text { get; set; } = "";
    public string SvrId { get; set; } = "";
    public long CreateTime { get; set; }
    public string ChatUser { get; set; } = "";
}

/// <summary>
/// Parsed message. Kind is one of: text, image, voice, video, file, link,
/// sticker, quote, chatRecord, pat, location, card, voip, system, other.
/// </summary>
public class Message
{
    public long Id { get; set; }
    public long Sort { get; set; }
    public long Time { get; set; }
    public string ServerId { get; set; } = "";
    public long Type { get; set; }
    public long SubType { get; set; }
    public string SenderUsername { get; set; }
    public string SenderName { get; set; }
    public bool IsSelf { get; set; }
    public string Kind { get; set; } = "other";
    public string Text { get; set; } = "";

    public LinkPayload Link { get; set; }
    public ImagePayload Image { get; set; }
    public VoicePayload Voice { get; set; }
    public VideoPayload Video { get; set; }
    public FilePayload File { get; set; }
    public StickerPayload Sticker { get; set; }
    public LocationPayload Location { get; set; }
    public CardPayload Card { get; set; }
    public QuotePayload Quote { get; set; }
    public string System { get; set; }
    public long RawType { get; set; }
}

/// <summary>Keyset pagination cursor: shard relative path -> (sort_seq, local_id).</summary>
public class MessageCursor : Dictionary<string, long[]>, IEquatable<MessageCursor>
{
    public MessageCursor() { }

    public static MessageCursor Parse(string json)
    {
        try
        {
            var result = new MessageCursor();
            if (string.IsNullOrWhiteSpace(json)) return result;
            var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Array && prop.Value.GetArrayLength() == 2)
                {
                    var arr = new long[2];
                    int i = 0;
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        arr[i++] = item.GetInt64();
                    }
                    result[prop.Name] = arr;
                }
            }
            return result;
        }
        catch
        {
            return new MessageCursor();
        }
    }

    public string ToJson()
    {
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var kv in this)
            {
                writer.WriteStartArray(kv.Key);
                writer.WriteNumberValue(kv.Value[0]);
                writer.WriteNumberValue(kv.Value[1]);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public bool Equals(MessageCursor other) => other != null && Count == other.Count && ToString().Equals(other.ToString());
}

public class MessagePage
{
    public List<Message> Messages { get; set; } = new();
    public bool HasMore { get; set; }
    public MessageCursor Cursor { get; set; }
}

public class SearchResult
{
    public List<Message> Messages { get; set; } = new();
    public bool Limited { get; set; }
}

public class LocateResult
{
    public bool Found { get; set; }
    public long Sort { get; set; }
    public long LocalId { get; set; }
    public long Time { get; set; }
    public string Shard { get; set; }
}

public class DecodedMedia
{
    public byte[] Buffer { get; set; }
    public string ContentType { get; set; }
    public string Variant { get; set; }
    public bool Preview { get; set; }
    public string File { get; set; }
}
