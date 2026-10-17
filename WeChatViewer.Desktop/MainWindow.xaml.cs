using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WeChatViewer.Desktop.Controls;
using WeChatViewer.Desktop.Services;
using WeChatViewer.Desktop.ViewModels;
using Windows.Storage.Pickers;

namespace WeChatViewer.Desktop;

/// <summary>Main window: sidebar + chat view + bootstrap + lightbox.</summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private ScrollViewer _messagesScroll;
    private bool _loadingOlder;
    private DispatcherQueueTimer _highlightTimer;
    private AppearanceSettings _appearance;
    private bool _suppressAppearanceEvents;
    private int _lastBlur = -1, _lastDim = -1, _lastPanel = -1;

    public MainWindow()
    {
        StartupLogger.Log("MainWindow..ctor: 创建 ViewModel");
        ViewModel = new MainViewModel(action => DispatcherQueue.TryEnqueue(() => action()));
        StartupLogger.Log("MainWindow..ctor: InitializeComponent 开始");
        InitializeComponent();
        StartupLogger.Log("MainWindow..ctor: XAML 加载完成");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        WireViewModel();
        WireInteractions();
        ConfigureResolvers();
        InitAppearance();

        LogList.ItemsSource = ViewModel.LogLines;
        ViewModel.LogLines.CollectionChanged += (_, _) => RunOnUi(() =>
        {
            LogScroller.UpdateLayout();
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, true);
        });
        SessionList.ItemsSource = ViewModel.Sessions;
        MessageList.ItemsSource = ViewModel.Messages;

        // Window has no Loaded event; hook the content root instead.
        ((FrameworkElement)Content).Loaded += OnLoaded;
        _ = InitializeAsync();
        StartupLogger.Log("MainWindow..ctor 完成");
    }

    private async Task InitializeAsync()
    {
        try
        {
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            StartupLogger.Log("数据初始化失败: " + ex);
            ViewModel.LogLines.Add("初始化失败: " + ex.Message);
        }
        RunOnUi(ApplyBootstrapVisibility);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _messagesScroll ??= FindScrollViewer(MessageList);
        if (_messagesScroll != null)
        {
            _messagesScroll.ViewChanged += OnMessagesScrollChanged;
        }
    }

    // ---------- ui-thread marshaling ----------

    /// <summary>Runs the action on the UI thread (inline if already there).</summary>
    private void RunOnUi(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    // ---------- view model wiring ----------

    private void WireViewModel()
    {
        // VM property changes may fire from worker threads; marshal to UI.
        ViewModel.PropertyChanged += (_, args) => RunOnUi(() =>
        {
            switch (args.PropertyName)
            {
                case nameof(MainViewModel.Busy):
                    BusyOverlay.Visibility = ViewModel.Busy && !ViewModel.BootstrapNeeded
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    BootstrapRing.Visibility = ViewModel.Busy && ViewModel.BootstrapNeeded
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    break;
                case nameof(MainViewModel.BusyText):
                    BusyTextBlock.Text = ViewModel.BusyText;
                    InitStatusText.Text = ViewModel.BootstrapNeeded ? ViewModel.BusyText : "";
                    break;
                case nameof(MainViewModel.BootstrapNeeded):
                    ApplyBootstrapVisibility();
                    break;
                case nameof(MainViewModel.AccountLine):
                    AccountLineText.Text = ViewModel.AccountLine;
                    break;
                case nameof(MainViewModel.SearchStatus):
                    SearchStatusText.Text = ViewModel.SearchStatus;
                    break;
                case nameof(MainViewModel.CanBackToLatest):
                    BackToLatestButton.Visibility = ViewModel.CanBackToLatest ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(MainViewModel.SearchResultsShown):
                    BackToChatButton.Visibility = ViewModel.SearchResultsShown ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case nameof(MainViewModel.HasImageKey):
                    ImageKeyBar.IsOpen = ViewModel.HasData && !ViewModel.HasImageKey;
                    break;
                case nameof(MainViewModel.StickerStatus):
                    StickerButtonText.Text = ViewModel.StickerStatus is { Length: > 0 } ? ViewModel.StickerStatus : "补全表情包";
                    break;
                case nameof(MainViewModel.StickerTaskRunning):
                    StickerButton.IsEnabled = !ViewModel.StickerTaskRunning;
                    break;
            }
        });
    }

    private void ApplyBootstrapVisibility()
    {
        BootstrapPanel.Visibility = ViewModel.BootstrapNeeded ? Visibility.Visible : Visibility.Collapsed;
        BootstrapRing.Visibility = ViewModel.Busy && ViewModel.BootstrapNeeded ? Visibility.Visible : Visibility.Collapsed;
        BusyOverlay.Visibility = ViewModel.Busy && !ViewModel.BootstrapNeeded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfigureResolvers()
    {
        MessageInteractions.AvatarResolver = username =>
        {
            if (ViewModel.AvatarImageLoader == null) return Task.FromResult<object>(null);
            return ViewModel.AvatarImageLoader(username);
        };

        ViewModel.AvatarImageLoader = async username =>
        {
            try
            {
                var media = await ViewModel.ResolveAvatarAsync(username);
                if (media?.Buffer == null) return null;
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        tcs.SetResult(await UiHelpers.BitmapFromBytesAsync(media.Buffer));
                    }
                    catch
                    {
                        tcs.SetResult(null);
                    }
                });
                return await tcs.Task;
            }
            catch
            {
                return null;
            }
        };

        VoicePlayerService.Configure(DispatcherQueue, vm => ViewModel.ResolveVoiceAsync(vm));

        MessageRow.Configure(
            DispatcherQueue,
            () => ViewModel.CurrentChat,
            (chat, localName) => ViewModel.ResolveImageBytes(chat, localName),
            message => ViewModel.ResolveStickerBytes(message),
            localName => ViewModel.ResolveVideoThumb(localName),
            message => ViewModel.ResolveFile(message.File?.Name));
    }

    // ---------- appearance (透明窗口 / 液态玻璃 / 自定义背景) ----------

    private void InitAppearance()
    {
        _appearance = AppearanceService.Load();
        _suppressAppearanceEvents = true;
        var modeIndex = _appearance.Mode switch { "mica" => 0, "image" => 2, _ => 1 };
        if (BackdropMode.Items.Count > modeIndex) BackdropMode.SelectedIndex = modeIndex;
        BlurSlider.Value = _appearance.Blur;
        DimSlider.Value = _appearance.Dim;
        PanelSlider.Value = _appearance.PanelOpacity;
        _suppressAppearanceEvents = false;
        AppearanceFlyout.Opening += (_, _) => SyncAppearanceControls();
        _ = ApplyAppearanceAsync(refreshLists: false);
    }

    private void SyncAppearanceControls()
    {
        _suppressAppearanceEvents = true;
        var modeIndex = _appearance.Mode switch { "mica" => 0, "image" => 2, _ => 1 };
        if (BackdropMode.Items.Count > modeIndex) BackdropMode.SelectedIndex = modeIndex;
        BlurSlider.Value = _appearance.Blur;
        DimSlider.Value = _appearance.Dim;
        PanelSlider.Value = _appearance.PanelOpacity;
        _suppressAppearanceEvents = false;
        ImageOptions.Visibility = _appearance.Mode == "image" ? Visibility.Visible : Visibility.Collapsed;
        PickBackgroundText.Text = string.IsNullOrEmpty(_appearance.ImagePath)
            ? "选择背景图片…"
            : "当前：" + Path.GetFileName(_appearance.ImagePath);
    }

    private async Task ApplyAppearanceAsync(bool refreshLists)
    {
        AppearanceService.Apply(_appearance, this);
        // 背景图层
        if (_appearance.Mode == "image" && !string.IsNullOrEmpty(_appearance.ImagePath))
        {
            var dim = (byte)Math.Round(255 * _appearance.Dim / 100.0);
            BackgroundDim.Background = new SolidColorBrush(new Windows.UI.Color { A = dim, R = 0, G = 0, B = 0 });
            try
            {
                var blurredPath = await AppearanceService.BuildBlurredImageAsync(_appearance.ImagePath, _appearance.Blur);
                var bitmap = new BitmapImage(new Uri(blurredPath));
                BackgroundImage.Source = bitmap;
                BackgroundLayer.Visibility = Visibility.Visible;
            }
            catch
            {
                BackgroundLayer.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            BackgroundLayer.Visibility = Visibility.Collapsed;
            BackgroundImage.Source = null;
        }

        if (refreshLists)
        {
            // 行渲染器在构建时读取画刷，切换外观后重建消息/会话行
            var selected = SessionList.SelectedItem;
            SessionList.ItemsSource = null;
            SessionList.ItemsSource = ViewModel.Sessions;
            MessageList.ItemsSource = null;
            MessageList.ItemsSource = ViewModel.Messages;
            if (selected != null && ReferenceEquals(selected, ViewModel.CurrentSession))
            {
                SessionList.SelectedItem = selected;
            }
            if (ViewModel.CurrentChat != null)
            {
                RunOnUi(ScrollToBottom); // 重建后保持阅读位置在最新消息
            }
        }
    }

    private void OnBackdropModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_suppressAppearanceEvents || BackdropMode.SelectedItem is not RadioButton item) return;
        var mode = item.Tag as string ?? "acrylic";
        if (mode == "image" && string.IsNullOrEmpty(_appearance.ImagePath))
        {
            // 尚未选图：先保持当前模式并弹图选择
            _suppressAppearanceEvents = true;
            BackdropMode.SelectedIndex = _appearance.Mode switch { "mica" => 0, _ => 1 };
            _suppressAppearanceEvents = false;
            _ = PickBackgroundAsync();
            return;
        }
        _appearance.Mode = mode;
        AppearanceService.Save(_appearance);
        ImageOptions.Visibility = mode == "image" ? Visibility.Visible : Visibility.Collapsed;
        _ = ApplyAppearanceAsync(refreshLists: true);
    }

    private void OnBackdropSliderChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_suppressAppearanceEvents || _appearance == null) return;
        _appearance.Blur = (int)BlurSlider.Value;
        _appearance.Dim = (int)DimSlider.Value;
        _appearance.PanelOpacity = (int)PanelSlider.Value;
        if (_appearance.Blur == _lastBlur && _appearance.Dim == _lastDim && _appearance.PanelOpacity == _lastPanel) return;
        _lastBlur = _appearance.Blur;
        _lastDim = _appearance.Dim;
        _lastPanel = _appearance.PanelOpacity;
        AppearanceService.Save(_appearance);
        _ = ApplyAppearanceAsync(refreshLists: false);
    }

    private async void OnPickBackground(object sender, RoutedEventArgs e) => await PickBackgroundAsync();

    private async Task PickBackgroundAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        picker.FileTypeChoices.Add("图片", new List<string> { ".png", ".jpg", ".jpeg", ".bmp", ".webp" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            SyncAppearanceControls();
            return;
        }
        _appearance.ImagePath = file.Path;
        _appearance.Mode = "image";
        AppearanceService.Save(_appearance);
        SyncAppearanceControls();
        _ = ApplyAppearanceAsync(refreshLists: true);
    }

    private async void OnResetAppearance(object sender, RoutedEventArgs e)
    {
        _appearance = new AppearanceSettings();
        AppearanceService.Save(_appearance);
        SyncAppearanceControls();
        await ApplyAppearanceAsync(refreshLists: true);
    }

    private void WireInteractions()
    {
        MessageInteractions.QuoteRequested += async vm =>
        {
            // 引用块点击要跳到"被引用的原消息"，必须用 refermsg 的 svrid；
            // vm.ServerId 是引用消息自己的 server_id，用它只会定位到自己（修复跳转失效）。
            var serverId = vm?.Message?.Quote?.SvrId;
            if (string.IsNullOrEmpty(serverId)) return;
            var target = await ViewModel.JumpToQuoteAsync(serverId);
            if (target == null) return;
            await Task.Delay(150);
            RunOnUi(() =>
            {
                MessageList.UpdateLayout();
                ScrollMessageToCenter(target);
            });
            await Task.Delay(150);
            target.IsHighlighted = true;
            _highlightTimer?.Stop();
            _highlightTimer = DispatcherQueue.CreateTimer();
            _highlightTimer.Interval = TimeSpan.FromSeconds(3);
            _highlightTimer.Tick += (_, _) =>
            {
                target.IsHighlighted = false;
                _highlightTimer.Stop();
            };
            _highlightTimer.Start();
        };

        MessageInteractions.ImageLightboxRequested += (vm, source) =>
        {
            LightboxImage.Source = source;
            Lightbox.Visibility = Visibility.Visible;
        };

        MessageInteractions.VideoOpenRequested += vm =>
        {
            var path = ViewModel.ResolveVideoPath(vm.Message?.Video?.LocalName);
            if (path != null && File.Exists(path))
            {
                NativeTools.ShellOpen(path);
            }
            else if (path != null)
            {
                NativeTools.ShowInExplorer(path);
            }
        };
    }

    // ---------- scroll / pagination ----------

    private static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }

    private async void OnMessagesScrollChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_loadingOlder || args.IsIntermediate || _messagesScroll == null) return;
        if (_messagesScroll.VerticalOffset > 60 || !ViewModel.HasMoreMessages) return;
        if (ViewModel.CurrentChat == null) return;

        _loadingOlder = true;
        try
        {
            double extentBefore = _messagesScroll.ExtentHeight;
            double offsetBefore = _messagesScroll.VerticalOffset;
            await ViewModel.LoadOlderAsync();
            RunOnUi(() =>
            {
                MessageList.UpdateLayout();
                double delta = _messagesScroll.ExtentHeight - extentBefore;
                if (delta > 0)
                {
                    _messagesScroll.ChangeView(null, offsetBefore + delta, null, true);
                }
            });
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private void ScrollToBottom()
    {
        MessageList.UpdateLayout();
        _messagesScroll ??= FindScrollViewer(MessageList);
        _messagesScroll?.ChangeView(null, _messagesScroll.ScrollableHeight, null, true);
    }

    /// <summary>
    /// 将目标消息滚动到视口垂直居中（微信定位样式）；
    /// ScrollIntoView 只有 Leading/Default，改用 ChangeView 计算居中偏移。
    /// </summary>
    private void ScrollMessageToCenter(MessageViewModel target)
    {
        if (_messagesScroll == null) return;
        try
        {
            if (MessageList.ContainerFromItem(target) is not FrameworkElement container) return;
            container.UpdateLayout();
            var origin = container.TransformToVisual(_messagesScroll.Content as UIElement ?? MessageList)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var itemTop = origin.Y + _messagesScroll.VerticalOffset;
            var centered = itemTop - (_messagesScroll.ViewportHeight / 2) + (container.ActualHeight / 2);
            _messagesScroll.ChangeView(null, Math.Max(0, centered), null, true);
        }
        catch
        {
            // 兜底：定位失败时退回 Leading 对齐
            MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
        }
    }

    // ---------- sidebar ----------

    private void OnSessionSearchChanged(object sender, TextChangedEventArgs args)
    {
        ViewModel.Filter = SessionSearchBox.Text;
    }

    private async void OnSessionSelected(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0) return;
        if (args.AddedItems[0] is not SessionViewModel session) return;
        // 外观切换后恢复选中项会再次触发本事件：同一会话不重复加载
        if (ReferenceEquals(session, ViewModel.CurrentSession) && ViewModel.CurrentChat != null) return;
        MessageRow.CurrentChatIsGroup = session.IsGroup;
        EmptyState.Visibility = Visibility.Collapsed;
        ChatHeader.Visibility = Visibility.Visible;
        await ViewModel.SelectSessionAsync(session);
        RunOnUi(() =>
        {
            if (!ViewModel.SearchResultsShown)
            {
                ScrollToBottom();
            }
        });
    }

    // ---------- search ----------

    private async void OnRunSearch(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunSearchAsync(ChatSearchBox.Text, SelectedSearchKind());
    }

    private async void OnChatSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            await ViewModel.RunSearchAsync(ChatSearchBox.Text, SelectedSearchKind());
        }
    }

    private async void OnChatSearchKindChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel.CurrentChat == null || ChatSearchBox is null) return;
        if (ChatSearchBox.Text.Trim().Length > 0 || SelectedSearchKind().Length > 0)
        {
            await ViewModel.RunSearchAsync(ChatSearchBox.Text, SelectedSearchKind());
        }
    }

    private string SelectedSearchKind()
    {
        return (ChatSearchKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    }

    private async void OnBackToChat(object sender, RoutedEventArgs e)
    {
        ChatSearchBox.Text = "";
        ChatSearchKind.SelectedIndex = 0;
        await ViewModel.ClearSearchAsync();
        RunOnUi(ScrollToBottom);
    }

    private async void OnBackToLatest(object sender, RoutedEventArgs e)
    {
        ChatSearchBox.Text = "";
        ChatSearchKind.SelectedIndex = 0;
        await ViewModel.ClearSearchAsync();
        RunOnUi(ScrollToBottom);
    }

    // ---------- bootstrap ----------

    private async void OnExtractKeys(object sender, RoutedEventArgs e)
    {
        RunOnUi(() => ExtractButton.IsEnabled = false);
        try
        {
            if (await ViewModel.ExtractKeysAsync())
            {
                RunOnUi(() => InitStatusText.Text = "正在解密数据库并加载…");
                await ViewModel.RunInitPipelineAsync();
                RunOnUi(ApplyBootstrapVisibility);
            }
        }
        finally
        {
            RunOnUi(() => ExtractButton.IsEnabled = !ViewModel.Busy);
        }
    }

    private async void OnRescanKeys(object sender, RoutedEventArgs e)
    {
        RunOnUi(() => RescanKeysButton.IsEnabled = false);
        try
        {
            await ViewModel.RefreshKeysAsync();
            RunOnUi(() => ImageKeyBar.IsOpen = ViewModel.HasData && !ViewModel.HasImageKey);
        }
        finally
        {
            RunOnUi(() => RescanKeysButton.IsEnabled = true);
        }
    }

    // ---------- stickers ----------

    private async void OnCompleteStickers(object sender, RoutedEventArgs e)
    {
        await ViewModel.CompleteStickersAsync();
        RunOnUi(() =>
        {
            ChatSearchBox.Text = "";
            ChatSearchKind.SelectedIndex = 0;
        });
        await ViewModel.ClearSearchAsync();
        RunOnUi(ScrollToBottom);
    }

    // ---------- export ----------

    private async void OnExportHtml(object sender, RoutedEventArgs e) => await ExportAsync("html");
    private async void OnExportTxt(object sender, RoutedEventArgs e) => await ExportAsync("txt");
    private async void OnExportCsv(object sender, RoutedEventArgs e) => await ExportAsync("csv");

    private async Task ExportAsync(string format)
    {
        if (ViewModel.CurrentChat == null || ViewModel.Busy) return;
        var suggested = ViewModel.ExportFilename(format) ?? "chat.html";
        var picker = new FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggested),
            DefaultFileExtension = format == "html" ? ".html" : format == "csv" ? ".csv" : ".txt",
        };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeChoices.Add(format switch
        {
            "html" => "网页文件",
            "csv" => "CSV 文件",
            _ => "文本文件",
        }, new List<string> { picker.DefaultFileExtension });

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        ViewModel.Busy = true;
        ViewModel.BusyText = "正在导出…";
        try
        {
            // Use plain System.IO on file.Path: no WinRT stream interop needed.
            var targetPath = file.Path;
            if (format == "html")
            {
                await using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await ViewModel.ExportHtmlAsync(stream);
            }
            else
            {
                var content = format == "csv" ? await ViewModel.ExportCsvAsync() : await ViewModel.ExportTxtAsync();
                await System.IO.File.WriteAllTextAsync(targetPath, content);
            }
            await ShowDialogAsync("导出完成", $"已导出到：{targetPath}");
        }
        catch (Exception error)
        {
            await ShowDialogAsync("导出失败", error.Message);
        }
        finally
        {
            ViewModel.Busy = false;
            ViewModel.BusyText = "";
        }
    }

    private Task ShowDialogAsync(string title, string message)
    {
        // ContentDialog must be created and shown on the UI thread.
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUi(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                };
                await dialog.ShowAsync();
                tcs.TrySetResult(null);
            }
            catch
            {
                // dialog may fail during shutdown; ignore
                tcs.TrySetResult(null);
            }
        });
        return tcs.Task;
    }

    // ---------- lightbox ----------

    private void OnLightboxTapped(object sender, TappedRoutedEventArgs args)
    {
        Lightbox.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Lightbox.Visibility == Visibility.Visible)
        {
            Lightbox.Visibility = Visibility.Collapsed;
            LightboxImage.Source = null;
            args.Handled = true;
        }
    }
}
