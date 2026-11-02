using System.IO;

namespace Harbor;

internal static class Log
{
    static readonly object s_lock = new();
    /// <summary>Developer aid: HARBOR_PROFILE points all of Harbor's own files at a folder, to try a first run on a used PC.</summary>
    public static string? TestProfile { get; } = Environment.GetEnvironmentVariable("HARBOR_PROFILE");
    public static string Directory { get; } = TestProfile != null
        ? Path.Combine(TestProfile, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Harbor");
    static readonly string s_path = Path.Combine(Directory, "harbor.log");

    static Log()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(s_path) && new FileInfo(s_path).Length > 2_000_000)
                File.Delete(s_path);
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (s_lock)
        {
            try { File.AppendAllText(s_path, line + Environment.NewLine); } catch { }
        }
    }
}
