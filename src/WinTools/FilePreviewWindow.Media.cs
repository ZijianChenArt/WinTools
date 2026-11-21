using System;
using System.Threading;
using System.Threading.Tasks;
using FFmpegInteropX;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private MediaPlayer? _previewPlayer;
    private DispatcherQueueTimer? _mediaTimer;
    private bool _updatingMediaProgress;
    private bool _seeking;
    private int _mediaLoadToken;
    private string _mediaPath = "";
    private double _mediaVolume = 1;
    private bool _mediaMuted;
    private bool _mediaReady;
    private bool _autoPlayPending;
    private FFmpegMediaSource? _mp4Source;
    private IRandomAccessStream? _mp4Stream;

    private void InitializeMediaControls()
    {
        _mediaTimer = DispatcherQueue.CreateTimer();
        _mediaTimer.Interval = TimeSpan.FromMilliseconds(250);
        _mediaTimer.Tick += (_, _) => UpdateMediaProgress();
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _seeking = true), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _seeking = false), true);
        SeekSlider.PointerCaptureLost += (_, _) => _seeking = false;
        SeekSlider.PointerCanceled += (_, _) => _seeking = false;
    }

    private void OpenMedia(StorageFile file, int token, string path) =>
        OpenMedia(MediaSource.CreateFromStorageFile(file), token, path);

    private void OpenMedia(IMediaPlaybackSource source, int token, string path)
    {
        _mediaLoadToken = token;
        _mediaPath = path;
        var player = new MediaPlayer { AutoPlay = false, Volume = _mediaVolume, IsMuted = _mediaMuted };
        _mediaReady = false;
        _autoPlayPending = true;
        _previewPlayer = player;
        player.MediaOpened += OnMediaOpened;
        player.MediaFailed += OnMediaFailed;
        player.Source = source;
        _mediaTimer?.Start();
        UpdateMediaProgress();
    }

    private bool IsCurrentMedia(MediaPlayer player) => _isOpen && ReferenceEquals(player, _previewPlayer)
        && _mediaLoadToken == _loadToken && string.Equals(_mediaPath, CurrentPath, StringComparison.OrdinalIgnoreCase);

    private void ReleaseMedia()
    {
        _mediaTimer?.Stop();
        var player = _previewPlayer;
        _previewPlayer = null;
        _mediaPath = "";
        _mediaReady = false;
        _autoPlayPending = false;
        _seeking = false;
        try
        {
            if (player == null) return;
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            try
            {
                MediaView.SetMediaPlayer(null);
                player.Source = null;
            }
            finally { player.Dispose(); }
        }
        finally
        {
            try { _mp4Source?.Dispose(); }
            finally
            {
                _mp4Source = null;
                _mp4Stream?.Dispose();
                _mp4Stream = null;
            }
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var error = args.Error;
        var hresult = args.ExtendedErrorCode?.HResult ?? 0;
        var message = args.ErrorMessage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMedia(sender)) return;
            var path = _mediaPath;
            ErrorReporter.Log("FilePreviewWindow.MediaFailed", $"{error} (0x{hresult:X8}) {message}");
            ShowMediaFailure(path);
        });
    }

    private async Task OpenMp4Async(StorageFile file, string path, int token, CancellationToken cancellation)
    {
        IRandomAccessStream? stream = null;
        FFmpegMediaSource? source = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            stream = await file.OpenAsync(FileAccessMode.Read).AsTask(timeout.Token);
            var config = new MediaSourceConfig();
            // Feed the selected file to the MP4 demuxer, even if another format uses an .mp4 extension.
            config.FFmpegOptions["format_whitelist"] = "mov";
            config.FFmpegOptions["protocol_whitelist"] = "file";
            config.Video.VideoDecoderMode = VideoDecoderMode.AutomaticSystemDecoder;
            config.Video.SystemDecoderH264MaxProfile = 100; // High 10 needs FFmpeg decoding.
            config.Video.MaxDecoderThreads = 4;
            source = await FFmpegMediaSource.CreateFromStreamAsync(stream, config, AppWindow.Id.Value).AsTask(timeout.Token);
            if (!_isOpen || token != _loadToken || cancellation.IsCancellationRequested) return;

            var item = source.CreateMediaPlaybackItem();
            _mp4Source = source;
            _mp4Stream = stream;
            source = null;
            stream = null;
            OpenMedia(item, token, path);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_isOpen || token != _loadToken) return;
            ErrorReporter.Log("FilePreviewWindow.Mp4", ex);
            ShowMediaFailure(path);
        }
        finally
        {
            try { source?.Dispose(); }
            finally { stream?.Dispose(); }
        }
    }

    private void ShowMediaFailure(string path)
    {
        try { ReleaseMedia(); }
        catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.ReleaseMedia", ex); }
        ShowFallback(path, "无法播放此文件", "无法读取文件，或其视频／音频编码不受支持。按 Enter 用默认程序打开。");
    }

    private void UpdateMediaProgress()
    {
        var player = _previewPlayer;
        if (player == null || !IsCurrentMedia(player)) return;
        var session = player.PlaybackSession;
        var duration = Math.Max(0, session.NaturalDuration.TotalSeconds);
        var position = Math.Clamp(session.Position.TotalSeconds, 0, Math.Max(0, duration));
        _updatingMediaProgress = true;
        try
        {
            SeekSlider.IsEnabled = duration > 0 && session.CanSeek;
            if (!_seeking)
            {
                SeekSlider.Maximum = Math.Max(1, duration);
                SeekSlider.Value = position;
            }
            MediaTimeText.Text = $"{FormatMediaTime(position)} / {FormatMediaTime(duration)}";
            var playing = session.PlaybackState == MediaPlaybackState.Playing;
            PlayGlyph.Glyph = playing ? "\uE769" : "\uE768";
            AutomationProperties.SetName(PlayButton, playing ? "暂停" : "播放");
            MuteGlyph.Glyph = player.IsMuted || player.Volume == 0 ? "\uE74F" : "\uE767";
            AutomationProperties.SetName(MuteButton, player.IsMuted ? "恢复音量" : "静音");
        }
        finally { _updatingMediaProgress = false; }
    }

    private static string FormatMediaTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        _autoPlayPending = false;
        var player = _previewPlayer;
        if (player == null) return;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) player.Pause();
        else
        {
            var session = player.PlaybackSession;
            if (session.CanSeek && session.NaturalDuration > TimeSpan.Zero && session.Position >= session.NaturalDuration)
                session.Position = TimeSpan.Zero;
            player.Play();
        }
        UpdateMediaProgress();
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingMediaProgress || _previewPlayer == null || !SeekSlider.IsEnabled) return;
        var session = _previewPlayer.PlaybackSession;
        if (session.CanSeek)
            session.Position = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, Math.Max(0, session.NaturalDuration.TotalSeconds)));
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _mediaMuted = !_mediaMuted;
        if (_previewPlayer is { } player) player.IsMuted = _mediaMuted;
        UpdateMediaProgress();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _mediaVolume = e.NewValue / 100;
        if (_previewPlayer is { } player)
        {
            player.Volume = _mediaVolume;
            player.IsMuted = _mediaMuted = false;
            UpdateMediaProgress();
        }
    }

    private void MediaSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窄窗口优先保留进度和时间，音量仍可通过静音按钮控制。
        VolumeSlider.Visibility = e.NewSize.Width >= 480 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TryStartMediaPlayback()
    {
        if (!_autoPlayPending || !_mediaReady || _previewPlayer is not { } player || !IsCurrentMedia(player)) return;
        _autoPlayPending = false;
        player.Play();
    }
}
