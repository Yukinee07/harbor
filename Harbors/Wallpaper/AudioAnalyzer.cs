// WasapiLoopbackCapture is marked obsolete in NAudio 3 in favour of a builder API, but it is still the
// simplest system-wide loopback and works fine.
#pragma warning disable CS0618
using NAudio.Dsp;
using NAudio.Wave;

namespace Harbor.Wallpaper;

/// <summary>
/// Listens to what the PC is already playing (WASAPI loopback, nothing is recorded or stored) and boils it
/// down to four smoothed numbers the scenes can react to: bass, mids, treble and overall level, each 0..1
/// with automatic gain so quiet and loud music both move the wallpaper.
/// </summary>
internal sealed class AudioAnalyzer : IDisposable
{
    const int FftSize = 1024, FftBits = 10;

    WasapiLoopbackCapture? _capture;
    readonly float[] _window = new float[FftSize];
    readonly Complex[] _fft = new Complex[FftSize];
    int _filled;
    float _peakBass = 0.02f, _peakMid = 0.02f, _peakHigh = 0.02f, _peakLevel = 0.02f;
    float _bass, _mid, _high, _level; // single floats: torn reads are impossible on x64
    long _lastData;

    public float Bass => Fresh ? _bass : 0;
    public float Mid => Fresh ? _mid : 0;
    public float Treble => Fresh ? _high : 0;
    public float Level => Fresh ? _level : 0;
    /// <summary>Loopback delivers nothing while the PC is silent; treat stale values as silence.</summary>
    bool Fresh => Environment.TickCount64 - Interlocked.Read(ref _lastData) < 400;
    public bool IsRunning => _capture != null;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Log.Warn("Audio capture stopped: " + e.Exception.Message); };
            _capture.StartRecording();
            Log.Info($"Audio reactive on ({_capture.WaveFormat})");
        }
        catch (Exception ex)
        {
            Log.Warn("Audio capture unavailable: " + ex.Message);
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        if (_capture == null) return;
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
        _capture = null;
        _bass = _mid = _high = _level = 0;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture?.WaveFormat;
        if (fmt == null || e.BytesRecorded == 0) return;
        int channels = fmt.Channels;
        var span = e.Buffer.AsSpan(0, e.BytesRecorded);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
        int bytesPerSample = fmt.BitsPerSample / 8;
        int frame = bytesPerSample * channels;
        for (int i = 0; i + frame <= span.Length; i += frame)
        {
            float sample = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = i + c * bytesPerSample;
                sample += isFloat ? BitConverter.ToSingle(span.Slice(o, 4)) : BitConverter.ToInt16(span.Slice(o, 2)) / 32768f;
            }
            _window[_filled++] = sample / channels;
            if (_filled == FftSize)
            {
                Analyze(fmt.SampleRate);
                // 50% overlap keeps the response snappy at modest cost.
                Array.Copy(_window, FftSize / 2, _window, 0, FftSize / 2);
                _filled = FftSize / 2;
            }
        }
        Interlocked.Exchange(ref _lastData, Environment.TickCount64);
    }

    void Analyze(int sampleRate)
    {
        double rms = 0;
        for (int i = 0; i < FftSize; i++)
        {
            float s = _window[i];
            rms += s * s;
            _fft[i].X = (float)(s * FastFourierTransform.HannWindow(i, FftSize));
            _fft[i].Y = 0;
        }
        rms = Math.Sqrt(rms / FftSize);
        FastFourierTransform.FFT(true, FftBits, _fft);

        float hz = sampleRate / (float)FftSize;
        float Band(float lo, float hi)
        {
            int a = Math.Max(1, (int)(lo / hz)), b = Math.Min(FftSize / 2 - 1, (int)(hi / hz));
            float sum = 0;
            for (int k = a; k <= b; k++) sum += MathF.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
            return sum / Math.Max(1, b - a + 1);
        }

        Update(ref _bass, ref _peakBass, Band(30, 160));
        Update(ref _mid, ref _peakMid, Band(160, 2000));
        Update(ref _high, ref _peakHigh, Band(2000, 9000));
        Update(ref _level, ref _peakLevel, (float)rms);
    }

    /// <summary>Automatic gain against a slowly decaying peak, fast attack and slower release.</summary>
    static void Update(ref float value, ref float peak, float raw)
    {
        peak = MathF.Max(raw, peak * 0.995f);
        peak = MathF.Max(peak, 0.002f);
        float target = MathF.Min(1, raw / peak);
        float v = value;
        v += (target - v) * (target > v ? 0.55f : 0.12f);
        value = v;
    }

    public void Dispose() => Stop();
}
