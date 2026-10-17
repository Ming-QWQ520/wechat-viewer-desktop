using System;
using WeChatViewer.Desktop.Models;

namespace WeChatViewer.Desktop.ViewModels;

/// <summary>Session list item view model.</summary>
public class SessionViewModel : ViewModelBase
{
    public SessionInfo Session { get; }

    public SessionViewModel(SessionInfo session) => Session = session;

    public string Username => Session.Username;
    public string Name => Session.Name;
    public string Alias => Session.Alias;
    public bool IsGroup => Session.IsGroup;
    public bool HasUnread => Session.Unread > 0;
    public long Unread => Session.Unread;
    public string Summary => Session.Summary ?? "";
    public string TimeText => Session.LastTimestamp > 0 ? ListTime(Session.LastTimestamp) : "";
    public string GroupTag => IsGroup ? "群" : "";

    private static string Pad2(long n) => n.ToString("00");

    private static string ListTime(long sec)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
        var now = DateTime.Now;
        if (now.Date == d.Date) return $"{Pad2(d.Hour)}:{Pad2(d.Minute)}";
        return d.Year == now.Year ? $"{d.Month}/{d.Day}" : $"{d.Year}/{d.Month}/{d.Day}";
    }

    public void Refresh() => OnPropertyChanged(nameof(TimeText));
}

/// <summary>
/// Message view model. Also represents day separators (IsDaySeparator) so the
/// messages list can mix them like the original web UI does.
/// </summary>
public class MessageViewModel : ViewModelBase
{
    public Message Message { get; }

    private MessageViewModel(Message message) => Message = message;

    public static MessageViewModel From(Message message) => new(message);

    public static MessageViewModel DaySeparator(string label) =>
        new(null) { _dayLabel = label };

    private string _dayLabel;
    public string DayLabel => _dayLabel ?? "";

    public bool IsDaySeparator => Message == null;

    public bool IsSystemLike => Message is { Kind: "system" or "pat" };

    public string Kind => Message?.Kind ?? "daysep";
    public long ServerIdAsLong => long.TryParse(Message?.ServerId, out var v) ? v : 0;
    public string ServerId => Message?.ServerId ?? "";
    public bool IsSelf => Message?.IsSelf ?? false;
    public string SenderName => Message?.SenderName ?? "";
    public string SenderUsername => Message?.SenderUsername ?? "";
    public long Time => Message?.Time ?? 0;
    public string TimeText => Time > 0 ? Clock(Time) : "";

    private static string Pad2(long n) => n.ToString("00");

    public static string Clock(long sec)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
        return $"{Pad2(d.Hour)}:{Pad2(d.Minute)}";
    }

    public static string DayKey(long sec)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
        return $"{d.Year}-{Pad2(d.Month)}-{Pad2(d.Day)}";
    }

    public static string DayLabelFor(long sec)
    {
        var d = DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime;
        var today = DateTime.Today;
        if (d.Date == today) return "今天";
        if (d.Date == today.AddDays(-1)) return "昨天";
        return $"{d.Year}年{d.Month}月{d.Day}日";
    }

    private bool _isHighlighted;
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    private object _avatarSource;
    /// <summary>BitmapImage once loaded (null → colored initial fallback).</summary>
    public object AvatarSource
    {
        get => _avatarSource;
        set => SetProperty(ref _avatarSource, value);
    }

    public override string ToString() => IsDaySeparator ? $"--- {DayLabel} ---" : $"{Kind}: {Message?.Text}";
}
