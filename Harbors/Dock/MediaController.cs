using System.Windows.Threading;
using Harbor.Interop;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Harbor.Dock;

/// <summary>
/// Whatever is playing on the PC (Spotify, a browser tab, Media Player...) through the system media
/// session API, so the dock can show the album art and control playback.
/// </summary>
internal sealed class MediaController : IDisposable
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    int _refreshId;

    public bool HasSession => _session != null && !string.IsNullOrWhiteSpace(Title);
    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? SourceAppId { get; private set; }
    public bool IsPlaying { get; private set; }
    public IconImage? Art { get; private set; }
    public int Version { get; private set; }

    /// <summary>Raised on the UI thread whenever the track, art or play state changes.</summary>
    public event Action? Changed;

    public MediaController(Dispatcher ui)
    {
        _ui = ui;
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(() => Attach(_manager.GetCurrentSession()));
            await _ui.InvokeAsync(() => Attach(_manager.GetCurrentSession()));
        }
        catch (Exception ex)
        {
            Log.Warn("Media sessions unavailable: " + ex.Message);
        }
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
        }
        _session = session;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
        }
        _ = RefreshAsync();
    }

    void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ui.BeginInvoke(() => _ = RefreshAsync());
    void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => _ui.BeginInvoke(UpdatePlayback);

    void UpdatePlayback()
    {
        bool playing = _session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing == IsPlaying) return;
        IsPlaying = playing;
        Version++;
        Changed?.Invoke();
    }

    async Task RefreshAsync()
    {
        int id = ++_refreshId;
        var session = _session;
        if (session == null)
        {
            Title = Artist = SourceAppId = null;
            Art = null;
            IsPlaying = false;
            Version++;
            Changed?.Invoke();
            return;
        }
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            IconImage? art = props?.Thumbnail != null ? await DecodeAsync(props.Thumbnail) : null;
            if (id != _refreshId) return; // a newer refresh superseded this one
            Title = props?.Title;
            Artist = props?.Artist;
            if (SourceAppId != session.SourceAppUserModelId) Log.Info($"Now playing tile: {session.SourceAppUserModelId}, artist {(string.IsNullOrEmpty(props?.Artist) ? "none" : "given")}");
            SourceAppId = session.SourceAppUserModelId;
            Art = art;
            IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Version++;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Media refresh failed: " + ex.Message);
        }
    }

    static async Task<IconImage?> DecodeAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            // Album art can be huge; the dock never shows it above ~200 px.
            var transform = new BitmapTransform();
            uint side = Math.Min(decoder.PixelWidth, decoder.PixelHeight);
            if (side > 256)
            {
                double k = 256.0 / side;
                transform.ScaledWidth = (uint)(decoder.PixelWidth * k);
                transform.ScaledHeight = (uint)(decoder.PixelHeight * k);
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            var buffer = new Windows.Storage.Streams.Buffer((uint)(w * h * 4));
            bitmap.CopyToBuffer(buffer);
            var bytes = new byte[w * h * 4];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
            return new IconImage(w, h, bytes);
        }
        catch
        {
            return null;
        }
    }

    public void PlayPause() => _ = Try(s => s.TryTogglePlayPauseAsync().AsTask());
    public void Next() => _ = Try(s => s.TrySkipNextAsync().AsTask());
    public void Previous() => _ = Try(s => s.TrySkipPreviousAsync().AsTask());

    async Task Try(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        if (_session == null) return;
        try { await action(_session); }
        catch (Exception ex) { Log.Warn("Media command failed: " + ex.Message); }
    }

    public string Label
    {
        get
        {
            string text = string.IsNullOrWhiteSpace(Artist) ? Title ?? "" : $"{Title}, {Artist}";
            return text.Length > 54 ? text[..51] + "..." : text;
        }
    }

    public void Dispose() => Attach(null);
}
