using System.Text.Json;

namespace MDViewer.Services;

public sealed class AppSettings
{
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 760;
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; } = int.MinValue;
    public bool Maximized { get; set; }
    public bool TocVisible { get; set; } = true;
    /// <summary>Max heading level shown in TOC: 1–4.</summary>
    public int TocMaxLevel { get; set; } = 4;
    /// <summary>TOC panel width in DIPs.</summary>
    public double TocWidth { get; set; } = 260;
    public bool StatusBarVisible { get; set; } = true;
    /// <summary>System | Light | Dark</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Viewer zoom factor (0.5–2.0).</summary>
    public double Zoom { get; set; } = 1.0;
}

public static class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static AppSettings? _cached;

    private static string StorePath => AppData.GetPath("settings.json");

    public static AppSettings Load()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return Clone(_cached!);
        }
    }

    public static void Update(Action<AppSettings> mutate)
    {
        lock (Gate)
        {
            EnsureLoaded();
            mutate(_cached!);
            try
            {
                AppData.EnsureRoot();
                File.WriteAllText(StorePath, JsonSerializer.Serialize(_cached, JsonOptions));
            }
            catch
            {
                // ignore persistence errors
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (_cached is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(StorePath))
            {
                _cached = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StorePath));
            }
        }
        catch
        {
            // fall through to defaults
        }

        _cached ??= new AppSettings();
    }

    private static AppSettings Clone(AppSettings s) => new()
    {
        Width = s.Width,
        Height = s.Height,
        X = s.X,
        Y = s.Y,
        Maximized = s.Maximized,
        TocVisible = s.TocVisible,
        TocMaxLevel = Math.Clamp(s.TocMaxLevel <= 0 ? 4 : s.TocMaxLevel, 1, 4),
        TocWidth = s.TocWidth <= 0 ? 260 : Math.Clamp(s.TocWidth, 160, 560),
        StatusBarVisible = s.StatusBarVisible,
        Theme = string.IsNullOrWhiteSpace(s.Theme) ? "System" : s.Theme,
        Zoom = s.Zoom is > 0 ? Math.Clamp(s.Zoom, 0.5, 2.0) : 1.0
    };
}
