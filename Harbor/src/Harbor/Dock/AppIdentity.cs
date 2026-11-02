namespace Harbor.Dock;

/// <summary>Decides whether a pinned app and a running window are the same app.</summary>
internal static class AppIdentity
{
    /// <summary>"Vendor.App_abc123!Main" gives "Vendor.App_abc123".</summary>
    public static string? FamilyOfAppId(string? appId)
    {
        int bang = appId?.IndexOf('!') ?? -1;
        return bang > 0 ? appId![..bang] : null;
    }

    /// <summary>
    /// "...\WindowsApps\MSTeams_26276.513.5241.9675_x64__8wekyb3d8bbwe\ms-teams.exe" gives "MSTeams_8wekyb3d8bbwe".
    /// A pin holding such a path keeps matching after an update changes the version in it.
    /// </summary>
    public static string? FamilyOfPath(string? path)
    {
        const string marker = @"\WindowsApps\";
        int at = path?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0) return null;
        int start = at + marker.Length, end = path!.IndexOf('\\', start);
        if (end < 0) return null;
        string folder = path[start..end];
        int name = folder.IndexOf('_'), publisher = folder.LastIndexOf("__", StringComparison.Ordinal);
        return name > 0 && publisher > name ? folder[..name] + "_" + folder[(publisher + 2)..] : null;
    }

    /// <summary>Sites installed as apps run from the browser's exe but are apps of their own.</summary>
    public static bool IsWebApp(string? appId) => appId != null && appId.Contains("_crx_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apps add a suffix for a mode or profile: Teams labels its window "...!MSTeams.work" under the "...!MSTeams" pin,
    /// and Chrome uses "Chrome.UserData.Profile15". Those are still the same app. A site installed as an app isn't.
    /// </summary>
    static bool IsSameOrVariant(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        return !IsWebApp(longer) && longer.StartsWith(shorter + ".", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="pinAppId">The pin's AppUserModelID, if it has one.</param>
    /// <param name="pinExe">The pin's executable, if it has one.</param>
    /// <param name="appId">The window's AppUserModelID, if it has one.</param>
    /// <param name="exe">The window's executable.</param>
    /// <param name="family">The package the window's process belongs to, if any.</param>
    public static bool Matches(string? pinAppId, string? pinExe, string? appId, string? exe, string? family)
    {
        if (pinAppId != null && appId != null && IsSameOrVariant(pinAppId, appId)) return true;

        string? pinFamily = FamilyOfAppId(pinAppId) ?? FamilyOfPath(pinExe);
        string? windowFamily = family ?? FamilyOfAppId(appId) ?? FamilyOfPath(exe);
        if (pinFamily != null && pinFamily.Equals(windowFamily, StringComparison.OrdinalIgnoreCase))
        {
            // One package can hold several apps (Word and Excel), and two full ids that differ are two of them.
            bool bothFullIds = FamilyOfAppId(pinAppId) != null && FamilyOfAppId(appId) != null;
            return !bothFullIds;
        }

        return pinExe != null && exe != null && pinExe.Equals(exe, StringComparison.OrdinalIgnoreCase) && !IsWebApp(appId);
    }
}
