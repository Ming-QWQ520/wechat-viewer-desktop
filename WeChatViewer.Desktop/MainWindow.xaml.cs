using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    public MainWindow()
    {
        ViewModel = new MainViewModel(action => DispatcherQueue.TryEnqueue(() => action()));
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        WireViewModel();
        WireInteractions();
        ConfigureResolvers();

        LogList.ItemsSource = ViewModel.LogLines;
        ViewModel.LogLines.CollectionChanged += (_, _) =>
        {
            LogScroller.UpdateLayout();
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, true);
        };
        SessionList.ItemsSource = ViewModel.Sessions;
        MessageList.ItemsSource = ViewModel.Messages;

        // Window has no Loaded event; hook the content root instead.
        ((FrameworkElement)Content).Loaded += OnLoaded;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        ApplyBootstrapVisibility();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _messagesScroll ??= FindScrollViewer(MessageList);
        if (_messagesScroll != null)
        {
            _messagesScroll.ViewChanged += OnMessagesScrollChanged;
        }
    }

    // ---------- view model wiring ----------

    private void WireViewModel()
    {
        ViewModel.PropertyChanged += (_, args) =>
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
        };
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
            (md5, cdnUrl) => ViewModel.ResolveStickerBytes(md5, cdnUrl),
            localName => ViewModel.ResolveVideoThumb(localName),
            message => ViewModel.ResolveFile(message.File?.Name));
    }

    private void WireInteractions()
    {
        MessageInteractions.QuoteRequested += async vm =>
        {
            var target = await ViewModel.JumpToQuoteAsync(vm.ServerId);
            if (target == null) return;
            await Task.Delay(150);
            MessageList.UpdateLayout();
            MessageList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
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
            MessageList.UpdateLayout();
            double delta = _messagesScroll.ExtentHeight - extentBefore;
            if (delta > 0)
            {
                _messagesScroll.ChangeView(null, offsetBefore + delta, null, true);
            }
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

    // ---------- sidebar ----------

    private void OnSessionSearchChanged(object sender, TextChangedEventArgs args)
    {
        ViewModel.Filter = SessionSearchBox.Text;
    }

    private async void OnSessionSelected(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count == 0) return;
        if (args.AddedItems[0] is not SessionViewModel session) return;
        MessageRow.CurrentChatIsGroup = session.IsGroup;
        EmptyState.Visibility = Visibility.Collapsed;
        ChatHeader.Visibility = Visibility.Visible;
        await ViewModel.SelectSessionAsync(session);
        if (!ViewModel.SearchResultsShown)
        {
            ScrollToBottom();
        }
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
        ScrollToBottom();
    }

    private async void OnBackToLatest(object sender, RoutedEventArgs e)
    {
        ChatSearchBox.Text = "";
        ChatSearchKind.SelectedIndex = 0;
        await ViewModel.ClearSearchAsync();
        ScrollToBottom();
    }

    // ---------- bootstrap ----------

    private async void OnExtractKeys(object sender, RoutedEventArgs e)
    {
        ExtractButton.IsEnabled = false;
        try
        {
            if (await ViewModel.ExtractKeysAsync())
            {
                InitStatusText.Text = "正在解密数据库并加载…";
                await ViewModel.RunInitPipelineAsync();
                ApplyBootstrapVisibility();
            }
        }
        finally
        {
            ExtractButton.IsEnabled = !ViewModel.Busy;
        }
    }

    private async void OnRescanKeys(object sender, RoutedEventArgs e)
    {
        RescanKeysButton.IsEnabled = false;
        try
        {
            await ViewModel.RefreshKeysAsync();
            ImageKeyBar.IsOpen = ViewModel.HasData && !ViewModel.HasImageKey;
        }
        finally
        {
            RescanKeysButton.IsEnabled = true;
        }
    }

    // ---------- stickers ----------

    private async void OnCompleteStickers(object sender, RoutedEventArgs e)
    {
        await ViewModel.CompleteStickersAsync();
        ChatSearchBox.Text = "";
        ChatSearchKind.SelectedIndex = 0;
        await ViewModel.ClearSearchAsync();
        ScrollToBottom();
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

    private async Task ShowDialogAsync(string title, string message)
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
        }
        catch
        {
            // dialog may fail during shutdown; ignore
        }
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
