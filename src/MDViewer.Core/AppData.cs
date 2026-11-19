namespace MDViewer.Services;

public static class AppData
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MDViewer");

    public static string GetPath(string fileName) => Path.Combine(Root, fileName);

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
