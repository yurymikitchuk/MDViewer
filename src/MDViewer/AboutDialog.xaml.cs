using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MDViewer;

/// <summary>
/// In-window About UI. A separate modal Window blanks WebView2 on the owner HWND.
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    public AboutDialog()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionText.Text = "Номер версии: " + version;
    }

    public static async Task ShowAsync(XamlRoot xamlRoot, ElementTheme theme = ElementTheme.Default)
    {
        var dialog = new AboutDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme
        };
        await dialog.ShowAsync();
    }
}
