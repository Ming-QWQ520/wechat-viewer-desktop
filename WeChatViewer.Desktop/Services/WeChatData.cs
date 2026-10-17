using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WeChatViewer.Desktop.Models;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// Data layer ported from app/wechat.mjs: sessions, contacts, messages,
/// avatars, media resolution, search. Read-only access to decrypted databases.
/// </summary>
public sealed partial class WeChatData : IDisposable
{
    private readonly string _dataDir;
    private readonly string _decDir;
    private readonly string _dbDir;
    public string ImageAesKey { get; }
    public int ImageXorKey { get; }
    public byte[] EmoticonAesKey { get; }
    public string AccountSeed { get; }
    public string AccountDir { get; }
    private readonly string _emoticonDir;
    private readonly string _msgDir;
    public string SelfUsername { get; private set; }
    private bool _selfResolved;

    private readonly Dictionary<string, SqliteConnection> _dbs = new();
    private readonly Dictionary<string, ContactInfo> _contacts = new();
    private readonly Dictionary<string, Dictionary<long, string>> _name2id = new();
    private readonly Dictionary<string, Dictionary<string, long>> _mediaName2id = new();
    private string[] _mediaDbRels;
    private readonly Dictionary<string, List<ShardRef>> _msgLocator = new();
    private readonly Dictionary<string, DecodedMedia> _avatarCache = new();
    private readonly Dictionary<string, DecodedMedia> _stickerCache = new();
    private readonly string _stickerDownloadDir;
    private readonly string _stickerThumbDir;
    private readonly string _avatarMediaDir;
    private readonly Dictionary<string, string[]> _subdirCache = new();
    private readonly Dictionary<string, (string[] Files, DateTime Time)> _imageCandidateCache = new();
    private (Dictionary<string, string> Thumb, Dictionary<string, string> Video)? _videoIndex;
    private (Dictionary<string, string> Exact, List<(string Name, string File)> All)? _fileIndex;
    private readonly Dictionary<string, List<Message>> _searchIndexes = new();
    private readonly string _imgcheckPath;
    private readonly string _validationFile;
    private readonly string _validationDir;
    private readonly Dictionary<string, bool> _validationCache = new();

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    });

    public class ShardRef
    {
        public string Shard { get; }
        public SqliteConnection Db { get; }
        public string Table { get; }

        /// <summary>消息表是否带 is_sender 列（1=自己发送），用于直接判定消息方向。</summary>
        public bool HasIsSender { get; set; }

        public ShardRef(string shard, SqliteConnection db, string table) { Shard = shard; Db = db; Table = table; }
    }

    public WeChatData(string dataDir)
    {
        _dataDir = dataDir;
        _decDir = Path.Combine(dataDir, "decrypted");
        var keys = KeysFile.Load(Path.Combine(dataDir, "keys.json"));
        _dbDir = keys.DbDir;
        ImageAesKey = keys.ImageAesKey;
        ImageXorKey = keys.ImageXorKey != 0 ? keys.ImageXorKey : 0x8f;
        EmoticonAesKey = string.IsNullOrEmpty(keys.EmoticonAesKey) ? null : SqlCipherDecryptor.HexToBytes(keys.EmoticonAesKey);
        AccountSeed = keys.AccountSeed;
        AccountDir = Path.GetDirectoryName(_dbDir);
        _emoticonDir = Path.Combine(AccountDir, "business", "emoticon");
        _msgDir = Path.Combine(AccountDir, "msg");
        _stickerDownloadDir = Path.Combine(dataDir, "media-cache", "stickers");
        _stickerThumbDir = Path.Combine(dataDir, "media-cache", "sticker-thumbs");
        _avatarMediaDir = Path.Combine(dataDir, "media-cache", "avatars");
        _imgcheckPath = NativeTools.ImgCheckExe;
        _validationFile = Path.Combine(dataDir, "media-cache", "image-validation.json");
        _validationDir = Path.Combine(dataDir, "media-cache", "imgcheck");
        try
        {
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(_validationFile));
            if (saved != null)
            {
                foreach (var kv in saved) _validationCache[kv.Key] = kv.Value;
            }
        }
        catch
        {
            // first run
        }
        SelfUsername = DetectSelf();
        LoadContacts();
    }

    private SqliteConnection Db(string rel)
    {
        if (!_dbs.TryGetValue(rel, out var conn))
        {
            var file = Path.Combine(_decDir, rel);
            if (!File.Exists(file))
                throw new FileNotFoundException($"解密后的数据库缺失: {rel}（上一步解密未生成该文件）");
            conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            }.ToString());
            conn.Open();
            _dbs[rel] = conn;
        }
        return conn;
    }

    private static List<Dictionary<string, object>> Query(SqliteConnection conn, string sql, params object[] args)
    {
        var rows = new List<Dictionary<string, object>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = RewritePlaceholders(sql, args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$p{i}", args[i] ?? DBNull.Value);
        }
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Microsoft.Data.Sqlite 无法绑定匿名 ? 占位符（AddWithValue("?") 会抛
    /// “Must add values for the following parameters”），因此把 SQL 里的 ? 依序改写为
    /// 命名参数 $p0..$pN。单引号字符串字面量内的 ? 原样保留。
    /// </summary>
    internal static string RewritePlaceholders(string sql, int count)
    {
        if (count == 0 || string.IsNullOrEmpty(sql) || sql.IndexOf('?') < 0) return sql;
        var sb = new StringBuilder(sql.Length + count * 3);
        int index = 0, i = 0;
        while (i < sql.Length)
        {
            char ch = sql[i];
            if (ch == '\'')
            {
                int end = sql.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    sb.Append(sql, i, sql.Length - i);
                    break;
                }
                sb.Append(sql, i, end - i + 1); // 字符串字面量原样保留
                i = end + 1;
                continue;
            }
            if (ch == '?' && index < count)
            {
                sb.Append("$p").Append(index++);
                i++;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 自我账号候选列表：账户目录形如 <wxid>_<4位hex>。wxid 本身可能含下划线，
    /// 因此除首选正则外再提供“剥掉末尾 _<hex4>”与“目录原名”两个候选，
    /// 并在加载 Name2Id 后用真实数据校验（ResolveSelfFromNames）。
    /// </summary>
    public List<string> SelfCandidates()
    {
        var baseName = Path.GetFileName(AccountDir);
        var list = new List<string>();
        var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^(wxid_[A-Za-z0-9]+)_[0-9a-f]{4}$");
        if (m.Success) list.Add(m.Groups[1].Value);
        var m2 = System.Text.RegularExpressions.Regex.Match(baseName, @"^(.+)_([0-9a-f]{4})$");
        if (m2.Success && m2.Groups[1].Value.Length > 0) list.Add(m2.Groups[1].Value);
        list.Add(baseName);
        return list.Distinct().ToList();
    }

    private string DetectSelf() => SelfCandidates()[0];

    /// <summary>用 Name2Id 的真实映射校验自我识别；识别错误会导致自己发的消息全部左对齐。</summary>
    internal void ResolveSelfFromNames(Dictionary<long, string> map)
    {
        if (_selfResolved || map.Count == 0) return;
        _selfResolved = true;
        if (map.ContainsValue(SelfUsername)) return;
        foreach (var candidate in SelfCandidates())
        {
            if (map.ContainsValue(candidate))
            {
                StartupLogger.Log($"[init] 自我识别修正: {SelfUsername} → {candidate}（依据 Name2Id 映射）");
                SelfUsername = candidate;
                return;
            }
        }
    }

    private void LoadContacts()
    {
        var db = Db("contact/contact.db");
        var rows = Query(db, "select username, remark, nick_name, alias, big_head_url from contact");
        foreach (var row in rows)
        {
            var username = row["username"] as string;
            if (string.IsNullOrEmpty(username)) continue;
            _contacts[username] = new ContactInfo
            {
                Remark = row["remark"] as string ?? "",
                Nick = row["nick_name"] as string ?? "",
                Alias = row["alias"] as string ?? "",
                BigHeadUrl = row["big_head_url"] as string ?? "",
            };
        }
    }

    public ContactInfo ContactInfo(string username)
    {
        if (username == SelfUsername)
        {
            _contacts.TryGetValue(username, out var info);
            info ??= new ContactInfo();
            return new ContactInfo
            {
                Remark = info.Remark, Nick = info.Nick, Alias = info.Alias, BigHeadUrl = info.BigHeadUrl,
                DisplayName = "我", IsSelf = true,
            };
        }
        if (_contacts.TryGetValue(username, out var found))
        {
            return new ContactInfo
            {
                Remark = found.Remark, Nick = found.Nick, Alias = found.Alias, BigHeadUrl = found.BigHeadUrl,
                DisplayName = found.Remark is { Length: > 0 } ? found.Remark
                    : found.Nick is { Length: > 0 } ? found.Nick
                    : found.Alias is { Length: > 0 } ? found.Alias
                    : username,
                IsSelf = false,
            };
        }
        return new ContactInfo { Remark = "", Nick = "", Alias = "", DisplayName = username, IsSelf = false };
    }

    public string SenderName(string username) => ContactInfo(username).DisplayName;

    // ---------- sessions ----------

    public List<SessionInfo> Sessions()
    {
        var db = Db("session/session.db");
        var rows = Query(db,
            @"select username, type, unread_count, is_hidden, summary, draft,
                     last_timestamp, sort_timestamp, last_msg_locald_id, last_msg_type
              from SessionTable order by sort_timestamp desc");
        var result = new List<SessionInfo>();
        foreach (var row in rows)
        {
            var username = row["username"] as string;
            if (string.IsNullOrEmpty(username)) continue;
            var info = ContactInfo(username);
            result.Add(new SessionInfo
            {
                Username = username,
                Name = info.DisplayName,
                Alias = info.Alias ?? "",
                IsGroup = username.EndsWith("@chatroom", StringComparison.Ordinal),
                IsSelf = username == SelfUsername,
                Unread = ToLong(row["unread_count"]),
                Hidden = ToLong(row["is_hidden"]) == 1,
                Summary = row["summary"] as string ?? "",
                Draft = row["draft"] as string ?? "",
                LastTimestamp = ToLong(row["last_timestamp"]),
                SortTimestamp = ToLong(row["sort_timestamp"]),
            });
        }
        return result;
    }

    internal static long ToLong(object value)
    {
        return value switch
        {
            null => 0,
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            double d => (long)d,
            float f => (long)f,
            decimal m => (long)m,
            bool bo => bo ? 1 : 0,
            string str => long.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0,
            _ => 0,
        };
    }

    public void Dispose()
    {
        foreach (var conn in _dbs.Values) conn.Dispose();
        _dbs.Clear();
    }
}
