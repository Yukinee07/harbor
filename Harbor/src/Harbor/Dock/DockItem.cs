using Harbor.Interop;
using Harbor.Settings;

namespace Harbor.Dock;

internal enum DockItemKind { App, Start, RecycleBin, Separator, Media, Harbor, Clock, Pomodoro }

/// <summary>One slot in the dock: a pinned app, a running app, or a built-in button.</summary>
internal sealed class DockItem
{
    public DockItem(string key, DockItemKind kind) { Key = key; Kind = kind; }

    /// <summary>Stable identity used to diff the dock between refreshes.</summary>
    public string Key { get; }
    public DockItemKind Kind { get; }
    public string Name { get; set; } = "";
    public IconImage? Icon { get; set; }
    /// <summary>Bumped whenever <see cref="Icon"/> changes so the renderer knows to repaint.</summary>
    public int IconVersion { get; set; }

    public PinnedApp? Pin { get; set; }
    public bool IsPinned => Pin != null;
    public string? ExePath { get; set; }
    public string? AppId { get; set; }

    public List<IntPtr> Windows { get; } = new();
    public bool IsRunning => Windows.Count > 0;
    public bool IsActive { get; set; }
    public bool NeedsAttention { get; set; }

    /// <summary>Launch in progress: the icon bounces until a window shows up or the timeout ends.</summary>
    public DateTime LaunchingUntil { get; set; }

    // For the Pomodoro button: how much time is left, 1 down to 0.
    public float TimerFraction { get; set; } = 1;
    public bool TimerRunning { get; set; }
    public bool TimerOnBreak { get; set; }

    public override string ToString() => $"{Kind}:{Name} ({Windows.Count} windows)";
}
