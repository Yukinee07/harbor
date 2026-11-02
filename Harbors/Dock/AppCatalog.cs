using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Harbor.Interop;

namespace Harbor.Dock;

/// <summary>
/// Friendly names and icons for apps. Icons are extracted on a dedicated STA thread (shell icon
/// extraction can take tens of milliseconds and must never stall the dock), then cached by source.
/// </summary>
internal sealed class AppCatalog : IDisposable
{
    readonly Dispatcher _ui;
    readonly BlockingCollection<(string source, string? fallbackExe)> _queue = new();
    readonly ConcurrentDictionary<string, IconImage?> _icons = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Thread _worker;
    volatile Dictionary<string, string> _namesById = new(StringComparer.OrdinalIgnoreCase);
    volatile Dictionary<string, string> _namesByExe = new(StringComparer.OrdinalIgnoreCase);
    volatile List<ShellInterop.InstalledApp> _installed = new();
    readonly ConcurrentDictionary<string, string> _namesFromFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An icon finished loading; the argument is its source key.</summary>
    public event Action<string>? IconLoaded;
    public event Action? CatalogLoaded;

    public IReadOnlyList<ShellInterop.InstalledApp> InstalledApps => _installed;

    public AppCatalog(Dispatcher ui)
    {
        _ui = ui;
        _worker = new Thread(Work) { IsBackground = true, Name = "Harbor icons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
        var t = new Thread(LoadCatalog) { IsBackground = true, Name = "Harbor catalog" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // Known-folder GUIDs Windows uses inside the AUMIDs it derives for unpackaged apps.
    static readonly (string guid, Environment.SpecialFolder folder)[] s_knownFolders =
    {
        ("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.SpecialFolder.ProgramFiles),
        ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.SpecialFolder.ProgramFilesX86),
        ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.SpecialFolder.System),
        ("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Environment.SpecialFolder.SystemX86),
        ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.SpecialFolder.Windows),
        ("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", Environment.SpecialFolder.LocalApplicationData),
        ("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", Environment.SpecialFolder.ApplicationData),
    };

    /// <summary>Turns an AppsFolder id like "{6D80...}\Vendor\app.exe" back into a real path.</summary>
    public static string? AppIdToPath(string appId)
    {
        foreach (var (guid, folder) in s_knownFolders)
        {
            if (appId.StartsWith(guid, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(Environment.GetFolderPath(folder), appId[guid.Length..].TrimStart('\\'));
        }
        return appId.Length > 3 && appId[1] == ':' ? appId : null;
    }

    void LoadCatalog()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var apps = ShellInterop.EnumerateAppsFolder();
            var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps)
            {
                byId.TryAdd(a.AppId, a.Name);
                var path = AppIdToPath(a.AppId);
                if (path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) byExe.TryAdd(path, a.Name);
            }
            _namesById = byId;
            _namesByExe = byExe;
            _installed = apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Log.Info($"App catalog: {apps.Count} apps in {sw.ElapsedMilliseconds} ms");
            _ui.BeginInvoke(() => CatalogLoaded?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Error("App catalog failed", ex);
        }
    }

    public string NameFor(string? appId, string? exePath, string? fallback = null)
    {
        if (appId != null)
        {
            if (appId.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase)) return "File Explorer";
            if (_namesById.TryGetValue(appId, out var n)) return n;
        }
        if (exePath != null)
        {
            if (_namesByExe.TryGetValue(exePath, out var n)) return n;
            // The dock refreshes on every window switch, which is too often to reopen the file each time.
            return _namesFromFiles.GetOrAdd(exePath, static path =>
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    var desc = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length < 48) return desc.Trim();
                }
                catch { }
                return Path.GetFileNameWithoutExtension(path);
            });
        }
        return fallback ?? appId ?? "App";
    }

    /// <summary>
    /// Returns the cached icon for <paramref name="source"/> (a path or shell parsing name), or null and
    /// queues a background load that raises <see cref="IconLoaded"/>.
    /// </summary>
    public IconImage? GetIcon(string source, string? fallbackExe = null)
    {
        if (_icons.TryGetValue(source, out var icon)) return icon;
        if (_pending.TryAdd(source, 0)) _queue.Add((source, fallbackExe));
        return null;
    }

    public bool HasIcon(string source) => _icons.ContainsKey(source);

    public void Invalidate(string source)
    {
        _icons.TryRemove(source, out _);
    }

    void Work()
    {
        foreach (var (source, fallback) in _queue.GetConsumingEnumerable())
        {
            IconImage? icon = null;
            try
            {
                icon = ShellIcons.Load(source);
                if (icon == null && fallback != null) icon = ShellIcons.Load(fallback);
            }
            catch (Exception ex)
            {
                Log.Warn($"Icon load failed for {source}: {ex.Message}");
            }
            _icons[source] = icon;
            _pending.TryRemove(source, out _);
            _ui.BeginInvoke(() => IconLoaded?.Invoke(source));
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
