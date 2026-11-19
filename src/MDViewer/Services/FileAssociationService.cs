using Microsoft.Win32;

namespace MDViewer.Services;

/// <summary>
/// Registers the unpackaged app under HKCU so it appears in Windows "Open with".
/// </summary>
public static class FileAssociationService
{
    private const string ProgId = "MDViewer.Markdown";
    private const string AppKeyName = "MDViewer.exe";

    public static void EnsureRegistered()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return;
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (!File.Exists(iconPath))
            {
                iconPath = exePath;
            }

            var command = $"\"{exePath}\" \"%1\"";
            var icon = $"\"{iconPath}\",0";

            var cmdKey = $@"Software\Classes\Applications\{AppKeyName}\shell\open\command";
            using (var existing = Registry.CurrentUser.OpenSubKey(cmdKey))
            {
                if (existing?.GetValue(null) is string current
                    && string.Equals(current, command, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{AppKeyName}"))
            {
                app?.SetValue("FriendlyAppName", "Просмотр Markdown");
                app?.SetValue("ApplicationCompany", "MDViewer");
            }

            using (var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{AppKeyName}\DefaultIcon"))
            {
                iconKey?.SetValue(null, icon);
            }

            using (var cmd = Registry.CurrentUser.CreateSubKey(cmdKey))
            {
                cmd?.SetValue(null, command);
            }

            using (var types = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{AppKeyName}\SupportedTypes"))
            {
                foreach (var ext in FileService.MarkdownExtensions)
                {
                    types?.SetValue(ext, string.Empty);
                }
            }

            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog?.SetValue(null, "Markdown-файл");
                prog?.SetValue("FriendlyTypeName", "Markdown-файл");
            }

            using (var progIcon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
            {
                progIcon?.SetValue(null, icon);
            }

            using (var progCmd = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            {
                progCmd?.SetValue(null, command);
            }

            foreach (var ext in FileService.MarkdownExtensions)
            {
                using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                openWith?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

                using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}");
                if (extKey is not null && extKey.GetValue(null) is null)
                {
                    extKey.SetValue(null, ProgId);
                }
            }
        }
        catch
        {
            // Association is best-effort; app still runs without it.
        }
    }

    public static void Unregister()
    {
        try
        {
            TryDeleteSubKeyTree($@"Software\Classes\Applications\{AppKeyName}");
            TryDeleteSubKeyTree($@"Software\Classes\{ProgId}");

            foreach (var ext in FileService.MarkdownExtensions)
            {
                using (var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true))
                {
                    openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                }

                using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", writable: true);
                if (extKey?.GetValue("") is string def
                    && string.Equals(def, ProgId, StringComparison.OrdinalIgnoreCase))
                {
                    extKey.DeleteValue("", throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteSubKeyTree(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }

    public static string? FindMarkdownPath(string[] args) =>
        FindMarkdownTarget(args).Path;

    public static (string? Path, string? Fragment) FindMarkdownTarget(string[] args)
    {
        // args[0] is the executable.
        for (var i = 1; i < args.Length; i++)
        {
            var candidate = args[i].Trim().Trim('"');
            if (candidate.StartsWith('-') || candidate.StartsWith('/'))
            {
                continue;
            }

            string? fragment = null;
            var hash = candidate.IndexOf('#');
            if (hash >= 0)
            {
                fragment = candidate[(hash + 1)..];
                candidate = candidate[..hash];
            }

            try
            {
                candidate = FileService.NormalizePath(candidate);
            }
            catch
            {
                continue;
            }

            if (FileService.IsMarkdownPath(candidate) && File.Exists(candidate))
            {
                return (candidate, string.IsNullOrWhiteSpace(fragment) ? null : fragment);
            }
        }

        return (null, null);
    }
}
