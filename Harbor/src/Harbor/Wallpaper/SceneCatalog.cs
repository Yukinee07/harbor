namespace Harbor.Wallpaper;

internal enum SceneCost { Low, Medium, High }

internal sealed record SceneInfo(string Id, string Name, string Description, SceneCost Cost, bool UsesVideo = false, bool UsesSimulation = false, bool MusicReactive = true, bool UsesBake = false);

internal static class SceneCatalog
{
    public static readonly SceneInfo[] All =
    {
        new("aurora", "Aurora", "Northern lights over mountain ridges. The lights lean toward your cursor.", SceneCost.Medium),
        new("sakura", "Sakura", "Cherry trees in full bloom beside a still lake. Move the mouse to blow the falling petals around.", SceneCost.Medium, UsesBake: true),
        new("pool", "Pool", "A water surface you can touch. The cursor leaves a wake, clicks splash, rain falls softly.", SceneCost.Medium, UsesSimulation: true),
        new("silk", "Silk", "Slow folds of satin in dusk colours. The cursor twists the fabric like a finger in liquid.", SceneCost.High),
        new("nebula", "Nebula", "Drifting gas clouds and star layers. A gentle gravitational lens follows the cursor.", SceneCost.Medium),
        new("lava", "Lava", "Lava-lamp wax that rises and merges. Blobs drift toward the warmth of your cursor.", SceneCost.Low),
        new("constellation", "Constellation", "A drifting web of stars that links up to your cursor.", SceneCost.Medium),
        new("contours", "Contours", "A living topographic map. Your cursor raises a hill wherever it goes.", SceneCost.Medium),
        new("petals", "Petals", "Blossoms drifting through evening light. Move the mouse to blow them around.", SceneCost.Medium),
        new("drift", "Drift", "Bokeh lights rising through haze. Lights near the cursor scatter away.", SceneCost.Medium),
        new("bloom", "Bloom", "A soft mesh gradient of colour. The lightest scene, ideal on battery.", SceneCost.Low),
        new("video", "Your video", "Any MP4/MOV/MKV, 4K included, hardware decoded, with cursor parallax and ripples.", SceneCost.Low, UsesVideo: true),
    };

    public static SceneInfo Get(string id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];
}
