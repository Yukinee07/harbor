using System.Diagnostics;

namespace Harbor;

/// <summary>Developer aid: with HARBOR_TRACE=1 in the environment, logs what each startup step costs.</summary>
internal static class Diag
{
    static readonly bool s_on = Environment.GetEnvironmentVariable("HARBOR_TRACE") == "1";

    static string? s_items;

    /// <summary>What the dock shows, whenever that changes.</summary>
    public static void Items(IEnumerable<Dock.DockItem> items)
    {
        if (!s_on) return;
        string line = string.Join(", ", items.Where(i => i.Kind == Dock.DockItemKind.App).Select(i => $"{i.Name}{(i.IsPinned ? " [pinned]" : "")} x{i.Windows.Count}"));
        if (line == s_items) return;
        s_items = line;
        Log.Info("[dock] " + line);
    }

    public static void Mark(string step)
    {
        if (!s_on) return;
        using var p = Process.GetCurrentProcess();
        var drivers = new List<string>();
        foreach (ProcessModule m in p.Modules)
        {
            string n = m.ModuleName.ToLowerInvariant();
            if (n.StartsWith("nvwgf2") || n.StartsWith("nvd3dum") || n.StartsWith("nvgpucomp") || n.StartsWith("igd10um") || n.StartsWith("igdumd")
                || n.StartsWith("igd12") || n.StartsWith("igc64") || n.StartsWith("media_bin") || n.StartsWith("d3d12") || n.StartsWith("windows.ui.xaml")
                || n.StartsWith("wpfgfx") || n.StartsWith("d3d9") || n.StartsWith("d3d11") || n.StartsWith("dcomp") || n.StartsWith("dxcore"))
                drivers.Add(n);
            m.Dispose();
        }
        drivers.Sort();
        Log.Info($"[mem] {step}: private {p.PrivateMemorySize64 >> 20} MB, working set {p.WorkingSet64 >> 20} MB, managed {GC.GetTotalMemory(false) >> 20} MB, threads {p.Threads.Count}, gfx [{string.Join(' ', drivers)}]");
    }
}
