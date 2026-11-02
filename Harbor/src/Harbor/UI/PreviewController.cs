using System.Windows.Threading;
using Harbor.Dock;
using Harbor.Interop;

namespace Harbor.UI;

/// <summary>
/// Decides when window previews appear: after a short hover on a running app. Once one is open, <see cref="PreviewIntent"/>
/// decides whether the pointer is heading for it, has settled on another app, or has really left. There's one preview
/// however many docks there are, and it belongs to whichever dock the pointer is on.
/// </summary>
internal sealed class PreviewController
{
    const int OpenDelayMs = 480;

    readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromMilliseconds(OpenDelayMs) };
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly PreviewIntent _intent = new();
    WindowPreview? _preview;
    DockWindow? _source, _hoveredOn;
    DockItem? _hovered;

    public bool Enabled { get; set; } = true;

    public PreviewController(DockController dock)
    {
        _openTimer.Tick += (_, _) =>
        {
            _openTimer.Stop();
            if (_hovered != null && _hoveredOn != null && HasWindows(_hovered)) Open(_hoveredOn, _hovered);
        };
        _watch.Tick += (_, _) => Watch();
        dock.HoverItemChanged += OnHover;
        dock.ItemPressed += (_, _) => Close();
        dock.ContextMenuRequested += (_, _, _) => Close();
        dock.DocksChanged += Close;
    }

    static bool HasWindows(DockItem? item) => item is { Kind: DockItemKind.App } && item.Windows.Any(Native.IsWindow);

    void OnHover(DockWindow dock, DockItem? item)
    {
        if (!Enabled) return;
        // One dock letting go after the pointer has already reached another shouldn't undo what the other just said.
        if (item == null && dock != _hoveredOn) return;
        _hovered = item;
        _hoveredOn = dock;

        if (_preview is { IsVisible: true })
        {
            if (dock == _source) return; // Watch decides, because crossing an icon means nothing yet
            Close();
        }
        _openTimer.Stop();
        if (HasWindows(item)) _openTimer.Start();
    }

    void Open(DockWindow dock, DockItem item)
    {
        var rect = dock.GetItemScreenRect(item);
        if (rect == null) return;
        // Made on first use: it's the only WPF window most sessions need, and many never need it.
        if (_preview == null)
        {
            _preview = new WindowPreview();
            _preview.WindowChosen += hwnd => { Close(); WindowActions.Activate(hwnd); };
        }
        _preview.ShowFor(item, rect.Value);
        if (!_preview.IsVisible) return;
        if (_source != dock) _source?.SetPreviewOpen(false);
        _source = dock;
        dock.SetPreviewOpen(true);
        _intent.Reset();
        _watch.Start();
    }

    void Watch()
    {
        if (_preview is not { IsVisible: true } || _source == null) { Close(); return; }
        Native.GetCursorPos(out var pt);
        var pr = _preview.ScreenRect;
        var item = _preview.Item;
        var ir = item != null ? _source.GetItemScreenRect(item) : null;
        var other = _hovered != null && _hoveredOn == _source && _hovered.Key != item?.Key ? _hovered : null;
        var verdict = _intent.Update(Environment.TickCount64, pt.X, pt.Y,
            new ScreenBox(pr.Left, pr.Top, pr.Right, pr.Bottom),
            ir is { } r ? new ScreenBox(r.Left, r.Top, r.Right, r.Bottom) : null,
            other?.Key, HasWindows(other));
        if (verdict == PreviewIntent.Verdict.Close) Close();
        else if (verdict == PreviewIntent.Verdict.Switch && other != null) Open(_source, other);
    }

    public void Close()
    {
        _openTimer.Stop();
        _watch.Stop();
        if (_preview != null && (_preview.IsVisible || _preview.Item != null)) _preview.Dismiss();
        var source = _source;
        _source = null;
        source?.SetPreviewOpen(false);
    }

    public void Shutdown()
    {
        Close();
        _preview?.Close();
    }
}
