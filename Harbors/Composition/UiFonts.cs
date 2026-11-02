namespace Harbor.Composition;

/// <summary>
/// Windows 11's fonts, or their Windows 10 equivalents where those aren't installed. Text would fall back by itself,
/// but icons wouldn't: a missing icon font draws empty boxes.
/// </summary>
internal static class UiFonts
{
    static string? s_text, s_display, s_icons;

    public static string Text => s_text ??= Pick("Segoe UI Variable Text", "Segoe UI");
    public static string Display => s_display ??= Pick("Segoe UI Variable Display", "Segoe UI");
    public static string Icons => s_icons ??= Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

    static string Pick(string wanted, string fallback)
    {
        using var fonts = CompositionHost.Instance.DWrite.GetSystemFontCollection(false);
        return fonts.FindFamilyName(wanted, out _) ? wanted : fallback;
    }
}
