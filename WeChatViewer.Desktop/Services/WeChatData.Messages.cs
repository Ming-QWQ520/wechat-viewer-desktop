using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WeChatViewer.Desktop.Models;

namespace WeChatViewer.Desktop.Services;

public sealed partial class WeChatData
{
    // ---------- message tables ----------

    private List<ShardRef> LocateMessageShards(string username)
    {
        if (_msgLocator.TryGetValue(username, out var cached)) return cached;
        var table = "Msg_" + TextUtils.Md5Hex(username);
        var found = new List<ShardRef>();
        foreach (var shard in new[]
                 {
                     "message/message_0.db", "message/message_1.db",
                     "message/message_2.db", "message/message_3.db",
                 })
        {
            if (!File.Exists(Path.Combine(_decDir, shard))) continue;
            var db = Db(shard);
            var row = Query(db, "select name from sqlite_master where type = 'table' and name = ?", table);
            if (row.Count > 0) found.Add(new ShardRef(shard, db, table));
        }
        _msgLocator[username] = found;
        return found;
    }

    private Dictionary<long, string> NameMap(string shard)
    {
        if (!_name2id.TryGetValue(shard, out var map))
        {
            map = new Dictionary<long, string>();
            var db = Db(shard);
            foreach (var row in Query(db, "select rowid as id, user_name from Name2Id"))
            {
                map[ToLong(row["id"])] = row["user_name"] as string;
            }
            _name2id[shard] = map;
        }
        return map;
    }

    private const string MessageColumns =
        "local_id, local_type, sort_seq, real_sender_id, create_time, message_content, " +
        "compress_content, source, packed_info_data, CAST(server_id AS TEXT) AS server_id_text";

    public MessagePage Messages(string username, long? before = null, int limit = 60, MessageCursor cursor = null)
    {
        var shards = LocateMessageShards(username);
        if (shards.Count == 0) return new MessagePage { Messages = new(), HasMore = false, Cursor = null };

        var cursors = cursor ?? new MessageCursor();
        var merged = new List<(Dictionary<string, object> Row, string Shard)>();
        bool shardHasMore = false;

        foreach (var shardRef in shards)
        {
            long[] position = cursors.TryGetValue(shardRef.Shard, out var p) && p is { Length: 2 } ? p : null;
            List<Dictionary<string, object>> rows;
            if (position != null)
            {
                rows = Query(shardRef.Db,
                    $"select {MessageColumns} from \"{shardRef.Table}\" " +
                    "where (sort_seq < ? or (sort_seq = ? and local_id < ?)) " +
                    "order by sort_seq desc, local_id desc limit ?",
                    position[0], position[0], position[1], limit + 1);
            }
            else if (before != null)
            {
                rows = Query(shardRef.Db,
                    $"select {MessageColumns} from \"{shardRef.Table}\" " +
                    "where sort_seq < ? order by sort_seq desc, local_id desc limit ?",
                    before.Value, limit + 1);
            }
            else
            {
                rows = Query(shardRef.Db,
                    $"select {MessageColumns} from \"{shardRef.Table}\" " +
                    "order by sort_seq desc, local_id desc limit ?", limit + 1);
            }
            if (rows.Count > limit) shardHasMore = true;
            foreach (var row in rows) merged.Add((row, shardRef.Shard));
        }

        merged.Sort((a, b) =>
        {
            int cmp = ToLong(b.Row["sort_seq"]).CompareTo(ToLong(a.Row["sort_seq"]));
            return cmp != 0 ? cmp : ToLong(b.Row["local_id"]).CompareTo(ToLong(a.Row["local_id"]));
        });

        bool hasMore = shardHasMore || merged.Count > limit;
        var page = merged.Take(limit).ToList();
        var next = new MessageCursor();
        foreach (var kv in cursors) next[kv.Key] = kv.Value;
        var emitted = new Dictionary<string, long[]>();
        foreach (var (row, shard) in page)
        {
            var position = new[] { ToLong(row["sort_seq"]), ToLong(row["local_id"]) };
            if (!emitted.TryGetValue(shard, out var previous)
                || position[0] < previous[0]
                || (position[0] == previous[0] && position[1] < previous[1]))
            {
                emitted[shard] = position;
            }
        }
        foreach (var kv in emitted) next[kv.Key] = kv.Value;

        var names = new Dictionary<string, Dictionary<long, string>>();
        var ordered = page.Select(entry =>
        {
            if (!names.TryGetValue(entry.Shard, out var nm))
            {
                nm = NameMap(entry.Shard);
                names[entry.Shard] = nm;
            }
            return ParseMessage(entry.Row, username, nm);
        }).ToList();
        ordered.Reverse();
        return new MessagePage { Messages = ordered, HasMore = hasMore, Cursor = next };
    }

    /// <summary>Stream all messages oldest-first (mirrors allMessages in wechat.mjs).</summary>
    public void AllMessages(string username, Action<List<Message>> onPage)
    {
        var pages = new List<List<Message>>();
        MessageCursor cursor = null;
        for (; ; )
        {
            var page = Messages(username, cursor: cursor, limit: 800);
            if (page.Messages.Count == 0) break;
            pages.Add(page.Messages);
            cursor = page.Cursor;
            if (!page.HasMore) break;
        }
        for (int i = pages.Count - 1; i >= 0; i--) onPage(pages[i]);
    }

    // ---------- search ----------

    private List<Message> SearchIndex(string username)
    {
        if (_searchIndexes.TryGetValue(username, out var cached))
        {
            // LRU refresh
            _searchIndexes.Remove(username);
            _searchIndexes[username] = cached;
            return cached;
        }
        var list = new List<Message>();
        AllMessages(username, page => list.AddRange(page));
        _searchIndexes[username] = list;
        while (_searchIndexes.Count > 2)
        {
            var oldest = _searchIndexes.Keys.First();
            _searchIndexes.Remove(oldest);
        }
        return list;
    }

    private static string MessageSearchText(Message message)
    {
        var parts = new List<string> { message.Text ?? "", message.SenderName ?? "" };
        if (message.Link != null) parts.AddRange(new[] { message.Link.Title, message.Link.Description, message.Link.Url });
        if (message.File != null) parts.Add(message.File.Name);
        if (message.Card != null) parts.AddRange(new[] { message.Card.Nickname, message.Card.Username });
        if (message.Location != null) parts.AddRange(new[] { message.Location.Label, message.Location.PoiName });
        if (message.Quote != null) parts.AddRange(new[] { message.Quote.Sender, message.Quote.Text });
        return string.Join("\n", parts.Where(p => !string.IsNullOrEmpty(p))).ToLowerInvariant();
    }

    public SearchResult SearchMessages(string username, string query = "", IList<string> types = null, int limit = 200)
    {
        var q = TextUtils.NormalizeText(query).ToLowerInvariant();
        var kinds = new HashSet<string>((types ?? Array.Empty<string>()).Where(t => !string.IsNullOrEmpty(t)));
        var index = SearchIndex(username);
        var results = new List<Message>();
        bool limited = false;
        for (int i = index.Count - 1; i >= 0; i--)
        {
            var message = index[i];
            if (kinds.Count > 0 && !kinds.Contains(message.Kind)) continue;
            if (q.Length > 0 && !MessageSearchText(message).Contains(q, StringComparison.Ordinal)) continue;
            if (results.Count >= limit)
            {
                limited = true;
                break;
            }
            results.Add(message);
        }
        return new SearchResult { Messages = results, Limited = limited };
    }

    /// <summary>Resolve a quoted message's svrid to its position in this chat.</summary>
    public LocateResult LocateMessageByServerId(string username, string serverId)
    {
        var sid = (serverId ?? "").Trim();
        if (!Regex.IsMatch(sid, @"^\d+$")) return new LocateResult { Found = false };
        foreach (var shard in LocateMessageShards(username))
        {
            try
            {
                var row = Query(shard.Db,
                    $"select local_id, sort_seq, create_time, CAST(server_id AS TEXT) as sid from \"{shard.Table}\" " +
                    "where CAST(server_id AS TEXT) = ? limit 1", sid);
                if (row.Count > 0)
                {
                    return new LocateResult
                    {
                        Found = true,
                        Sort = ToLong(row[0]["sort_seq"]),
                        LocalId = ToLong(row[0]["local_id"]),
                        Time = ToLong(row[0]["create_time"]),
                        Shard = shard.Shard,
                    };
                }
            }
            catch
            {
                // table without server_id on older schemas
            }
        }
        return new LocateResult { Found = false };
    }

    // ---------- message parsing ----------

    private Message ParseMessage(Dictionary<string, object> row, string chatUsername, Dictionary<long, string> names)
    {
        var (baseType, subType) = TextUtils.TypeParts(ToLong(row["local_type"]));
        var senderUsername =
            names.TryGetValue(ToLong(row["real_sender_id"]), out var name) && name != null
                ? name
                : chatUsername.EndsWith("@chatroom", StringComparison.Ordinal) ? "unknown" : chatUsername;
        var isSelf = senderUsername == SelfUsername;
        var senderDisplay = SenderName(senderUsername);
        var raw = TextUtils.DecodeContentBuffer(row["message_content"]);
        var content = raw;
        if (chatUsername.EndsWith("@chatroom", StringComparison.Ordinal))
        {
            var m = Regex.Match(raw, @"^([A-Za-z0-9_@.\-]{4,64}):\n");
            if (m.Success) content = raw.Substring(m.Value.Length);
        }

        var message = new Message
        {
            Id = ToLong(row["local_id"]),
            Sort = ToLong(row["sort_seq"]),
            Time = ToLong(row["create_time"]),
            ServerId = row["server_id_text"] as string ?? "",
            Type = baseType,
            SubType = subType,
            SenderUsername = senderUsername,
            SenderName = senderDisplay,
            IsSelf = isSelf,
            Kind = "other",
            Text = "",
            RawType = ToLong(row["local_type"]),
        };

        switch (baseType)
        {
            case 1:
                message.Kind = "text";
                message.Text = TextUtils.NormalizeText(content);
                break;
            case 3:
            {
                message.Kind = "image";
                message.Image = new ImagePayload
                {
                    Md5 = TextUtils.GetAttr(content, "md5"),
                    LocalName = TextUtils.LocalMediaName(row["packed_info_data"] as byte[]),
                    ThumbWidth = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "cdnthumbwidth"))),
                    ThumbHeight = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "cdnthumbheight"))),
                    Length = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "length"))),
                    HdLength = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "hdlength"))),
                };
                break;
            }
            case 34:
                message.Kind = "voice";
                message.Voice = new VoicePayload
                {
                    DurationMs = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "voicelength"))),
                    LocalId = ToLong(row["local_id"]),
                    CreateTime = ToLong(row["create_time"]),
                };
                break;
            case 42:
                message.Kind = "card";
                message.Card = new CardPayload
                {
                    Username = TextUtils.GetAttr(content, "username") ?? TextUtils.GetTag(content, "username"),
                    Nickname = TextUtils.GetAttr(content, "nickname"),
                    Avatar = TextUtils.GetAttr(content, "bigheadimgurl"),
                    Region = TextUtils.GetAttr(content, "region"),
                };
                break;
            case 43:
                message.Kind = "video";
                message.Video = new VideoPayload
                {
                    LocalName = TextUtils.LocalMediaName(row["packed_info_data"] as byte[]),
                    DurationSec = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "playlength"))),
                    Length = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "length"))),
                };
                break;
            case 47:
                message.Kind = "sticker";
                message.Sticker = new StickerPayload
                {
                    Md5 = TextUtils.GetAttr(content, "md5"),
                    ThumbMd5 = TextUtils.GetAttr(content, "cdnthumbmd5") ?? "",
                    Length = ToLong(ParseLongOrNull(TextUtils.GetAttr(content, "len"))),
                    CdnUrl = TextUtils.HtmlUnescape(TextUtils.GetAttr(content, "cdnurl") ?? ""),
                };
                break;
            case 48:
                message.Kind = "location";
                message.Location = new LocationPayload
                {
                    X = TextUtils.GetAttr(content, "x"),
                    Y = TextUtils.GetAttr(content, "y"),
                    Label = TextUtils.HtmlUnescape(TextUtils.GetAttr(content, "label") ?? ""),
                    PoiName = TextUtils.HtmlUnescape(TextUtils.GetAttr(content, "poiname") ?? ""),
                };
                break;
            case 49:
                ParseAppMessage(message, content);
                break;
            case 50:
            {
                message.Kind = "voip";
                var cdata = TextUtils.GetCdata(content, "msg") ?? TextUtils.StripXml(content);
                message.Text = string.IsNullOrEmpty(cdata) ? "通话" : cdata;
                break;
            }
            case 10000:
            {
                message.Kind = "system";
                string text = content;
                var template = TextUtils.GetCdata(content, "template");
                if (!string.IsNullOrEmpty(template))
                {
                    text = Regex.Replace(template, @"\$[a-zA-Z0-9_]+\$", "");
                    foreach (Match link in Regex.Matches(content, @"<link[^>]*>[\s\S]*?<title>([\s\S]*?)</title>"))
                    {
                        text = Regex.Replace(text, @"\$[a-zA-Z0-9_]+\$",
                            $"[{TextUtils.HtmlUnescape(TextUtils.StripXml(link.Groups[1].Value))}]");
                    }
                }
                else
                {
                    text = TextUtils.StripXml(content);
                }
                message.System = TextUtils.HtmlUnescape(string.IsNullOrEmpty(text) ? "系统消息" : text);
                message.Text = message.System;
                break;
            }
            default:
                message.Kind = "other";
                message.Text = TextUtils.StripXml(content);
                if (string.IsNullOrEmpty(message.Text)) message.Text = $"[消息类型 {baseType}]";
                break;
        }
        return message;
    }

    private static long? ParseLongOrNull(string value)
        => long.TryParse(value, out var result) ? result : null;

    private void ParseAppMessage(Message message, string content)
    {
        var app = content;
        long appType = ToLong(ParseLongOrNull(TextUtils.GetTag(app, "type")) ?? 0);
        var title = TextUtils.HtmlUnescape(TextUtils.StripXml(TextUtils.GetTag(app, "title") ?? ""));
        var des = TextUtils.HtmlUnescape(TextUtils.StripXml(TextUtils.GetTag(app, "des") ?? ""));
        var url = TextUtils.HtmlUnescape(TextUtils.GetTag(app, "url") ?? "");
        var lowurl = TextUtils.HtmlUnescape(TextUtils.GetTag(app, "lowurl") ?? "");
        message.Link = null;

        // 引用回复：title 是回复内容，<refermsg> 里是被引用的消息。
        if (appType == 57)
        {
            var replyText = TextUtils.NormalizeText(title);
            if (string.IsNullOrEmpty(replyText)) replyText = TextUtils.NormalizeText(des);
            if (string.IsNullOrEmpty(replyText)) replyText = "引用消息";
            message.Kind = "quote";
            message.Text = replyText;
            var referMatch = Regex.Match(app, @"<refermsg>([\s\S]*?)</refermsg>");
            var referXml = referMatch.Success ? referMatch.Groups[1].Value : "";
            long referType = ToLong(ParseLongOrNull(TextUtils.GetTag(referXml, "type")) ?? 0);
            var rawRefer = TextUtils.GetTag(referXml, "content") ?? "";
            string quoted;
            if (referType == 1)
            {
                quoted = TextUtils.HtmlUnescape(TextUtils.StripXml(rawRefer));
                if (Regex.IsMatch(quoted, @"^\s*<(\?xml|msg|appmsg)", RegexOptions.IgnoreCase)) quoted = "[消息]";
                quoted = TextUtils.NormalizeText(quoted);
            }
            else if (referType == 3) quoted = "[图片]";
            else if (referType == 34) quoted = "[语音]";
            else if (referType == 43) quoted = "[视频]";
            else if (referType == 47) quoted = "[表情]";
            else if (referType == 42) quoted = "[名片]";
            else if (referType == 48)
            {
                var xml = TextUtils.HtmlUnescape(rawRefer);
                var label = TextUtils.HtmlUnescape(TextUtils.GetAttr(xml, "label") ?? TextUtils.GetAttr(xml, "poiname") ?? "");
                quoted = label.Length > 0 ? $"[位置] {label}" : "[位置]";
            }
            else if (referType == 49)
            {
                var xml = TextUtils.HtmlUnescape(rawRefer);
                var referAppType = ToLong(ParseLongOrNull(TextUtils.GetTag(xml, "type")) ?? 0);
                var referTitle = TextUtils.HtmlUnescape(TextUtils.StripXml(TextUtils.GetTag(xml, "title") ?? ""));
                string label = "[链接]";
                if (referAppType == 6) label = "[文件]";
                else if (referAppType == 19) label = "[聊天记录]";
                else if (referAppType == 33 || referAppType == 36) label = "[小程序]";
                else if (referAppType == 57) label = "[引用]";
                quoted = referTitle.Length > 0 ? $"{label} {referTitle}" : label;
            }
            else if (referType == 50) quoted = "[通话]";
            else if (referType == 62) quoted = "[拍一拍]";
            else if (referType == 10000) quoted = "[系统消息]";
            else
            {
                var text = TextUtils.HtmlUnescape(TextUtils.StripXml(rawRefer));
                quoted = Regex.IsMatch(text, @"</?[a-z][^>]*>", RegexOptions.IgnoreCase) || Regex.IsMatch(text, @"^\s*<\?xml")
                    ? "[消息]"
                    : text;
                quoted = Regex.Replace(quoted, @"[\s\u00a0\u2005\u3000]+", " ").Trim();
            }
            message.Quote = new QuotePayload
            {
                Sender = TextUtils.HtmlUnescape(TextUtils.StripXml(TextUtils.GetTag(referXml, "displayname") ?? "")),
                Text = quoted,
                SvrId = (TextUtils.GetTag(referXml, "svrid") ?? "").Trim(),
                CreateTime = ToLong(ParseLongOrNull(TextUtils.GetTag(referXml, "createtime")) ?? 0),
                ChatUser = TextUtils.HtmlUnescape(TextUtils.StripXml(TextUtils.GetTag(referXml, "chatusr") ?? "")),
            };
            return;
        }

        // 拍一拍：appmsg type 62，不是文件。
        if (appType == 62)
        {
            message.Kind = "pat";
            var patText = TextUtils.NormalizeText(title);
            message.Text = string.IsNullOrEmpty(patText) ? "拍了拍" : patText;
            return;
        }

        // 只有 type 6 才是文件附件；其他 appmsg 也可能带空 <appattach>。
        if (appType == 6)
        {
            message.Kind = "file";
            message.File = new FilePayload
            {
                Name = TextUtils.NormalizeText(title),
                Size = ToLong(ParseLongOrNull(TextUtils.GetTag(app, "totllen")) ?? 0),
                Ext = TextUtils.GetTag(app, "fileext") ?? "",
                Url = url is { Length: > 0 } ? url : lowurl,
            };
            if (string.IsNullOrEmpty(message.File.Name)) message.File.Name = "未命名文件";
            return;
        }

        if (appType == 19)
        {
            message.Kind = "chatRecord";
            var recordTitle = TextUtils.NormalizeText(title);
            if (string.IsNullOrEmpty(recordTitle)) recordTitle = "[聊天记录]";
            message.Text = recordTitle;
            message.Link = new LinkPayload
            {
                Title = recordTitle,
                Description = TextUtils.NormalizeText(des),
                Url = url is { Length: > 0 } ? url : lowurl,
                AppType = appType,
            };
            return;
        }

        message.Kind = "link";
        message.Link = new LinkPayload
        {
            Title = TextUtils.NormalizeText(title) is { Length: > 0 } t1 ? t1
                : TextUtils.NormalizeText(des) is { Length: > 0 } t2 ? t2 : "链接",
            Description = TextUtils.NormalizeText(des),
            Url = url is { Length: > 0 } ? url : lowurl,
            AppType = appType,
        };
    }
}
