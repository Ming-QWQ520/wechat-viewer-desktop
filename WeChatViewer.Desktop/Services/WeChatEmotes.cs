using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WeChatViewer.Desktop.Services;

/// <summary>
/// 微信经典小表情代码（[微笑] 等）→ Unicode emoji 的近似映射，
/// 渲染时使用；数据库中的原文保持不变（导出仍为 [微笑] 原文）。
/// 无映射的表情代码原样保留。
/// </summary>
public static partial class WeChatEmotes
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["微笑"] = "🙂", ["撇嘴"] = "🙁", ["色"] = "😍", ["发呆"] = "😶",
        ["得意"] = "😏", ["流泪"] = "😢", ["害羞"] = "☺️", ["闭嘴"] = "🤐",
        ["睡"] = "😴", ["大哭"] = "😭", ["尴尬"] = "😳", ["发怒"] = "😠",
        ["调皮"] = "😝", ["呲牙"] = "😁", ["惊讶"] = "😮", ["难过"] = "😔",
        ["囧"] = "😵", ["抓狂"] = "🤯", ["吐"] = "🤮", ["偷笑"] = "🤭",
        ["可爱"] = "🥰", ["白眼"] = "🙄", ["傲慢"] = "😤", ["饥饿"] = "😋",
        ["困"] = "😪", ["惊恐"] = "😱", ["流汗"] = "😅", ["憨笑"] = "🤣",
        ["大兵"] = "🪖", ["奋斗"] = "💪", ["咒骂"] = "🤬", ["疑问"] = "❓",
        ["嘘"] = "🤫", ["晕"] = "💫", ["疯了"] = "🤪", ["衰"] = "😞",
        ["骷髅"] = "💀", ["敲打"] = "👊", ["再见"] = "👋", ["擦汗"] = "💦",
        ["抠鼻"] = "👃", ["鼓掌"] = "👏", ["坏笑"] = "😏", ["右哼哼"] = "😤",
        ["左哼哼"] = "😤", ["鄙视"] = "👎", ["委屈"] = "🥺", ["快哭了"] = "🥲",
        ["阴险"] = "😈", ["亲亲"] = "😘", ["吓"] = "😱", ["可怜"] = "🥺",
        ["菜刀"] = "🔪", ["西瓜"] = "🍉", ["啤酒"] = "🍺", ["篮球"] = "🏀",
        ["乒乓"] = "🏓", ["咖啡"] = "☕", ["饭"] = "🍜", ["猪头"] = "🐷",
        ["玫瑰"] = "🌹", ["凋谢"] = "🥀", ["嘴唇"] = "💋", ["爱心"] = "❤️",
        ["心碎"] = "💔", ["蛋糕"] = "🎂", ["闪电"] = "⚡", ["炸弹"] = "💥",
        ["刀"] = "🔪", ["足球"] = "⚽", ["瓢虫"] = "🐞", ["便便"] = "💩",
        ["月亮"] = "🌙", ["太阳"] = "☀️", ["礼物"] = "🎁", ["拥抱"] = "🤗",
        ["强"] = "👍", ["弱"] = "🆘", ["握手"] = "🤝", ["胜利"] = "✌️",
        ["抱拳"] = "🙏", ["勾引"] = "👉", ["拳头"] = "✊", ["OK"] = "👌",
        ["合十"] = "🙏", ["啤酒花"] = "🍻", ["咖啡花"] = "☕", ["怄火"] = "🔥",
        ["转圈"] = "🌀", ["跳跳"] = "🕺", ["发抖"] = "🥶", ["对联"] = "🧧",
        ["红包"] = "🧧", ["发红包"] = "🧧", ["庆祝"] = "🎉", ["鞭炮"] = "🧨",
        ["翻白眼"] = "🙄", ["让我看看"] = "👀", ["叹气"] = "😮‍💨", ["苦涩"] = "😖",
        ["裂开"] = "🤦", ["狗"] = "🐶", ["doge"] = "🐶", ["月亮见"] = "🌙",
        ["口罩"] = "😷", ["加油"] = "⛽", ["点赞"] = "👍", ["喝茶"] = "🍵",
        ["吃瓜"] = "🍉", ["敲锣"] = "🥁", ["笑脸"] = "😄", ["委屈巴巴"] = "🥺",
    };

    [GeneratedRegex("\\[([^\\[\\]\\n]{1,12})\\]")]
    private static partial Regex EmoteRegex();

    /// <summary>把文本中的 [微信表情代码] 替换为对应 Unicode emoji（无映射保留原文）。</summary>
    public static string Replace(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0) return text;
        return EmoteRegex().Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            return Map.TryGetValue(name, out var emoji) ? emoji : m.Value;
        });
    }

    /// <summary>
    /// 拆分一段文本为 (type, value) 序列：type=emote 的 value 是 emoji 字符，
    /// type=text 的是普通文本；供 Inline 逐段渲染。
    /// </summary>
    public static IEnumerable<(string Type, string Value)> Tokenize(string text)
    {
        var source = text ?? "";
        int last = 0;
        foreach (Match match in EmoteRegex().Matches(source))
        {
            var name = match.Groups[1].Value;
            if (!Map.TryGetValue(name, out var emoji)) continue;
            if (match.Index > last) yield return ("text", source[last..match.Index]);
            yield return ("emote", emoji);
            last = match.Index + match.Value.Length;
        }
        if (last < source.Length) yield return ("text", source[last..]);
    }
}
