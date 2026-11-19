using System.Diagnostics;

namespace MDViewer.Services;

public sealed class DocumentLoadRequest
{
    public required string Path { get; init; }
    public bool IsReload { get; init; }
    public bool AddToRecent { get; init; } = true;
    public string? DisplayTitle { get; init; }
    public string? Fragment { get; init; }
    public string? HelpRoot { get; init; }
}

public sealed class DocumentLoadResult
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public required string HtmlFragment { get; init; }
    public required bool HasMermaid { get; init; }
    public required long FileSize { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public string? DocumentDirectory { get; init; }
    public string? Fragment { get; init; }
    public bool IsReload { get; init; }
    public bool AddToRecent { get; init; }
    public bool IsBuiltInHelp { get; init; }
    /// <summary>Reload skipped: size/mtime unchanged (spurious watcher / Explorer touch).</summary>
    public bool SkippedUnchanged { get; init; }
}

public sealed class DocumentLoadException : Exception
{
    public DocumentLoadException(string message) : base(message)
    {
    }
}

/// <summary>
/// Owns the current document path, open/reload pipeline, and file watcher.
/// UI-agnostic: marshal watcher callbacks via <see cref="MarshalToUi"/>.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    public const long MaxOpenFileBytes = 12L * 1024 * 1024;

    private readonly MarkdownService _markdown;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private int _openVersion;
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;
    private long _loadedLength = -1;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private bool _disposed;

    public DocumentSession(MarkdownService? markdown = null)
    {
        _markdown = markdown ?? new MarkdownService();
    }

    /// <summary>Schedules work on the UI thread (required for watcher → reload).</summary>
    public Action<Action>? MarshalToUi { get; set; }

    public string? CurrentPath { get; private set; }
    public string? DisplayTitle { get; private set; }
    public long FileSize { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public bool IsOpen => !string.IsNullOrWhiteSpace(CurrentPath);

    public event EventHandler? ReloadRequested;
    public event EventHandler<string>? PathRenamed;

    public async Task<DocumentLoadResult> LoadAsync(
        DocumentLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var version = Interlocked.Increment(ref _openVersion);
        try
        {
            var path = FileService.NormalizePath(request.Path);
            if (!File.Exists(path))
            {
                throw new DocumentLoadException("Файл не найден.");
            }

            var info = new FileInfo(path);
            if (info.Length > MaxOpenFileBytes)
            {
                throw new DocumentLoadException(
                    $"Файл слишком большой для просмотра ({info.Length / (1024.0 * 1024.0):0.#} МБ).\n" +
                    $"Лимит: {MaxOpenFileBytes / (1024 * 1024)} МБ.");
            }

            var isHelp = IsUnderHelpRoot(path, request.HelpRoot);
            var addToRecent = request.AddToRecent && !isHelp;
            var title = string.IsNullOrWhiteSpace(request.DisplayTitle)
                ? (isHelp ? "Синтаксис Markdown" : Path.GetFileName(path))
                : request.DisplayTitle!;

            var docDir = Path.GetDirectoryName(path);

            // Explorer "select" / AV often bumps metadata without content change → skip re-render.
            if (request.IsReload
                && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)
                && info.Length == _loadedLength
                && info.LastWriteTimeUtc == _loadedWriteTimeUtc)
            {
                return new DocumentLoadResult
                {
                    Path = path,
                    Title = DisplayTitle ?? title,
                    HtmlFragment = "",
                    HasMermaid = false,
                    FileSize = info.Length,
                    ElapsedMilliseconds = 0,
                    OpenedAt = OpenedAt,
                    DocumentDirectory = docDir,
                    Fragment = request.Fragment,
                    IsReload = true,
                    AddToRecent = false,
                    IsBuiltInHelp = isHelp,
                    SkippedUnchanged = true
                };
            }

            var sw = Stopwatch.StartNew();
            var markdown = await FileService.ReadUtf8Async(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var render = await Task.Run(
                () => _markdown.Render(markdown, docDir),
                cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (version != _openVersion)
            {
                throw new OperationCanceledException("Открыт более новый документ.");
            }

            CurrentPath = path;
            DisplayTitle = title;
            FileSize = info.Length;
            OpenedAt = DateTimeOffset.Now;
            _loadedLength = info.Length;
            _loadedWriteTimeUtc = info.LastWriteTimeUtc;
            AttachWatcher(path);

            return new DocumentLoadResult
            {
                Path = path,
                Title = title,
                HtmlFragment = render.HtmlFragment,
                HasMermaid = render.HasMermaid,
                FileSize = info.Length,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                OpenedAt = OpenedAt,
                DocumentDirectory = docDir,
                Fragment = request.Fragment,
                IsReload = request.IsReload,
                AddToRecent = addToRecent,
                IsBuiltInHelp = isHelp
            };
        }
        finally
        {
            _openGate.Release();
        }
    }

    public Task<DocumentLoadResult> ReloadAsync(
        string? helpRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            throw new DocumentLoadException("Нет открытого файла.");
        }

        return LoadAsync(
            new DocumentLoadRequest
            {
                Path = CurrentPath,
                IsReload = true,
                AddToRecent = false,
                DisplayTitle = DisplayTitle,
                HelpRoot = helpRoot
            },
            cancellationToken);
    }

    public static bool IsUnderHelpRoot(string path, string? helpRoot)
    {
        if (string.IsNullOrWhiteSpace(helpRoot))
        {
            return false;
        }

        var root = Path.GetFullPath(helpRoot);
        var full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, root, StringComparison.OrdinalIgnoreCase);
    }

    private void AttachWatcher(string path)
    {
        try
        {
            if (_watcher is not null
                && string.Equals(_watchedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _watcher?.Dispose();
            _watcher = null;
            _watchedPath = null;

            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            {
                return;
            }

            void scheduleReload() =>
                Marshal(() => ReloadRequested?.Invoke(this, EventArgs.Empty));

            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += (_, _) => scheduleReload();
            _watcher.Renamed += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.FullPath))
                {
                    return;
                }

                Marshal(() =>
                {
                    CurrentPath = e.FullPath;
                    _loadedLength = -1;
                    _loadedWriteTimeUtc = DateTime.MinValue;
                    PathRenamed?.Invoke(this, e.FullPath);
                    AttachWatcher(e.FullPath);
                    ReloadRequested?.Invoke(this, EventArgs.Empty);
                });
            };
            _watcher.EnableRaisingEvents = true;
            _watchedPath = path;
        }
        catch
        {
            // watcher is best-effort
        }
    }

    private void Marshal(Action action)
    {
        var marshal = MarshalToUi;
        if (marshal is null)
        {
            action();
            return;
        }

        marshal(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        _openGate.Dispose();
    }
}
