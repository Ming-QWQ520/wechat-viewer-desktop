using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WeChatViewer.Desktop.Models;
using WeChatViewer.Desktop.Services;

namespace WeChatViewer.Desktop.ViewModels;

/// <summary>
/// Application orchestration: bootstrap (key extraction + decryption),
/// sessions, chat pagination, search, quote jumps, sticker completion,
/// export. UI-framework free: UI marshaling via runOnUiThread delegate.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Action<Action> _runOnUiThread;
    private readonly string _dataDir;

    private WeChatData _data;
    private ExportService _exporter;
    private DatabaseDecryptor _decryptor;
    private List<SessionViewModel> _allSessions = new();
    private string _currentChat;
    private MessageCursor _cursor;
    private bool _hasMore;
    private bool _loading;
    private bool _searchActive;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public MainViewModel(Action<Action> runOnUiThread)
    {
        _runOnUiThread = runOnUiThread;
        _dataDir = NativeTools.DataDir;
        Messages = new ObservableCollection<MessageViewModel>();
        Sessions = new ObservableCollection<SessionViewModel>();
    }

    public ObservableCollection<SessionViewModel> Sessions { get; }
    public ObservableCollection<MessageViewModel> Messages { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    public string CurrentChat => _currentChat;

    private SessionViewModel _currentSession;
    public SessionViewModel CurrentSession => _currentSession;

    public bool HasData => _data != null;
    public bool HasImageKey => _data?.ImageAesKey != null;
    public string SelfUsername => _data?.SelfUsername ?? "";
    public string AccountDir => _data?.AccountDir ?? "";

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(NotBusy));
            }
        }
    }

    public bool NotBusy => !_busy;

    private string _busyText;
    public string BusyText
    {
        get => _busyText;
        set => SetProperty(ref _busyText, value);
    }

    private bool _bootstrapNeeded;
    public bool BootstrapNeeded
    {
        get => _bootstrapNeeded;
        private set => SetProperty(ref _bootstrapNeeded, value);
    }

    private bool _searchResultsShown;
    public bool SearchResultsShown
    {
        get => _searchResultsShown;
        private set => SetProperty(ref _searchResultsShown, value);
    }

    private string _searchStatus = "";
    public string SearchStatus
    {
        get => _searchStatus;
        set => SetProperty(ref _searchStatus, value);
    }

    private bool _canBackToLatest;
    public bool CanBackToLatest
    {
        get => _canBackToLatest;
        private set => SetProperty(ref _canBackToLatest, value);
    }

    private string _chatTitle = "";
    public string ChatTitle
    {
        get => _chatTitle;
        private set => SetProperty(ref _chatTitle, value);
    }

    private string _chatSubtitle = "";
    public string ChatSubtitle
    {
        get => _chatSubtitle;
        private set => SetProperty(ref _chatSubtitle, value);
    }

    private string _accountLine = "";
    public string AccountLine
    {
        get => _accountLine;
        private set => SetProperty(ref _accountLine, value);
    }

    /// <summary>Loaded by the UI to convert avatar bytes into an ImageSource on the UI thread.</summary>
    public Func<string, Task<object>> AvatarImageLoader { get; set; }

    // ---------- logging ----------

    public void Log(string line)
    {
        _runOnUiThread(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 400) LogLines.RemoveAt(0);
            OnPropertyChanged(nameof(LogLines));
        });
    }

    // ---------- bootstrap ----------

    public bool KeysFileExists => File.Exists(Path.Combine(_dataDir, "keys.json"));

    public async Task InitializeAsync()
    {
        if (!KeysFileExists)
        {
            BootstrapNeeded = true;
            Log("[init] 未找到 data\\keys.json：请保持微信运行并已登录，然后点击“提取密钥并启动”。");
            return;
        }
        await RunInitPipelineAsync();
    }

    public async Task<bool> ExtractKeysAsync()
    {
        if (!NativeTools.HasTool(NativeTools.WeChatKeysExe))
        {
            Log("[keys] 缺少 tools\\wechat-keys.exe（请重新获取完整发行包）。");
            return false;
        }
        Log("[keys] 正在从运行中的微信进程提取数据库密钥与账户 seed（约 5-60 秒）…");
        Busy = true;
        BusyText = "正在提取密钥…";
        try
        {
            Directory.CreateDirectory(_dataDir);
            var result = await NativeTools.RunToolAsync(NativeTools.WeChatKeysExe,
                $"-out \"{Path.Combine(_dataDir, "keys.json")}\"", 300000);
            Log(result.Output.TrimEnd());
            if (!result.Success)
            {
                Log("[keys] 密钥提取失败：请确认微信已启动并已登录（必要时以管理员身份运行）。");
                return false;
            }
            Log("[keys] 密钥提取成功。");
            return true;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task RunInitPipelineAsync()
    {
        Busy = true;
        try
        {
            BusyText = "正在解密数据库…";
            Log("[init] 解密数据库（增量，已缓存的跳过）…");
            _decryptor = new DatabaseDecryptor(_dataDir);
            _decryptor.Log += Log;
            var stats = await Task.Run(() => _decryptor.DecryptAll());
            if (stats.Failed > 0 && stats.Decrypted == 0 && stats.Skipped == 0)
            {
                Log("[init] 解密失败：keys.json 可能过期。请在微信运行时点击“重新提取密钥”。");
                BootstrapNeeded = true;
                return;
            }

            BusyText = "正在加载数据…";
            _data?.Dispose();
            _data = await Task.Run(() => new WeChatData(_dataDir));
            _exporter = new ExportService(_data, Path.Combine(_dataDir, "media-cache"));
            _exporter.Progress += (done, total) => _runOnUiThread(() =>
            {
                BusyText = $"正在导出… {done}/{total}";
            });
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(HasImageKey));
            OnPropertyChanged(nameof(SelfUsername));
            OnPropertyChanged(nameof(AccountDir));
            Log($"[init] 账号: {SelfUsername} · 数据目录: {AccountDir}");
            BootstrapNeeded = false;
            await LoadSessionsAsync();
        }
        catch (Exception error)
        {
            Log($"[init] 初始化失败: {error.Message}");
            BootstrapNeeded = true;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task<bool> RefreshKeysAsync()
    {
        var ok = await ExtractKeysAsync();
        if (ok)
        {
            await RunInitPipelineAsync();
        }
        return ok;
    }

    // ---------- sessions ----------

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                RenderSessions();
            }
        }
    }

    public async Task LoadSessionsAsync()
    {
        var sessions = await Task.Run(() => _data.Sessions());
        _allSessions = sessions.Select(s => new SessionViewModel(s)).ToList();
        RenderSessions();
        OnPropertyChanged(nameof(HasImageKey));
    }

    private void RenderSessions()
    {
        _runOnUiThread(() =>
        {
            var filter = (_filter ?? "").Trim();
            Sessions.Clear();
            int shown = 0;
            foreach (var session in _allSessions)
            {
                if (session.Session.Hidden) continue;
                if (filter.Length > 0
                    && !session.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !session.Username.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Sessions.Add(session);
                shown++;
            }
            AccountLine = $"共 {_allSessions.Count} 个会话 · 本机只读";
            OnPropertyChanged(nameof(Sessions));
        });
    }

    // ---------- chat pagination ----------

    private static MessageViewModel WithDaySeparator(List<MessageViewModel> target, Message message, ref string lastDay)
    {
        var day = MessageViewModel.DayKey(message.Time);
        MessageViewModel vm = null;
        if (day != lastDay)
        {
            target.Add(ViewModels.MessageViewModel.DaySeparator(MessageViewModel.DayLabelFor(message.Time)));
            lastDay = day;
        }
        vm = MessageViewModel.From(message);
        target.Add(vm);
        return vm;
    }

    public async Task SelectSessionAsync(SessionViewModel session)
    {
        if (_loading || session == null) return;
        _currentChat = session.Username;
        _currentSession = session;
        _cursor = null;
        _hasMore = false;
        _searchActive = false;
        SearchResultsShown = false;
        CanBackToLatest = false;
        SearchStatus = "";
        ChatTitle = session.Name;
        ChatSubtitle = session.IsGroup ? "群聊" : (session.Alias is { Length: > 0 } ? session.Alias : session.Username);
        OnPropertyChanged(nameof(CurrentChat));
        OnPropertyChanged(nameof(CurrentSession));

        Messages.Clear();
        await LoadMessagesAsync(reset: true);
    }

    public async Task LoadMessagesAsync(bool reset)
    {
        if (_currentChat == null || _loading) return;
        await _loadLock.WaitAsync();
        if (reset) _loading = true;
        else if (_loading) { _loadLock.Release(); return; }
        try
        {
            var chat = _currentChat;
            var cursor = _cursor;
            var page = await Task.Run(() => _data.Messages(chat, cursor: cursor, limit: 60));
            if (chat != _currentChat) return;
            _hasMore = page.HasMore;
            _cursor = page.Cursor ?? _cursor;
            HasMoreMessages = page.HasMore;

            var items = new List<MessageViewModel>();
            string lastDay = null;
            if (!reset)
            {
                var firstReal = Messages.FirstOrDefault(m => !m.IsDaySeparator);
                if (firstReal != null) lastDay = MessageViewModel.DayKey(firstReal.Time);
            }
            foreach (var message in page.Messages)
            {
                WithDaySeparator(items, message, ref lastDay);
            }

            _runOnUiThread(() =>
            {
                if (reset)
                {
                    Messages.Clear();
                    foreach (var item in items) Messages.Add(item);
                }
                else
                {
                    for (int i = items.Count - 1; i >= 0; i--)
                    {
                        Messages.Insert(0, items[i]);
                    }
                }
                OnPropertyChanged(nameof(Messages));
            });
        }
        catch (Exception error)
        {
            Log($"[messages] 加载失败: {error.Message}");
        }
        finally
        {
            _loading = false;
            _loadLock.Release();
        }
    }

    private bool _hasMoreMessages;
    public bool HasMoreMessages
    {
        get => _hasMoreMessages;
        private set => SetProperty(ref _hasMoreMessages, value);
    }

    /// <summary>Called by the UI when the list is scrolled to the top.</summary>
    public Task LoadOlderAsync() => _searchActive ? Task.CompletedTask : LoadMessagesAsync(reset: false);

    /// <summary>Locate a message by server id and load a page around it (quote jump).</summary>
    public async Task<MessageViewModel> JumpToQuoteAsync(string serverId)
    {
        if (_currentChat == null || _loading || string.IsNullOrEmpty(serverId)) return null;
        await _loadLock.WaitAsync();
        _loading = true;
        try
        {
            var chat = _currentChat;
            var location = await Task.Run(() => _data.LocateMessageByServerId(chat, serverId));
            if (!location.Found)
            {
                SearchStatus = "未找到原消息（可能已删除）";
                return null;
            }
            var page = await Task.Run(() => _data.Messages(chat, before: location.Sort + 1, limit: 80));
            if (chat != _currentChat) return null;
            _searchActive = false;
            SearchResultsShown = false;
            CanBackToLatest = true;
            _cursor = page.Cursor;
            _hasMore = page.HasMore;
            HasMoreMessages = page.HasMore;
            var items = new List<MessageViewModel>();
            string lastDay = null;
            MessageViewModel target = null;
            foreach (var message in page.Messages)
            {
                var vm = WithDaySeparator(items, message, ref lastDay);
                if (message.ServerId == serverId)
                {
                    target = vm;
                }
            }
            _runOnUiThread(() =>
            {
                Messages.Clear();
                foreach (var item in items) Messages.Add(item);
                OnPropertyChanged(nameof(Messages));
            });
            SearchStatus = $"已定位到 {TextUtils.FormatDay(location.Time)} {MessageViewModel.Clock(location.Time)}";
            return target;
        }
        finally
        {
            _loading = false;
            _loadLock.Release();
        }
    }

    // ---------- search ----------

    public async Task RunSearchAsync(string query, string kind)
    {
        if (_currentChat == null || _loading) return;
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0 && string.IsNullOrEmpty(kind))
        {
            await ClearSearchAsync();
            return;
        }
        await _loadLock.WaitAsync();
        _loading = true;
        try
        {
            SearchStatus = "搜索中…";
            var chat = _currentChat;
            var result = await Task.Run(() => _data.SearchMessages(chat, trimmed,
                kind == null ? Array.Empty<string>() : new[] { kind }, 300));
            if (chat != _currentChat) return;
            _searchActive = true;
            SearchResultsShown = true;
            CanBackToLatest = false;
            var items = result.Messages.Select(MessageViewModel.From).ToList();
            _runOnUiThread(() =>
            {
                Messages.Clear();
                foreach (var item in items) Messages.Add(item);
                OnPropertyChanged(nameof(Messages));
            });
            var label = KindLabel(kind);
            SearchStatus = $"找到 {items.Count} 条（{label}）{(result.Limited ? "，仅显示前 300 条" : "")}";
        }
        finally
        {
            _loading = false;
            _loadLock.Release();
        }
    }

    public async Task ClearSearchAsync()
    {
        _searchActive = false;
        SearchResultsShown = false;
        SearchStatus = "";
        CanBackToLatest = false;
        _cursor = null;
        await LoadMessagesAsync(reset: true);
    }

    public static string KindLabel(string kind) => kind switch
    {
        "text" => "文字",
        "image" => "图片",
        "voice" => "语音",
        "video" => "视频",
        "file" => "文件",
        "sticker" => "表情",
        "link" => "链接",
        "quote" => "引用",
        "chatRecord" => "聊天记录",
        "pat" => "拍一拍",
        "system" => "系统",
        null or "" => "全部",
        _ => kind,
    };

    // ---------- stickers ----------

    private volatile bool _stickerTaskRunning;

    public bool StickerTaskRunning => _stickerTaskRunning;

    private double _stickerProgress;
    public double StickerProgress
    {
        get => _stickerProgress;
        private set => SetProperty(ref _stickerProgress, value);
    }

    private string _stickerStatus = "";
    public string StickerStatus
    {
        get => _stickerStatus;
        private set => SetProperty(ref _stickerStatus, value);
    }

    public async Task CompleteStickersAsync()
    {
        if (_currentChat == null || _stickerTaskRunning) return;
        _stickerTaskRunning = true;
        OnPropertyChanged(nameof(StickerTaskRunning));
        StickerStatus = "准备中…";
        try
        {
            var chat = _currentChat;
            var pending = new Dictionary<string, string>();
            await Task.Run(() =>
            {
                _data.AllMessages(chat, page =>
                {
                    foreach (var message in page)
                    {
                        if (message.Kind != "sticker") continue;
                        var md5 = (message.Sticker?.Md5 ?? "").ToLowerInvariant();
                        var url = message.Sticker?.CdnUrl;
                        if (!System.Text.RegularExpressions.Regex.IsMatch(md5, "^[0-9a-f]{32}$") || string.IsNullOrEmpty(url)) continue;
                        if (_data.ReadSticker(md5) != null) continue;
                        if (!pending.ContainsKey(md5)) pending[md5] = url;
                    }
                });
            });
            int total = pending.Count;
            int done = 0, ok = 0, failed = 0;
            StickerStatus = $"补全中 0/{total}";
            using var semaphore = new SemaphoreSlim(6);
            var tasks = pending.Select(async kv =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var result = await _data.DownloadStickerAsync(kv.Key, kv.Value);
                    Interlocked.Increment(ref done);
                    if (result != null) Interlocked.Increment(ref ok);
                    else Interlocked.Increment(ref failed);
                    StickerProgress = total > 0 ? done * 100.0 / total : 100;
                    StickerStatus = $"补全中 {done}/{total}";
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks);
            StickerStatus = $"补全完成 {ok} 成功 / {failed} 失败";
            Log($"[stickers] {chat}: {ok} downloaded, {failed} failed");
        }
        catch (Exception error)
        {
            StickerStatus = "补全失败: " + error.Message;
        }
        finally
        {
            _stickerTaskRunning = false;
            OnPropertyChanged(nameof(StickerTaskRunning));
        }
    }

    // ---------- export ----------

    public string ExportFilename(string format) => _exporter?.ExportChatFilename(_currentChat, format);

    public Task ExportHtmlAsync(Stream stream) => _exporter.ExportChatHtmlAsync(_currentChat, stream);

    public async Task<string> ExportTxtAsync() => await _exporter.ExportChatTxtAsync(_currentChat);

    public async Task<string> ExportCsvAsync() => await _exporter.ExportChatCsvAsync(_currentChat);

    // ---------- misc ----------

    public bool HasServerId(MessageViewModel vm)
        => vm?.Message?.ServerId is { Length: > 0 };

    // ----- resolvers exposed for the UI layer -----

    public Task<DecodedMedia> ResolveAvatarAsync(string username)
        => _data != null && username != null ? _data.ResolveAvatarAsync(username) : Task.FromResult<DecodedMedia>(null);

    public async Task<string> ResolveVoiceAsync(MessageViewModel vm)
    {
        if (_data == null || vm?.Message?.Voice == null || _currentChat == null) return null;
        try
        {
            return await _data.GetVoiceWavAsync(_currentChat, vm.Message.Voice);
        }
        catch
        {
            return null;
        }
    }

    public byte[] ResolveImageBytes(string chat, string localName)
    {
        try
        {
            return _data?.ReadBestImage(chat, localName)?.Buffer;
        }
        catch
        {
            return null;
        }
    }

    public byte[] ResolveStickerBytes(string md5, string cdnUrl)
    {
        try
        {
            var sticker = _data?.ReadSticker(md5);
            if (sticker == null && cdnUrl is { Length: > 0 } && _data != null)
            {
                sticker = _data.DownloadStickerAsync(md5, cdnUrl).GetAwaiter().GetResult();
            }
            return sticker?.Buffer;
        }
        catch
        {
            return null;
        }
    }

    public string ResolveVideoThumb(string localName)
        => _data != null ? _data.FindVideoAssets(localName).Thumb : null;

    public string ResolveVideoPath(string localName)
        => _data != null ? _data.FindVideoAssets(localName).Video : null;

    public string ResolveFile(string name)
        => _data?.FindFile(name);

    public void Dispose()
    {
        _data?.Dispose();
    }
}
