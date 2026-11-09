using System.Windows.Interop;
using System.Windows.Threading;
using Harbor.Dock;
using Harbor.Pomodoro;
using Harbor.Settings;

namespace Harbor.UI;

/// <summary>
/// Keeps the timer running and its two faces up to date: the floating window and the dock icon. The timer carries on with
/// its window closed, and the window only exists while it's open.
/// </summary>
internal sealed class PomodoroController : IDisposable
{
    readonly AppSettings _settings;
    readonly DockController _dock;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    PomodoroWindow? _window;
    bool _coveredByFullscreen;

    public PomodoroTimer Timer { get; }

    public PomodoroController(AppSettings settings, DockController dock)
    {
        _settings = settings;
        _dock = dock;
        Timer = new PomodoroTimer(settings.Pomodoro);
        _tick.Tick += (_, _) => Timer.Tick();
        Timer.Changed += () => { UpdateTicking(); PushToDock(); _settings.Save(); };
        Timer.Ticked += PushToDock;
        Timer.Finished += OnFinished;
        dock.PomodoroClicked += Toggle;
        dock.SettingsApplied += Apply;
        dock.Tracker.ForegroundChanged += _ => CheckFullscreen();
        UpdateTicking();
        PushToDock();
        if (settings.Dock.ShowPomodoro && settings.Pomodoro.WindowOpen) Open();
    }

    void UpdateTicking()
    {
        if (Timer.Running) _tick.Start(); else _tick.Stop();
    }

    void PushToDock() => _dock.SetPomodoro(Timer.Fraction, Timer.Running, Timer.OnBreak);

    public void Toggle()
    {
        if (_window != null) Close(); else Open();
    }

    public void Open()
    {
        if (_window != null) return;
        _window = new PomodoroWindow(Timer, _settings.Pomodoro, _settings.Save, Close);
        _window.Show();
        Remember(open: true);
        CheckFullscreen();
    }

    public void Close()
    {
        if (_window == null) return;
        _window.Close();
        _window = null;
        _coveredByFullscreen = false;
        Remember(open: false);
    }

    void Remember(bool open)
    {
        if (_settings.Pomodoro.WindowOpen == open) return;
        _settings.Pomodoro.WindowOpen = open;
        _settings.Save();
    }

    /// <summary>Settings may have changed: the dock switch, or the lengths.</summary>
    public void Apply()
    {
        if (!_settings.Dock.ShowPomodoro) Close();
        Timer.LengthsChanged();
    }

    void OnFinished(PomodoroPhase ended)
    {
        System.Media.SystemSounds.Asterisk.Play();
        _dock.BouncePomodoro();
        _window?.ShowFullFor(6000);
    }

    /// <summary>It floats above everything, which isn't wanted over a game or a film.</summary>
    void CheckFullscreen()
    {
        if (_window == null) return;
        bool covered = DockController.IsFullscreenForeground(new WindowInteropHelper(_window).Handle);
        if (covered == _coveredByFullscreen) return;
        _coveredByFullscreen = covered;
        if (covered) _window.Hide(); else _window.Show();
    }

    /// <summary>For the developer test.</summary>
    internal PomodoroWindow? Window => _window;

    public void Dispose()
    {
        _tick.Stop();
        // Goes through the window directly so that quitting leaves it marked as open for next time.
        _window?.Close();
        _window = null;
    }
}
