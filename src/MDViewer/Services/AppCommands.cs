using Windows.ApplicationModel.DataTransfer;

namespace MDViewer.Services;

/// <summary>
/// UI-facing command helpers shared by menus/accelerators.
/// </summary>
public sealed class AppCommands
{
    private readonly DocumentSession _session;
    private readonly ViewerHost _viewer;

    public AppCommands(DocumentSession session, ViewerHost viewer)
    {
        _session = session;
        _viewer = viewer;
    }

    public DocumentSession Session => _session;
    public ViewerHost Viewer => _viewer;

    public void ZoomIn() => _viewer.ZoomBy(0.1);

    public void ZoomOut() => _viewer.ZoomBy(-0.1);

    public void ZoomReset() => _viewer.SetZoom(1.0);

    public void ScrollTop() => _viewer.ScrollTop();

    public void ScrollToHeading(string id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            _viewer.ScrollTo(id);
        }
    }

    public async Task<bool> CopyViewerTextAsync()
    {
        var text = await _viewer.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        SetClipboard(text);
        return true;
    }

    public async Task<bool> CopyViewerHtmlAsync()
    {
        var html = await _viewer.GetHtmlAsync();
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        SetClipboard(html);
        return true;
    }

    public static void CopyText(string text) => SetClipboard(text);

    public static string FormatTocMarkdown(IEnumerable<(int Level, string Text, string Id)> items) =>
        string.Join(
            Environment.NewLine,
            items.Select(t =>
            {
                var indent = new string(' ', Math.Max(0, t.Level - 1) * 2);
                return $"{indent}- [{t.Text}](#{t.Id})";
            }));

    private static void SetClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }
}
