using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using WeChatViewer.Desktop.Models;
using WeChatViewer.Desktop.Services;
using WeChatViewer.Desktop.ViewModels;
using ImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace WeChatViewer.Desktop.Controls;

/// <summary>Decoupled interactions between message rows and the main window.</summary>
public static class MessageInteractions
{
    /// <summary>username → avatar ImageSource (set by MainWindow, backed by WeChatData).</summary>
    public static Func<string, Task<object>> AvatarResolver;

    /// <summary>Quote block clicked → jump to the original message.</summary>
    public static event Action<MessageViewModel> QuoteRequested;

    /// <summary>Image tapped → show fullscreen lightbox.</summary>
    public static event Action<MessageViewModel, ImageSource> ImageLightboxRequested;

    /// <summary>Video tapped → open in default player.</summary>
    public static event Action<MessageViewModel> VideoOpenRequested;

    public static void RaiseQuoteRequested(MessageViewModel vm) => QuoteRequested?.Invoke(vm);
    public static void RaiseImageLightbox(MessageViewModel vm, ImageSource source) => ImageLightboxRequested?.Invoke(vm, source);
    public static void RaiseVideoOpen(MessageViewModel vm) => VideoOpenRequested?.Invoke(vm);
}

/// <summary>
/// Single shared voice player: SILK → WAV decode (cached on disk), playback and
/// the waveform animation state on the active MessageViewModel.
/// </summary>
public static class VoicePlayerService
{
    private static MediaPlayer _player;
    private static MessageViewModel _current;
    private static DispatcherQueue _dispatcher;
    private static Func<MessageViewModel, Task<string>> _wavResolver;

    public static void Configure(DispatcherQueue dispatcher, Func<MessageViewModel, Task<string>> wavResolver)
    {
        _dispatcher = dispatcher;
        _wavResolver = wavResolver;
    }

    private static MediaPlayer EnsurePlayer()
    {
        if (_player == null)
        {
            _player = new MediaPlayer();
            _player.MediaEnded += (_, _) =>
            {
                if (_current != null) SetPlaying(_current, false);
                _current = null;
            };
            _player.MediaFailed += (_, _) =>
            {
                if (_current != null) SetPlaying(_current, false);
                _current = null;
            };
        }
        return _player;
    }

    private static void SetPlaying(MessageViewModel vm, bool playing)
    {
        void Apply()
        {
            if (vm.IsPlaying != playing) vm.IsPlaying = playing;
        }
        if (_dispatcher != null) _dispatcher.TryEnqueue(Apply);
        else Apply();
    }

    public static async Task ToggleAsync(MessageViewModel vm)
    {
        var player = EnsurePlayer();
        lock (player)
        {
            if (_current == vm && vm.IsPlaying)
            {
                player.Pause();
                SetPlaying(vm, false);
                return;
            }
            if (_current != null && _current != vm)
            {
                SetPlaying(_current, false);
            }
        }
        try
        {
            var wavPath = _wavResolver != null ? await _wavResolver(vm) : null;
            if (wavPath == null)
            {
                return;
            }
            lock (player)
            {
                var bytes = System.IO.File.ReadAllBytes(wavPath);
                var stream = new System.IO.MemoryStream(bytes).AsRandomAccessStream();
                var source = MediaSource.CreateFromStream(stream, "audio/wav");
                player.Source = source;
                player.Play();
                _current = vm;
            }
            SetPlaying(vm, true);
        }
        catch
        {
            SetPlaying(vm, false);
        }
    }

    public static void StopAll()
    {
        if (_player == null) return;
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch { }
        if (_current != null) SetPlaying(_current, false);
        _current = null;
    }
}
