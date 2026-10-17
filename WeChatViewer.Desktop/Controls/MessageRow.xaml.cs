using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WeChatViewer.Desktop.Models;
using WeChatViewer.Desktop.Services;
using WeChatViewer.Desktop.ViewModels;

namespace WeChatViewer.Desktop.Controls;

/// <summary>
/// Message renderer, ported from app/public/app.js renderMessage(): day
/// separators, system/pat center lines and WeChat-style bubbles for text,
/// image, voice, video, file, link, sticker, quote, chatRecord, location,
/// card, voip and other messages.
/// </summary>
public sealed partial class MessageRow : UserControl
{
    private MessageViewModel _vm;
    private Border _bubbleBorder;
    private Storyboard _voiceStoryboard;

    /// <summary>Set by MainWindow: whether the current chat is a group chat.</summary>
    public static bool CurrentChatIsGroup;

    public MessageRow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateMaxWidth();
        SizeChanged += (_, _) => UpdateMaxWidth();
        Unloaded += (_, _) => Detach();
    }

    private void Detach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _voiceStoryboard?.Stop();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        Detach();
        _vm = DataContext as MessageViewModel;
        Build();
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageViewModel.IsPlaying))
        {
            if (_vm is { IsPlaying: true }) _voiceStoryboard?.Begin();
            else _voiceStoryboard?.Stop();
        }
        else if (e.PropertyName == nameof(MessageViewModel.IsHighlighted))
        {
            if (_bubbleBorder != null && _vm != null)
            {
                _bubbleBorder.BorderBrush = _vm.IsHighlighted
                    ? new SolidColorBrush(new Windows.UI.Color { A = 255, R = 7, G = 193, B = 96 })
                    : null;
                _bubbleBorder.BorderThickness = _vm.IsHighlighted ? new Thickness(2) : new Thickness(0);
            }
        }
    }

    private void UpdateMaxWidth()
    {
        if (Root is Grid grid && grid.Children.Count > 0 && grid.Children[0] is FrameworkElement content)
        {
            var wrap = FindChildByName(content, "Wrap") as FrameworkElement;
            if (wrap != null)
            {
                wrap.MaxWidth = Math.Max(300, ActualWidth * 0.66);
            }
        }
    }

    private static FrameworkElement FindChildByName(FrameworkElement root, string name)
    {
        if (root?.Name == name) return root;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
            {
                var found = FindChildByName(child, name);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static TextBlock CenteredText(string text, double opacity = 1.0)
    {
        return new TextBlock
        {
            Text = text ?? "",
            FontSize = 12,
            Opacity = opacity,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["WxMutedBrush"] as Brush,
            Margin = new Thickness(0, 10, 0, 10),
        };
    }

    private void Build()
    {
        _bubbleBorder = null;
        _voiceStoryboard?.Stop();
        _voiceStoryboard = null;
        Root.Children.Clear();
        if (_vm == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        if (_vm.IsDaySeparator)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = Application.Current.Resources["WxDaySepBrush"] as Brush,
                Padding = new Thickness(12, 4, 12, 5),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 6),
                Child = new TextBlock
                {
                    Text = _vm.DayLabel,
                    FontSize = 12,
                    Foreground = Application.Current.Resources["WxMutedBrush"] as Brush,
                },
            };
            Root.Children.Add(pill);
            return;
        }

        var message = _vm.Message;
        if (_vm.IsSystemLike)
        {
            Root.Children.Add(CenteredText(message.Kind == "pat" ? message.Text ?? "拍了拍" : message.System ?? ""));
            return;
        }

        Root.Children.Add(BuildMessageRow(message));
        UpdateMaxWidth();
    }

    private FrameworkElement BuildMessageRow(Message message)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = BuildAvatar(message);
        var wrap = BuildWrap(message);
        wrap.Name = "Wrap";

        if (message.IsSelf)
        {
            wrap.HorizontalAlignment = HorizontalAlignment.Right;
            avatar.SetValue(Grid.ColumnProperty, 2);
            wrap.SetValue(Grid.ColumnProperty, 1);
        }
        else
        {
            wrap.HorizontalAlignment = HorizontalAlignment.Left;
            avatar.SetValue(Grid.ColumnProperty, 0);
            wrap.SetValue(Grid.ColumnProperty, 1);
        }
        grid.Children.Add(avatar);
        grid.Children.Add(wrap);
        return grid;
    }

    private FrameworkElement BuildAvatar(Message message)
    {
        var border = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(UiHelpers.AvatarColor(message.SenderName)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var panel = new Grid();
        var initial = new TextBlock
        {
            Text = UiHelpers.Initial(message.SenderName),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(initial);
        panel.Children.Add(image);
        border.Child = panel;

        _ = LoadAvatarAsync(message.SenderUsername, message.SenderName, initial, image);
        return border;
    }

    private async Task LoadAvatarAsync(string username, string fallbackName, TextBlock initial, Image image)
    {
        var resolver = MessageInteractions.AvatarResolver;
        if (resolver == null || string.IsNullOrEmpty(username) || username == "unknown") return;
        try
        {
            var source = await resolver(username);
            if (source == null) return;
            var currentVm = _vm;
            if (currentVm == null || currentVm.SenderUsername != username) return;
            image.Source = source as ImageSource;
            image.Visibility = Visibility.Visible;
            initial.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // keep the colored initial fallback
        }
    }

    private StackPanel BuildWrap(Message message)
    {
        var wrap = new StackPanel { Spacing = 0 };

        bool isGroup = CurrentChatIsGroup;
        if (isGroup && !message.IsSelf)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = message.SenderName,
                FontSize = 12,
                Margin = new Thickness(2, 0, 0, 4),
                Foreground = Application.Current.Resources["WxNameBrush"] as Brush,
            });
        }

        var bubble = BuildBubble(message);
        wrap.Children.Add(bubble);

        if (message.Kind == "quote" && message.Quote != null)
        {
            wrap.Children.Add(BuildQuoteBlock(message));
        }

        wrap.Children.Add(new TextBlock
        {
            Text = MessageViewModel.Clock(message.Time),
            FontSize = 10,
            Margin = new Thickness(2, 3, 2, 0),
            Foreground = Application.Current.Resources["WxTimeBrush"] as Brush,
            HorizontalAlignment = message.IsSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        });

        return wrap;
    }

    private Border BuildBubble(Message message)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = message.Kind switch
            {
                "image" => new Thickness(3),
                "sticker" => new Thickness(0),
                _ => new Thickness(11, 9, 11, 9),
            },
            Background = message.Kind is "image" or "sticker"
                ? null
                : message.IsSelf
                    ? Application.Current.Resources["WxBubbleOutBrush"] as Brush
                    : Application.Current.Resources["WxBubbleInBrush"] as Brush,
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 460,
        };
        border.Child = BuildBubbleContent(message);
        _bubbleBorder ??= border;
        return border;
    }

    private FrameworkElement BuildBubbleContent(Message message)
    {
        switch (message.Kind)
        {
            case "text":
            case "other":
            case "voip":
                return BuildTextContent(message);
            case "image":
                return BuildImageContent(message);
            case "voice":
                return BuildVoiceContent(message);
            case "video":
                return BuildVideoContent(message);
            case "file":
                return BuildFileCard("📄", message.File?.Name ?? "文件", TextUtils.FormatBytes(message.File?.Size ?? 0), () =>
                {
                    var file = _fileResolver?.Invoke(message);
                    if (file != null && System.IO.File.Exists(file)) NativeTools.ShellOpen(file);
                });
            case "link":
                return BuildLinkCard(message);
            case "sticker":
                return BuildStickerContent(message);
            case "chatRecord":
                return BuildFileCard("💬", message.Link?.Title ?? message.Text ?? "聊天记录", "聊天记录", null);
            case "location":
                return new TextBlock
                {
                    Text = $"[位置] {message.Location?.Label ?? ""}{(message.Location?.PoiName is { Length: > 0 } p ? " · " + p : "")}",
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Application.Current.Resources["WxTextBrush"] as Brush,
                };
            case "card":
                return BuildFileCard("👤", message.Card?.Nickname ?? message.Card?.Username ?? "名片", message.Card?.Username ?? "", null);
            default:
                return BuildTextContent(message);
        }
    }

    private FrameworkElement BuildTextContent(Message message)
    {
        var block = new TextBlock
        {
            FontSize = 14,
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = Application.Current.Resources["WxTextBrush"] as Brush,
        };
        string text = message.Kind switch
        {
            "voip" => $"[通话] {message.Text ?? ""}",
            "quote" => string.IsNullOrEmpty(message.Text) ? "引用消息" : message.Text,
            _ => string.IsNullOrEmpty(message.Text) ? "[消息]" : message.Text,
        };
        foreach (var (type, value) in TextUtils.SplitUrls(text))
        {
            if (type == "url")
            {
                var link = new Microsoft.UI.Xaml.Documents.Hyperlink
                {
                    NavigateUri = new Uri(value),
                };
                link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value });
                link.Foreground = Application.Current.Resources["WxLinkBrush"] as Brush;
                block.Inlines.Add(link);
            }
            else
            {
                block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = value });
            }
        }
        return block;
    }

    private FrameworkElement BuildImageContent(Message message)
    {
        var localName = message.Image?.LocalName;
        if (string.IsNullOrEmpty(localName))
        {
            return new TextBlock { Text = "[图片]", FontSize = 14 };
        }
        var image = new Image
        {
            MaxWidth = 240,
            MaxHeight = 320,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var placeholder = new TextBlock
        {
            Text = "图片加载中…",
            FontSize = 13,
            Foreground = Application.Current.Resources["WxMutedBrush"] as Brush,
            Margin = new Thickness(4, 2, 4, 2),
        };
        var panel = new Grid { MinWidth = 60 };
        panel.Children.Add(placeholder);
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Child = image,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        image.Tapped += (_, _) =>
        {
            if (image.Source is ImageSource source)
            {
                MessageInteractions.RaiseImageLightbox(_vm, source);
            }
        };
        _ = Task.Run(async () =>
        {
            try
            {
                var chat = _chatResolver?.Invoke();
                var bytes = _imageBytesResolver?.Invoke(chat, localName);
                if (bytes == null)
                {
                    ShowPlaceholder(placeholder, "[图片]");
                    return;
                }
                var bitmap = await UiHelpers.BitmapFromBytesAsync(bytes);
                if (bitmap == null)
                {
                    ShowPlaceholder(placeholder, "[图片]");
                    return;
                }
                _dispatcher?.TryEnqueue(() =>
                {
                    if (_vm == null || placeholder.Parent == null) return;
                    image.Source = bitmap;
                    placeholder.Visibility = Visibility.Collapsed;
                });
            }
            catch
            {
                ShowPlaceholder(placeholder, "[图片]");
            }
        });
        return panel;
    }

    private static void ShowPlaceholder(TextBlock placeholder, string text)
    {
        RunOnUiThread(() => placeholder.Text = text);
    }

    private static void RunOnUiThread(System.Action action) => _dispatcherSafeSetter?.Invoke(action);

    private FrameworkElement BuildVoiceContent(Message message)
    {
        var seconds = Math.Max(1, (long)Math.Round((message.Voice?.DurationMs ?? 0) / 1000.0));
        var width = Math.Min(240, 64 + seconds * 6);

        var wave = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bars = new Rectangle[3];
        double[] heights = { 8, 14, 11 };
        for (int i = 0; i < 3; i++)
        {
            var bar = new Rectangle
            {
                Width = 3,
                Height = heights[i],
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.Black),
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1),
            };
            var scale = new ScaleTransform { ScaleY = 1 };
            bar.RenderTransform = scale;
            bars[i] = bar;
            wave.Children.Add(bar);
        }

        var duration = new TextBlock
        {
            Text = $"{seconds}″",
            FontSize = 13,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var button = new Button
        {
            Content = null,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = width,
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
        };
        contentPanel.Children.Add(wave);
        contentPanel.Children.Add(duration);
        button.Content = contentPanel;

        ToolTipService.SetToolTip(button, "点击播放语音");

        // Waveform animation (mirrors @keyframes voicePulse in the original CSS).
        _voiceStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        double[] delays = { 0, 0.15, 0.3 };
        for (int i = 0; i < 3; i++)
        {
            var animation = new DoubleAnimation
            {
                From = 0.45,
                To = 1.15,
                Duration = new Duration(TimeSpan.FromSeconds(0.7)),
                AutoReverse = true,
                BeginTime = TimeSpan.FromSeconds(delays[i]),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(animation, bars[i]);
            Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
            _voiceStoryboard.Children.Add(animation);
        }
        button.Tapped += async (_, _) =>
        {
            if (_vm != null) await VoicePlayerService.ToggleAsync(_vm);
        };

        return button;
    }

    private FrameworkElement BuildVideoContent(Message message)
    {
        var card = new Grid { Width = 240 };
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            MaxHeight = 180,
        };
        var placeholder = new Border
        {
            Height = 140,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 51, G = 51, B = 51 }),
            Child = new TextBlock
            {
                Text = "▶",
                FontSize = 28,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        card.Children.Add(placeholder);
        card.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Child = image,
        });
        var badge = new Border
        {
            Background = new SolidColorBrush(new Windows.UI.Color { A = 153, R = 0, G = 0, B = 0 }),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = $"{message.Video?.DurationSec ?? 0}″",
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        card.Children.Add(badge);
        var localName = message.Video?.LocalName;
        if (!string.IsNullOrEmpty(localName))
        {
            _ = Task.Run(async () =>
            {
                var thumbPath = _videoThumbResolver?.Invoke(localName);
                if (thumbPath == null) return;
                var bitmap = await UiHelpers.GetFileImageAsync(thumbPath);
                if (bitmap == null) return;
                RunOnUiThread(() =>
                {
                    if (_vm == null) return;
                    image.Source = bitmap;
                    placeholder.Visibility = Visibility.Collapsed;
                });
            });
        }
        var tapTarget = new ContentControl { Content = card, IsTabStop = false };
        tapTarget.Tapped += (_, _) => MessageInteractions.RaiseVideoOpen(_vm);
        return tapTarget;
    }

    private FrameworkElement BuildFileCard(string icon, string name, string sub, Action action)
    {
        var card = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            MinWidth = 180,
        };
        card.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 24,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280,
        });
        body.Children.Add(new TextBlock
        {
            Text = sub ?? "",
            FontSize = 12,
            Foreground = Application.Current.Resources["WxMutedBrush"] as Brush,
            Margin = new Thickness(0, 2, 0, 0),
        });
        card.Children.Add(body);
        var button = new Button
        {
            Content = card,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (action != null)
        {
            button.Tapped += (_, _) => action();
        }
        return button;
    }

    private FrameworkElement BuildLinkCard(Message message)
    {
        var body = new StackPanel { MinWidth = 220, MaxWidth = 320 };
        body.Children.Add(new TextBlock
        {
            Text = message.Link?.Title ?? "链接",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (!string.IsNullOrEmpty(message.Link?.Description))
        {
            body.Children.Add(new TextBlock
            {
                Text = message.Link.Description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["WxNameBrush"] as Brush,
            });
        }
        if (!string.IsNullOrEmpty(message.Link?.Url))
        {
            body.Children.Add(new TextBlock
            {
                Text = message.Link.Url,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Application.Current.Resources["WxMutedBrush"] as Brush,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        var button = new Button
        {
            Content = body,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var url = message.Link?.Url;
        if (!string.IsNullOrEmpty(url))
        {
            button.Tapped += (_, _) => NativeTools.ShellOpen(url);
        }
        return button;
    }

    private FrameworkElement BuildStickerContent(Message message)
    {
        var md5 = message.Sticker?.Md5 ?? message.Sticker?.ThumbMd5;
        if (string.IsNullOrEmpty(md5))
        {
            return new TextBlock { Text = "[表情]", FontSize = 15 };
        }
        var image = new Image
        {
            MaxWidth = 130,
            MaxHeight = 130,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var placeholder = new TextBlock
        {
            Text = "[表情]",
            FontSize = 15,
        };
        var panel = new Grid { MinWidth = 60, MinHeight = 24 };
        panel.Children.Add(placeholder);
        panel.Children.Add(image);
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = _stickerBytesResolver?.Invoke(md5, message.Sticker?.CdnUrl);
                if (bytes == null) return;
                var bitmap = await UiHelpers.BitmapFromBytesAsync(bytes);
                if (bitmap == null) return;
                RunOnUiThread(() =>
                {
                    if (_vm == null) return;
                    image.Source = bitmap;
                    placeholder.Visibility = Visibility.Collapsed;
                });
            }
            catch { }
        });
        return panel;
    }

    private FrameworkElement BuildQuoteBlock(Message message)
    {
        var panel = new StackPanel { Spacing = 0 };
        var quote = message.Quote;
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(quote?.Sender) ? "" : $"{quote.Sender}：",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = message.IsSelf
                ? new SolidColorBrush(new Windows.UI.Color { A = 255, R = 242, G = 242, B = 242 })
                : Application.Current.Resources["WxTextBrush"] as Brush,
        });
        var textBlock = new TextBlock
        {
            Text = quote?.Text ?? "",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = message.IsSelf
                ? new SolidColorBrush(new Windows.UI.Color { A = 255, R = 242, G = 242, B = 242 })
                : Application.Current.Resources["WxQuoteTextBrush"] as Brush,
        };
        panel.Children.Add(textBlock);

        var border = new Border
        {
            Background = message.IsSelf
                ? Application.Current.Resources["WxQuoteOutBrush"] as Brush
                : Application.Current.Resources["WxQuoteInBrush"] as Brush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 6, 0, 0),
            Child = panel,
            HorizontalAlignment = message.IsSelf ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 440,
        };
        if (!string.IsNullOrEmpty(quote?.SvrId))
        {
            ToolTipService.SetToolTip(border, "点击定位到原消息");
            border.Tapped += (_, _) => MessageInteractions.RaiseQuoteRequested(_vm);
        }
        return border;
    }

    // ----- resolvers wired by MainWindow -----

    private static System.Action<System.Action> _dispatcherSafeSetter;

    public static Func<string> _chatResolver;
    public static Func<string, string, byte[]> _imageBytesResolver;
    public static Func<string, string, byte[]> _stickerBytesResolver;
    public static Func<string, string> _videoThumbResolver;
    public static Func<Message, string> _fileResolver;
    public static Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public static void Configure(
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
        Func<string> chatResolver,
        Func<string, string, byte[]> imageBytesResolver,
        Func<string, string, string, byte[]> stickerBytesResolver,
        Func<string, string> videoThumbResolver,
        Func<Message, string> fileResolver)
    {
        _dispatcher = dispatcher;
        _chatResolver = chatResolver;
        _imageBytesResolver = imageBytesResolver;
        _stickerBytesResolver = stickerBytesResolver;
        _videoThumbResolver = videoThumbResolver;
        _fileResolver = fileResolver;
        _dispatcherSafeSetter = a => dispatcher.TryEnqueue(() => a());
    }
}
