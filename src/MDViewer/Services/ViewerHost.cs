using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace MDViewer.Services;

public sealed class ViewerMessage
{
    public required string Type { get; init; }
    public required JsonElement Root { get; init; }
}

public sealed class FindResultEventArgs : EventArgs
{
    public int Count { get; init; }
    public int Index { get; init; }
    public bool Cleared { get; init; }
    public bool Capped { get; init; }
}

/// <summary>
/// WebView2 + viewer.js protocol. Keeps virtual hosts, messaging, and print away from the window.
/// </summary>
public sealed class ViewerHost
{
    public const string ShellHost = "mdviewer.localhost";

    private static readonly JsonSerializerOptions JsonOptions = new();

    private WebView2? _viewer;
    private bool _configured;
    private string? _mappedDocDir;
    private string? _pendingHtml;
    private bool _pendingHasMermaid;
    private string? _pendingFragment;
    private string? _lastHtml;
    private bool _lastHasMermaid;
    private TaskCompletionSource<string?>? _contentRequest;

    public bool IsReady { get; private set; }
    public bool HasCore => _viewer?.CoreWebView2 is not null;
    public double Zoom { get; private set; } = 1.0;
    public string Theme { get; private set; } = "System";

    public event EventHandler<ViewerMessage>? MessageReceived;
    public event EventHandler? Ready;
    public event EventHandler<string>? OpenMarkdownRequested;
    public event EventHandler<FindResultEventArgs>? FindResultReceived;
    public event EventHandler<double>? ZoomChanged;

    public async Task InitializeAsync(WebView2 viewer)
    {
        _viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));

        var userData = Path.Combine(AppData.Root, "WebView2");
        Directory.CreateDirectory(userData);
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            userData,
            EnvironmentVariableTarget.Process);

        await viewer.EnsureCoreWebView2Async();
        Configure();
        viewer.CoreWebView2.Navigate($"https://{ShellHost}/viewer.html");
    }

    public void SetTheme(string theme)
    {
        Theme = theme;
        Post(new { type = "setTheme", theme = theme.ToLowerInvariant() });
    }

    public void SetZoom(double zoom)
    {
        var next = Math.Clamp(zoom, 0.5, 2.0);
        if (Math.Abs(next - Zoom) < 0.0001)
        {
            Post(new { type = "setZoom", zoom = Zoom });
            return;
        }

        Zoom = next;
        Post(new { type = "setZoom", zoom = Zoom });
        ZoomChanged?.Invoke(this, Zoom);
    }

    public void ZoomBy(double delta) => SetZoom(Zoom + delta);

    public void ScrollTo(string id) => Post(new { type = "scrollTo", id });

    public void ScrollTop() => Post(new { type = "scrollTop" });

    public void Find(string query) => Post(new { type = "find", query });

    public void FindNext() => Post(new { type = "findNext" });

    public void FindPrev() => Post(new { type = "findPrev" });

    public void FindClear() => Post(new { type = "findClear" });

    public void ShowContent(
        string html,
        bool hasMermaid,
        bool restoreScroll = false,
        string? fragment = null)
    {
        _lastHtml = html;
        _lastHasMermaid = hasMermaid;

        if (!IsReady || _viewer?.CoreWebView2 is null)
        {
            _pendingHtml = html;
            _pendingHasMermaid = hasMermaid;
            _pendingFragment = fragment;
            return;
        }

        Post(new
        {
            type = "setContent",
            html,
            hasMermaid,
            restoreScroll,
            fragment
        });
    }

    /// <summary>
    /// Re-push last document after WebView2 HWND blanking. Skips TOC rebuild to avoid flicker.
    /// </summary>
    public void RepublishLastContent()
    {
        if (_lastHtml is null || !IsReady || _viewer?.CoreWebView2 is null)
        {
            return;
        }

        Post(new
        {
            type = "setContent",
            html = _lastHtml,
            hasMermaid = _lastHasMermaid,
            restoreScroll = true,
            skipToc = true,
            fragment = (string?)null
        });
    }

    public void ClearCachedContent()
    {
        _lastHtml = null;
        _lastHasMermaid = false;
        _pendingHtml = null;
        _pendingFragment = null;
    }

    public async Task<bool> HasPaintedContentAsync()
    {
        if (_viewer?.CoreWebView2 is null || !IsReady)
        {
            return false;
        }

        try
        {
            var json = await _viewer.CoreWebView2.ExecuteScriptAsync(
                "(function(){var c=document.getElementById('content');" +
                "return !!(c&&c.classList.contains('ready')&&(c.innerText||'').trim().length>0);})()");
            return string.Equals(json, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    public void MapDocumentFolder(string? directory)
    {
        if (_viewer?.CoreWebView2 is null
            || string.IsNullOrWhiteSpace(directory)
            || !Directory.Exists(directory))
        {
            return;
        }

        var full = Path.GetFullPath(directory);
        if (string.Equals(_mappedDocDir, full, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _viewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
            MarkdownService.DocumentHost,
            full,
            CoreWebView2HostResourceAccessKind.Allow);
        _mappedDocDir = full;
    }

    public async Task<string?> GetHtmlAsync(int timeoutMs = 2000) =>
        await RequestContentAsync("getHtml", timeoutMs).ConfigureAwait(true);

    public async Task<string?> GetTextAsync(int timeoutMs = 2000) =>
        await RequestContentAsync("getText", timeoutMs).ConfigureAwait(true);

    public void ShowPrintUi()
    {
        _viewer?.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    }

    public async Task<bool> PrintToPdfAsync(string path)
    {
        if (_viewer?.CoreWebView2 is null)
        {
            return false;
        }

        var settings = _viewer.CoreWebView2.Environment.CreatePrintSettings();
        settings.ShouldPrintHeaderAndFooter = false;
        settings.ShouldPrintBackgrounds = true;
        return await _viewer.CoreWebView2.PrintToPdfAsync(path, settings);
    }

    private async Task<string?> RequestContentAsync(string type, int timeoutMs)
    {
        FindClear();
        _contentRequest = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Post(new { type });
            var completed = await Task.WhenAny(_contentRequest.Task, Task.Delay(timeoutMs));
            return completed == _contentRequest.Task ? await _contentRequest.Task : null;
        }
        finally
        {
            _contentRequest = null;
        }
    }

    private void Configure()
    {
        if (_viewer?.CoreWebView2 is null || _configured)
        {
            return;
        }

        var core = _viewer.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            ShellHost,
            Path.Combine(AppContext.BaseDirectory, "Assets", "viewer"),
            CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.WebMessageReceived += Core_WebMessageReceived;
        core.NavigationStarting += Core_NavigationStarting;
        core.NewWindowRequested += Core_NewWindowRequested;
        _configured = true;
    }

    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        var shellPrefix = $"https://{ShellHost}/";
        var toShell = args.Uri.StartsWith(shellPrefix, StringComparison.OrdinalIgnoreCase);

        // Context-menu Refresh / Ctrl+R reloads empty viewer.html and wipes injected markdown.
        // Soft-refresh instead: keep the shell, re-push the last document.
        if (args.IsUserInitiated && toShell && _lastHtml is not null && IsReady)
        {
            args.Cancel = true;
            RepublishLastContent();
            return;
        }

        if (!args.IsUserInitiated)
        {
            return;
        }

        if (!toShell)
        {
            args.Cancel = true;
            _ = LaunchExternalAsync(args.Uri);
        }
    }

    private void Core_NewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        _ = LaunchExternalAsync(args.Uri);
    }

    private static async Task LaunchExternalAsync(string uri)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(uri));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            var type = typeEl.GetString() ?? "";
            switch (type)
            {
                case "ready":
                    IsReady = true;
                    SetTheme(Theme);
                    SetZoom(Zoom);
                    // Restore document after shell load or unexpected reload.
                    // _lastHtml is set by ShowContent even while pending the first ready.
                    if (_lastHtml is not null)
                    {
                        var fragment = _pendingFragment;
                        var firstOpen = _pendingHtml is not null;
                        _pendingHtml = null;
                        _pendingFragment = null;
                        if (firstOpen)
                        {
                            ShowContent(_lastHtml, _lastHasMermaid, fragment: fragment);
                        }
                        else
                        {
                            RepublishLastContent();
                        }
                    }

                    Ready?.Invoke(this, EventArgs.Empty);
                    break;

                case "zoomChanged":
                    if (root.TryGetProperty("zoom", out var zoomEl) && zoomEl.TryGetDouble(out var z))
                    {
                        var next = Math.Clamp(z, 0.5, 2.0);
                        if (Math.Abs(next - Zoom) >= 0.0001)
                        {
                            Zoom = next;
                            ZoomChanged?.Invoke(this, Zoom);
                        }
                    }
                    break;

                case "openMarkdown":
                    if (root.TryGetProperty("href", out var hrefEl))
                    {
                        OpenMarkdownRequested?.Invoke(this, hrefEl.GetString() ?? "");
                    }
                    break;

                case "findResult":
                    FindResultReceived?.Invoke(
                        this,
                        new FindResultEventArgs
                        {
                            Count = root.GetProperty("count").GetInt32(),
                            Index = root.GetProperty("index").GetInt32(),
                            Cleared = root.TryGetProperty("cleared", out var cleared) && cleared.ValueKind == JsonValueKind.True,
                            Capped = root.TryGetProperty("capped", out var capped) && capped.ValueKind == JsonValueKind.True
                        });
                    break;

                case "html":
                    _contentRequest?.TrySetResult(
                        root.TryGetProperty("html", out var html) ? html.GetString() : "");
                    break;

                case "text":
                    _contentRequest?.TrySetResult(
                        root.TryGetProperty("text", out var plain) ? plain.GetString() : "");
                    break;
            }

            MessageReceived?.Invoke(this, new ViewerMessage { Type = type, Root = root.Clone() });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void Post(object payload)
    {
        _viewer?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
