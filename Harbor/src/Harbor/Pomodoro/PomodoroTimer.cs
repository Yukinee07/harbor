namespace Harbor.Settings
{
    public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

    /// <summary>The timer's lengths, where its window sits, and where it had got to, so a restart doesn't lose the session.</summary>
    public sealed class PomodoroSettings
    {
        public int FocusMinutes { get; set; } = 25;
        public int BreakMinutes { get; set; } = 5;
        public int LongBreakMinutes { get; set; } = 15;
        public bool WindowOpen { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }

        public PomodoroPhase Phase { get; set; } = PomodoroPhase.Focus;
        public bool Running { get; set; }
        public DateTime EndsAtUtc { get; set; }
        /// <summary>Time left while paused; 0 means this phase hasn't been started.</summary>
        public int SecondsLeft { get; set; }
        public int Completed { get; set; }
    }
}

namespace Harbor.Pomodoro
{
    using Harbor.Settings;

    /// <summary>
    /// The timer itself, with nothing about how it's shown. Time is measured against the clock rather than counted in ticks, so
    /// it stays right however late a tick arrives or if the PC sleeps. Breaks start on their own after a focus session, but
    /// the next focus session waits to be started.
    /// </summary>
    internal sealed class PomodoroTimer
    {
        public const int RoundsPerSet = 4;

        readonly PomodoroSettings _s;
        readonly Func<DateTime> _now;

        /// <summary>The phase or the running state changed. Worth saving.</summary>
        public event Action? Changed;
        /// <summary>A second went by while running. Only the display cares.</summary>
        public event Action? Ticked;
        /// <summary>A phase ran to its end by itself, as opposed to being skipped.</summary>
        public event Action<PomodoroPhase>? Finished;

        public PomodoroTimer(PomodoroSettings settings, Func<DateTime>? clock = null)
        {
            _s = settings;
            _now = clock ?? (() => DateTime.UtcNow);
            // It ran out while Harbor wasn't running. Nobody saw it finish, so don't start the next phase behind their back.
            if (_s.Running && _s.EndsAtUtc <= _now()) Advance(counted: true, autoStart: false);
        }

        public PomodoroPhase Phase => _s.Phase;
        public bool Running => _s.Running;
        public bool OnBreak => _s.Phase != PomodoroPhase.Focus;
        public bool Started => _s.Running || _s.SecondsLeft > 0;

        /// <summary>Dots to fill: how far through the set of four this is.</summary>
        public int Rounds => _s.Phase == PomodoroPhase.LongBreak ? RoundsPerSet : _s.Completed % RoundsPerSet;

        public TimeSpan Total => TimeSpan.FromMinutes(Math.Max(1, _s.Phase switch
        {
            PomodoroPhase.Focus => _s.FocusMinutes,
            PomodoroPhase.ShortBreak => _s.BreakMinutes,
            _ => _s.LongBreakMinutes,
        }));

        public TimeSpan Remaining
        {
            get
            {
                if (_s.Running)
                {
                    var left = _s.EndsAtUtc - _now();
                    return left > TimeSpan.Zero ? left : TimeSpan.Zero;
                }
                return _s.SecondsLeft > 0 ? TimeSpan.FromSeconds(_s.SecondsLeft) : Total;
            }
        }

        /// <summary>How much of this phase is left, from 1 down to 0.</summary>
        public float Fraction => (float)Math.Clamp(Remaining.TotalSeconds / Total.TotalSeconds, 0, 1);

        public void Toggle()
        {
            if (_s.Running) Pause(); else Start();
        }

        public void Start()
        {
            if (_s.Running) return;
            Begin();
            Changed?.Invoke();
        }

        public void Pause()
        {
            if (!_s.Running) return;
            _s.SecondsLeft = Math.Max(1, (int)Math.Ceiling(Remaining.TotalSeconds));
            _s.Running = false;
            Changed?.Invoke();
        }

        /// <summary>Back to the start of this phase. Pressed again, back to the very first session.</summary>
        public void Reset()
        {
            if (!Started)
            {
                _s.Phase = PomodoroPhase.Focus;
                _s.Completed = 0;
            }
            _s.Running = false;
            _s.SecondsLeft = 0;
            Changed?.Invoke();
        }

        /// <summary>On to the next phase. A skipped focus session doesn't count as a completed one.</summary>
        public void Skip()
        {
            Advance(counted: false, autoStart: _s.Running);
            Changed?.Invoke();
        }

        /// <summary>Lengths may have changed. A phase that's under way keeps its end time.</summary>
        public void LengthsChanged() => Changed?.Invoke();

        /// <summary>Called about once a second while running.</summary>
        public void Tick()
        {
            if (!_s.Running) return;
            if (_s.EndsAtUtc > _now())
            {
                Ticked?.Invoke();
                return;
            }
            var ended = _s.Phase;
            Advance(counted: true, autoStart: true);
            Changed?.Invoke();
            Finished?.Invoke(ended);
        }

        /// <summary>For the developer test: make the current phase end soon.</summary>
        public void EndIn(TimeSpan time) => _s.EndsAtUtc = _now() + time;

        void Begin()
        {
            _s.EndsAtUtc = _now() + Remaining;
            _s.SecondsLeft = 0;
            _s.Running = true;
        }

        void Advance(bool counted, bool autoStart)
        {
            _s.Running = false;
            _s.SecondsLeft = 0;
            if (_s.Phase == PomodoroPhase.Focus)
            {
                if (counted) _s.Completed++;
                _s.Phase = counted && _s.Completed % RoundsPerSet == 0 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
                if (autoStart) Begin();
            }
            else
            {
                _s.Phase = PomodoroPhase.Focus;
            }
        }
    }
}
