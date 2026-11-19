using System.Text.Json;

namespace MDViewer.Services;

public static class RecentFilesService
{
    private const int MaxItems = 12;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static List<string>? _cache;

    private static string StorePath => AppData.GetPath("recent.json");

    public static IReadOnlyList<string> Load()
    {
        lock (Gate)
        {
            EnsureLoaded();
            PruneMissingUnlocked();
            return _cache!.ToList();
        }
    }

    public static void Add(string path)
    {
        lock (Gate)
        {
            try
            {
                path = FileService.NormalizePath(path);
                EnsureLoaded();
                _cache!.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                _cache.Insert(0, path);
                if (_cache.Count > MaxItems)
                {
                    _cache.RemoveRange(MaxItems, _cache.Count - MaxItems);
                }

                PersistUnlocked();
            }
            catch
            {
                // ignore persistence errors
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                _cache = [];
                if (File.Exists(StorePath))
                {
                    File.Delete(StorePath);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void EnsureLoaded()
    {
        if (_cache is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(StorePath))
            {
                _cache = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StorePath)) ?? [];
            }
        }
        catch
        {
            // fall through
        }

        _cache ??= [];
        if (_cache.Count > MaxItems)
        {
            _cache = _cache.Take(MaxItems).ToList();
        }
    }

    private static void PruneMissingUnlocked()
    {
        var before = _cache!.Count;
        _cache.RemoveAll(p => !File.Exists(p));
        if (_cache.Count != before)
        {
            PersistUnlocked();
        }
    }

    private static void PersistUnlocked()
    {
        AppData.EnsureRoot();
        File.WriteAllText(StorePath, JsonSerializer.Serialize(_cache, JsonOptions));
    }
}
