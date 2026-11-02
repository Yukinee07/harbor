namespace Harbor.UI;

/// <summary>Physical pixels on screen, right and bottom exclusive.</summary>
internal readonly record struct ScreenBox(int L, int T, int R, int B);

/// <summary>How big each window's tile is, so that all of an app's windows fit on one line.</summary>
internal readonly record struct TileSize(double ThumbW, double ThumbH, double Padding, bool ShowTitle)
{
    const double FullWidth = 184, SmallestWidth = 10, MostPadding = 6, Aspect = 0.625;

    /// <param name="count">How many windows the app has.</param>
    /// <param name="available">The width to share out between them, in device independent units.</param>
    /// <param name="scale">The screen's scale. Sizes are worked out in whole screen pixels, because the layout rounds
    /// each measurement to pixels anyway, and across a long row those small differences add up to running off the screen.</param>
    public static TileSize Fit(int count, double available, double scale)
    {
        int slot = (int)Math.Floor(available * scale / Math.Max(1, count));
        int mostPadding = (int)Math.Round(MostPadding * scale);
        // Padding gives way along with the tile, smoothly, so one more window never makes the picture bigger.
        int w = Math.Min((int)Math.Round(FullWidth * scale), Math.Max(slot - mostPadding * 2, slot * 13 / 15));
        w = Math.Max(w, (int)Math.Round(SmallestWidth * scale));
        int padding = Math.Clamp((slot - w) / 2, 1, mostPadding);
        return new TileSize(w / scale, Math.Round(w * Aspect) / scale, padding / scale, w / scale >= 84);
    }

    /// <summary>Beyond this many, even the smallest tiles wouldn't fit. Allows for padding, which is at least a pixel each side.</summary>
    public static int MostThatFit(double available) => Math.Max(1, (int)(available / (SmallestWidth + 3)));
}

/// <summary>
/// Reads what the pointer means while a preview is open. Getting from an icon to a tile usually means crossing other
/// icons on the way, so nothing here reacts to merely passing over something: another app has to be rested on, and
/// leaving takes a moment to count. Holds no references to the UI, so it can be tested with made-up pointer paths.
/// </summary>
internal sealed class PreviewIntent
{
    public enum Verdict { Stay, Switch, Close }

    public const int SwitchAfterMs = 180, NoWindowsCloseAfterMs = 350, CloseAfterMs = 600;

    long _awaySince, _candidateSince;
    string? _candidate;
    int _lastY = int.MaxValue;

    public void Reset()
    {
        _awaySince = 0;
        _candidate = null;
        _lastY = int.MaxValue;
    }

    /// <summary>The preview, the icon, and everything between them. This is where the pointer is on its way across.</summary>
    static bool InKeepZone(int x, int y, ScreenBox preview, ScreenBox? icon)
    {
        const int slack = 12, belowIcon = 40;
        int left = preview.L, right = preview.R, bottom = preview.B;
        if (icon is { } i)
        {
            left = Math.Min(left, i.L);
            right = Math.Max(right, i.R);
            bottom = Math.Max(bottom, i.B + belowIcon);
        }
        return x >= left - slack && x <= right + slack && y >= preview.T - slack && y <= bottom + slack;
    }

    /// <param name="now">Milliseconds, from any steady clock.</param>
    /// <param name="preview">The open preview.</param>
    /// <param name="icon">The icon of the app it's showing.</param>
    /// <param name="otherApp">The dock item under the pointer, if that's a different one from the preview's.</param>
    /// <param name="otherHasWindows">Whether that item has windows to show.</param>
    public Verdict Update(long now, int x, int y, ScreenBox preview, ScreenBox? icon, string? otherApp, bool otherHasWindows)
    {
        bool movingUp = y < _lastY && _lastY != int.MaxValue;
        _lastY = y;

        // The dock keeps reporting the last icon while the pointer is up over the preview, so only trust it down at the dock.
        bool atDock = icon is { } i && y >= i.T - 2;
        if (atDock && otherApp != null)
        {
            _awaySince = 0;
            // Rising means heading for the preview, even if there's an icon underneath right now.
            if (otherApp != _candidate || movingUp)
            {
                _candidate = otherApp;
                _candidateSince = now;
                return Verdict.Stay;
            }
            long rested = now - _candidateSince;
            if (otherHasWindows) return rested >= SwitchAfterMs ? Verdict.Switch : Verdict.Stay;
            return rested >= NoWindowsCloseAfterMs ? Verdict.Close : Verdict.Stay;
        }

        _candidate = null;
        if (InKeepZone(x, y, preview, icon))
        {
            _awaySince = 0;
            return Verdict.Stay;
        }
        if (_awaySince == 0) _awaySince = now;
        return now - _awaySince >= CloseAfterMs ? Verdict.Close : Verdict.Stay;
    }
}
