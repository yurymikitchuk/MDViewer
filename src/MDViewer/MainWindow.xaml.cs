using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using MDViewer.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace MDViewer;

public sealed class TocItem
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public int Level { get; init; }
    public Thickness Indent { get; init; }
}

public sealed partial class MainWindow : Window
{
    private readonly DocumentSession _session = new();
    private readonly ViewerHost _viewerHost = new();
    private readonly AppCommands _commands;
    private readonly ObservableCollection<TocItem> _tocItems = [];
    private readonly List<TocItem> _allTocItems = [];
    private readonly DispatcherTimer _watcherDebounce;
    private readonly DispatcherTimer _settingsSaveDebounce;
    private readonly DispatcherTimer _statusFlashTimer;
    private readonly DispatcherTimer _findDebounce;

    private string? _lastFindQuery;
    private bool _tocPreference = true;
    private int _tocMaxLevel = 4;
    private double _tocWidth = DefaultTocWidth;
    private string? _activeTocId;
    private TocItem? _contextTocItem;
    private bool _tocContextFromItem;
    private bool _statusBarVisible = true;
    private string _theme = "System";
    private bool _settingsReady;
    private bool _suppressTocClick;
    private bool _tocResizing;
    private double _tocResizeStartX;
    private double _tocResizeStartWidth;

    private const double DefaultTocWidth = 260;
    private const double MinTocWidth = 160;
    private const double MaxTocWidth = 560;
    private const double TocWidthStep = 40;
    private const int EmptyRecentLimit = 8;

    public MainWindow()
    {
        _commands = new AppCommands(_session, _viewerHost);

        InitializeComponent();
        TocList.ItemsSource = _tocItems;
        // ListView / empty hint swallow right-clicks; share the panel flyout for the whole TOC.
        TocList.ContextFlyout = TocContextFlyout;
        TocEmptyHint.ContextFlyout = TocContextFlyout;
        RegisterWindowAccelerators();
        WireSessionAndViewer();

        if (!string.IsNullOrWhiteSpace(App.LaunchFilePath))
        {
            EmptyState.Visibility = Visibility.Collapsed;
            StatusText.Text = "Открытие…";
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDrag);
        TrySetAppIcon();

        var settings = AppSettingsService.Load();
        _tocPreference = settings.TocVisible;
        _tocMaxLevel = Math.Clamp(settings.TocMaxLevel <= 0 ? 4 : settings.TocMaxLevel, 1, 4);
        _tocWidth = Math.Clamp(settings.TocWidth <= 0 ? DefaultTocWidth : settings.TocWidth, MinTocWidth, MaxTocWidth);
        TocToggleMenuItem.IsChecked = settings.TocVisible;
        SetStatusBarVisible(settings.StatusBarVisible);
        ApplyTheme(settings.Theme, persist: false);
        ApplyWindowSettings(settings);

        _watcherDebounce = CreateDebounceTimer(400, async () =>
        {
            if (_session.IsOpen)
            {
                await OpenPathAsync(_session.CurrentPath!, isReload: true);
            }
        });
        _settingsSaveDebounce = CreateDebounceTimer(400, PersistWindowSettings);
        _statusFlashTimer = CreateDebounceTimer(2500, RefreshDocumentStatus);
        _findDebounce = CreateDebounceTimer(200, RunFindFromBox);

        // After debounce timers exist (ZoomChanged may schedule a save).
        _viewerHost.SetZoom(settings.Zoom);

        AppWindow.Changed += AppWindow_Changed;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
        // System pickers / print UI / external browser can blank WebView2 while focused away.
        Activated += MainWindow_ActivationChanged;

        // MenuBarItem has no Opening event; refresh before the File flyout is shown.
        FileMenuBarItem.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => RebuildRecentMenu()),
            handledEventsToo: true);
        RebuildRecentMenu();

        _settingsReady = true;
    }

    private void WireSessionAndViewer()
    {
        _session.MarshalToUi = action => DispatcherQueue.TryEnqueue(() => action());
        _session.ReloadRequested += (_, _) =>
        {
            _watcherDebounce.Stop();
            _watcherDebounce.Start();
        };
        _session.PathRenamed += (_, newPath) =>
        {
            if (!DocumentSession.IsUnderHelpRoot(newPath, GetHelpRoot()))
            {
                var fileName = Path.GetFileName(newPath);
                TitleText.Text = fileName;
                Title = fileName + " — Просмотр Markdown";
            }
        };

        _viewerHost.MessageReceived += ViewerHost_MessageReceived;
        _viewerHost.OpenMarkdownRequested += (_, href) => _ = OpenMarkdownLinkAsync(href);
        _viewerHost.ZoomChanged += (_, _) =>
        {
            if (!_settingsReady)
            {
                return;
            }

            _settingsSaveDebounce.Stop();
            _settingsSaveDebounce.Start();
        };
        _viewerHost.FindResultReceived += (_, result) =>
        {
            // findClear / empty query also report count=0 — don't show "Нет совпадений" then.
            if (FindBar.Visibility != Visibility.Visible || result.Cleared)
            {
                FindStatus.Text = "";
                return;
            }

            if (result.Count <= 0)
            {
                FindStatus.Text = "Нет совпадений";
                return;
            }

            FindStatus.Text = result.Capped
                ? $"{result.Index + 1} / {result.Count}+"
                : $"{result.Index + 1} / {result.Count}";
        };
    }

    private static string GetHelpRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "help"));

    private static DispatcherTimer CreateDebounceTimer(int ms, Func<Task> action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await action();
        };
        return timer;
    }

    private static DispatcherTimer CreateDebounceTimer(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    private void TrySetAppIcon()
    {
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch
        {
            // optional
        }
    }

    private void RegisterWindowAccelerators()
    {
        // Accelerators on MenuFlyoutItem inside AttachedFlyout often do not fire
        // (especially with WebView2 focus). Bind them on the window root instead.
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, args =>
        {
            _ = PickAndOpenAsync();
            args.Handled = true;
        });
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, args =>
        {
            FindMenu_Click(FindMenuItem, new RoutedEventArgs());
            args.Handled = true;
        });
        AddAccelerator(VirtualKey.P, VirtualKeyModifiers.Control, args =>
        {
            PrintMenu_Click(PrintMenuItem, new RoutedEventArgs());
            args.Handled = true;
        });
        AddAccelerator(VirtualKey.E, VirtualKeyModifiers.Control, args =>
        {
            _ = ExportPdfAsync();
            args.Handled = true;
        });
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, args =>
        {
            _ = ReloadFromAcceleratorAsync();
            args.Handled = true;
        });
    }

    private void AddAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Action<KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accel = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accel.Invoked += (_, args) => handler(args);
        RootGrid.KeyboardAccelerators.Add(accel);
    }

    private async Task ReloadFromAcceleratorAsync()
    {
        if (!string.IsNullOrWhiteSpace(_session.CurrentPath))
        {
            await OpenPathAsync(_session.CurrentPath, isReload: true);
        }
    }

    private void ApplyWindowSettings(AppSettings settings)
    {
        try
        {
            var size = new SizeInt32(
                (int)Math.Clamp(settings.Width, 640, 10000),
                (int)Math.Clamp(settings.Height, 480, 10000));
            AppWindow.Resize(size);

            if (TryGetOnScreenPosition(settings.X, settings.Y, size, out var pos))
            {
                AppWindow.Move(pos);
            }

            if (settings.Maximized && AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.Maximize();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Rejects minimized sentinel coords (-32000) and positions with no title-bar overlap on any monitor.
    /// </summary>
    private static bool TryGetOnScreenPosition(int x, int y, SizeInt32 size, out PointInt32 pos)
    {
        pos = default;
        if (x == int.MinValue || y == int.MinValue || x <= -10_000 || y <= -10_000)
        {
            return false;
        }

        pos = new PointInt32(x, y);
        const int titleBarProbe = 32;
        var probe = new PointInt32(x + Math.Min(80, size.Width / 2), y + titleBarProbe / 2);
        var area = DisplayArea.GetFromPoint(probe, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        var intersects =
            x + size.Width > work.X + 40
            && x < work.X + work.Width - 40
            && y + titleBarProbe > work.Y
            && y < work.Y + work.Height - 40;

        if (!intersects)
        {
            pos = new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2));
        }

        return true;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_settingsReady)
        {
            return;
        }

        if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
        {
            _settingsSaveDebounce.Stop();
            _settingsSaveDebounce.Start();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _settingsSaveDebounce.Stop();
        _watcherDebounce.Stop();
        _statusFlashTimer.Stop();
        _findDebounce.Stop();
        PersistWindowSettings();
        _session.Dispose();
    }

    private void PersistWindowSettings()
    {
        try
        {
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            var minimized = presenter?.State == OverlappedPresenterState.Minimized;
            // Minimized windows report Position=(-32000,-32000) and a tiny Size — never persist that.
            if (minimized)
            {
                return;
            }

            var maximized = presenter?.State == OverlappedPresenterState.Maximized;
            var size = AppWindow.Size;
            var pos = AppWindow.Position;

            AppSettingsService.Update(s =>
            {
                if (!maximized && pos.X > -10_000 && pos.Y > -10_000
                    && size.Width >= 640 && size.Height >= 480)
                {
                    s.Width = size.Width;
                    s.Height = size.Height;
                    s.X = pos.X;
                    s.Y = pos.Y;
                }

                s.Maximized = maximized;
                s.TocVisible = _tocPreference;
                s.TocMaxLevel = _tocMaxLevel;
                s.TocWidth = _tocWidth;
                s.StatusBarVisible = _statusBarVisible;
                s.Theme = _theme;
                s.Zoom = _viewerHost.Zoom;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public Task OpenFromActivationAsync(string? path)
    {
        Activate();
        AppWindow.Show();
        return TryOpenPathAsync(path);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await PrewarmViewerAsync();
        await TryOpenPathAsync(App.LaunchFilePath, App.LaunchFragment);
    }

    private async void MainWindow_ActivationChanged(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        if (Viewer.Visibility != Visibility.Visible)
        {
            return;
        }

        // Only restore when WebView2 actually blanked (print/browser). Skip on normal Alt-Tab
        // so TOC does not rebuild/flicker.
        if (!await _viewerHost.HasPaintedContentAsync())
        {
            _viewerHost.RepublishLastContent();
        }
    }

    private Task TryOpenPathAsync(string? path, string? fragment = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        var hash = path.IndexOf('#');
        if (hash >= 0)
        {
            fragment ??= path[(hash + 1)..];
            path = path[..hash];
        }

        if (!FileService.IsMarkdownPath(path) || !File.Exists(path))
        {
            return Task.CompletedTask;
        }

        return OpenPathAsync(FileService.NormalizePath(path), fragment: fragment);
    }

    private async Task PrewarmViewerAsync()
    {
        try
        {
            _viewerHost.SetTheme(_theme);
            await _viewerHost.InitializeAsync(Viewer);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            await ShowErrorAsync(
                "Не удалось инициализировать просмотр (WebView2).\n" + ex.Message);
        }
    }

    private void ViewerHost_MessageReceived(object? sender, ViewerMessage message)
    {
        switch (message.Type)
        {
            case "toc":
                ApplyToc(message.Root);
                break;
            case "tocActive":
                if (message.Root.TryGetProperty("id", out var activeId))
                {
                    _activeTocId = activeId.GetString();
                    SelectTocItem(_activeTocId);
                }
                break;
        }
    }

    private void ApplyToc(JsonElement root)
    {
        _allTocItems.Clear();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var level = item.GetProperty("level").GetInt32();
                _allTocItems.Add(new TocItem
                {
                    Id = item.GetProperty("id").GetString() ?? "",
                    Text = item.GetProperty("text").GetString() ?? "",
                    Level = level,
                    Indent = new Thickness(8 + Math.Max(0, level - 1) * 12, 4, 8, 4)
                });
            }
        }

        RefreshTocList();
        if (_tocItems.Count > 0)
        {
            SelectTocItem(_tocItems[0].Id);
        }
    }

    private void RefreshTocList()
    {
        var previousId = (TocList.SelectedItem as TocItem)?.Id ?? _activeTocId;
        _tocItems.Clear();
        foreach (var item in _allTocItems)
        {
            if (item.Level > _tocMaxLevel)
            {
                continue;
            }

            _tocItems.Add(item);
        }

        TocEmptyHint.Visibility = _allTocItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrEmpty(previousId))
        {
            SelectTocItem(previousId);
        }
    }

    private void TocContextFlyout_Opening(object sender, object e)
    {
        if (!_tocContextFromItem)
        {
            _contextTocItem = null;
        }

        _tocContextFromItem = false;

        TocLevel1MenuItem.IsChecked = _tocMaxLevel == 1;
        TocLevel2MenuItem.IsChecked = _tocMaxLevel == 2;
        TocLevel3MenuItem.IsChecked = _tocMaxLevel == 3;
        TocLevel4MenuItem.IsChecked = _tocMaxLevel == 4;

        TocCopyMenuItem.IsEnabled = _allTocItems.Count > 0;
        TocGoCurrentMenuItem.IsEnabled = !string.IsNullOrEmpty(ResolveContextTocItem()?.Id);
        TocCopyLinkMenuItem.IsEnabled = !string.IsNullOrEmpty(ResolveContextTocItem()?.Id);
    }

    private void TocList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _contextTocItem = null;
        _tocContextFromItem = false;
        if (e.OriginalSource is FrameworkElement fe)
        {
            _contextTocItem = fe.DataContext as TocItem
                ?? (fe.Parent as FrameworkElement)?.DataContext as TocItem;
        }

        if (_contextTocItem is not null)
        {
            _tocContextFromItem = true;
            SelectTocItem(_contextTocItem.Id);
        }
    }

    private TocItem? ResolveContextTocItem() =>
        _contextTocItem
        ?? TocList.SelectedItem as TocItem
        ?? _tocItems.FirstOrDefault(t => t.Id == _activeTocId)
        ?? _allTocItems.FirstOrDefault(t => t.Id == _activeTocId);

    private void TocLevelMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag }
            && int.TryParse(tag, out var level))
        {
            _tocMaxLevel = Math.Clamp(level, 1, 4);
            RefreshTocList();
            AppSettingsService.Update(s => s.TocMaxLevel = _tocMaxLevel);
        }
    }

    private void TocGoTop_Click(object sender, RoutedEventArgs e) =>
        _commands.ScrollTop();

    private void TocGoCurrent_Click(object sender, RoutedEventArgs e)
    {
        var item = ResolveContextTocItem();
        if (item is null || string.IsNullOrEmpty(item.Id))
        {
            return;
        }

        SelectTocItem(item.Id);
        _commands.ScrollToHeading(item.Id);
    }

    private void TocCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_allTocItems.Count == 0)
        {
            return;
        }

        var text = AppCommands.FormatTocMarkdown(
            _allTocItems.Where(t => t.Level <= _tocMaxLevel).Select(t => (t.Level, t.Text, t.Id)));
        AppCommands.CopyText(text);
        FlashStatus("Оглавление скопировано");
    }

    private void TocCopyLink_Click(object sender, RoutedEventArgs e)
    {
        var item = ResolveContextTocItem();
        if (item is null || string.IsNullOrEmpty(item.Id))
        {
            return;
        }

        AppCommands.CopyText("#" + item.Id);
        FlashStatus("Ссылка на заголовок скопирована");
    }

    private void TocWidthNarrower_Click(object sender, RoutedEventArgs e) =>
        ApplyTocWidth(_tocWidth - TocWidthStep, persist: true);

    private void TocWidthWider_Click(object sender, RoutedEventArgs e) =>
        ApplyTocWidth(_tocWidth + TocWidthStep, persist: true);

    private void TocWidthReset_Click(object sender, RoutedEventArgs e) =>
        ApplyTocWidth(DefaultTocWidth, persist: true);

    private void TocHide_Click(object sender, RoutedEventArgs e)
    {
        _tocPreference = false;
        SetTocVisible(false);
        AppSettingsService.Update(s => s.TocVisible = false);
    }

    private void ApplyTocWidth(double width, bool persist)
    {
        _tocWidth = Math.Clamp(width, MinTocWidth, MaxTocWidth);
        if (_tocPreference && TocPanel.Visibility == Visibility.Visible)
        {
            TocColumn.Width = new GridLength(_tocWidth);
        }

        if (persist)
        {
            AppSettingsService.Update(s => s.TocWidth = _tocWidth);
        }
    }

    private void TocSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el)
        {
            return;
        }

        _tocResizing = true;
        _tocResizeStartX = e.GetCurrentPoint(RootGrid).Position.X;
        _tocResizeStartWidth = _tocWidth;
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TocSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_tocResizing)
        {
            return;
        }

        var dx = e.GetCurrentPoint(RootGrid).Position.X - _tocResizeStartX;
        ApplyTocWidth(_tocResizeStartWidth + dx, persist: false);
        e.Handled = true;
    }

    private void TocSplitter_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        EndTocResize(sender as UIElement, e.Pointer);

    private void TocSplitter_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndTocResize(sender as UIElement, e.Pointer);

    private void TocSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_tocResizing)
        {
            _tocResizing = false;
            AppSettingsService.Update(s => s.TocWidth = _tocWidth);
        }
    }

    private void EndTocResize(UIElement? el, Pointer pointer)
    {
        if (!_tocResizing)
        {
            return;
        }

        _tocResizing = false;
        el?.ReleasePointerCapture(pointer);
        AppSettingsService.Update(s => s.TocWidth = _tocWidth);
    }

    private void SelectTocItem(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var match = _tocItems.FirstOrDefault(t => t.Id == id);
        if (match is null)
        {
            return;
        }

        _suppressTocClick = true;
        TocList.SelectedItem = match;
        TocList.ScrollIntoView(match);
        _suppressTocClick = false;
    }

    private async Task OpenMarkdownLinkAsync(string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(_session.CurrentPath))
        {
            return;
        }

        try
        {
            var baseDir = Path.GetDirectoryName(_session.CurrentPath)!;
            var hashIndex = href.IndexOf('#');
            var pathPart = hashIndex >= 0 ? href[..hashIndex] : href;
            var fragment = hashIndex >= 0 ? href[(hashIndex + 1)..] : null;
            if (string.IsNullOrWhiteSpace(pathPart))
            {
                if (!string.IsNullOrWhiteSpace(fragment))
                {
                    _commands.ScrollToHeading(fragment);
                }

                return;
            }

            var candidate = Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(pathPart)));
            if (!FileService.IsMarkdownPath(candidate) || !File.Exists(candidate))
            {
                await ShowErrorAsync("Файл не найден:\n" + candidate);
                return;
            }

            await OpenPathAsync(candidate, fragment: fragment);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось открыть ссылку: " + ex.Message);
        }
    }

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var recent = RecentFilesService.Load();
        if (recent.Count == 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "Пусто", IsEnabled = false });
            RebuildEmptyRecent(recent);
            return;
        }

        foreach (var path in recent)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(path), Tag = path };
            ToolTipService.SetToolTip(item, path);
            item.Click += async (_, _) =>
            {
                if (item.Tag is string p)
                {
                    await OpenPathAsync(p);
                }
            };
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Очистить список" };
        clear.Click += (_, _) =>
        {
            RecentFilesService.Clear();
            RebuildRecentMenu();
        };
        RecentMenu.Items.Add(clear);
        RebuildEmptyRecent(recent);
    }

    private void RebuildEmptyRecent(IReadOnlyList<string>? recent = null)
    {
        EmptyRecentList.Children.Clear();
        recent ??= RecentFilesService.Load();
        var items = recent.Take(EmptyRecentLimit).ToList();
        EmptyRecentPanel.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Flat rows (not HyperlinkButton) so names use secondary text color, not accent blue.
        var rowStyle = (Style)Application.Current.Resources["ChromeFlatButtonStyle"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        foreach (var path in items)
        {
            var row = new Button
            {
                Content = Path.GetFileName(path),
                Tag = path,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 13,
                Foreground = secondary,
                Style = rowStyle
            };
            ToolTipService.SetToolTip(row, path);
            row.Click += async (_, _) =>
            {
                if (row.Tag is string p)
                {
                    await OpenPathAsync(p);
                }
            };
            EmptyRecentList.Children.Add(row);
        }
    }

    private async void OpenMenu_Click(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async void ReloadMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_session.CurrentPath))
        {
            await OpenPathAsync(_session.CurrentPath, isReload: true);
        }
    }

    private void FindMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!HasOpenDocument)
        {
            return;
        }

        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        if (!string.IsNullOrWhiteSpace(FindBox.Text))
        {
            RunFindFromBox();
        }
        else
        {
            _lastFindQuery = null;
            FindStatus.Text = "";
        }
    }

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        _findDebounce.Stop();
        _lastFindQuery = null;
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = "";
        _viewerHost.FindClear();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible)
        {
            return;
        }

        _findDebounce.Stop();
        _findDebounce.Start();
    }

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift)
            {
                FindPrev_Click(sender, e);
            }
            else
            {
                FindNext_Click(sender, e);
            }

            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            FindClose_Click(sender, e);
            e.Handled = true;
        }
    }

    private void RunFindFromBox()
    {
        var query = FindBox.Text ?? "";
        _lastFindQuery = query;
        _viewerHost.Find(query);
    }

    /// <summary>Applies the box text if it differs from the last search. Returns true if a new search was started.</summary>
    private bool EnsureFindApplied()
    {
        var query = FindBox.Text ?? "";
        if (string.Equals(query, _lastFindQuery, StringComparison.Ordinal))
        {
            return false;
        }

        _findDebounce.Stop();
        RunFindFromBox();
        return true;
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        // New/changed query → search lands on first match; otherwise advance.
        if (!EnsureFindApplied())
        {
            _viewerHost.FindNext();
        }
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureFindApplied())
        {
            _viewerHost.FindPrev();
        }
    }

    private async void CopyTextMenu_Click(object sender, RoutedEventArgs e) =>
        await CopyViewerContentAsync(asHtml: false);

    private async void CopyHtmlMenu_Click(object sender, RoutedEventArgs e) =>
        await CopyViewerContentAsync(asHtml: true);

    private async Task CopyViewerContentAsync(bool asHtml)
    {
        if (!HasOpenDocument)
        {
            return;
        }

        var label = asHtml ? "HTML" : "текст";
        try
        {
            var ok = asHtml
                ? await _commands.CopyViewerHtmlAsync()
                : await _commands.CopyViewerTextAsync();
            if (!ok)
            {
                await ShowErrorAsync($"Не удалось получить {label}.");
                return;
            }

            FlashStatus(asHtml ? "HTML скопирован в буфер обмена" : "Текст скопирован в буфер обмена");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Не удалось скопировать {label}: " + ex.Message);
        }
    }

    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => ApplyTheme("System");

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light");

    private void ThemeDark_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark");

    private void ApplyTheme(string theme, bool persist = true)
    {
        _theme = theme switch
        {
            "Light" => "Light",
            "Dark" => "Dark",
            _ => "System"
        };

        ThemeSystemItem.IsChecked = _theme == "System";
        ThemeLightItem.IsChecked = _theme == "Light";
        ThemeDarkItem.IsChecked = _theme == "Dark";

        var elementTheme = _theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = elementTheme;
        }

        ApplyCaptionButtonColors(elementTheme);
        _viewerHost.SetTheme(_theme);
        if (persist)
        {
            AppSettingsService.Update(s => s.Theme = _theme);
        }
    }

    private void ApplyCaptionButtonColors(ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = AppWindow.TitleBar;
        var effective = theme switch
        {
            ElementTheme.Light => ElementTheme.Light,
            ElementTheme.Dark => ElementTheme.Dark,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark
                : ElementTheme.Light
        };

        titleBar.PreferredTheme = effective == ElementTheme.Dark
            ? TitleBarTheme.Dark
            : TitleBarTheme.Light;

        // With ExtendsContentIntoTitleBar, button backgrounds must be transparent.
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (effective == ElementTheme.Dark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 0xA0, 0xA0, 0xA0);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 0x45, 0x45, 0x45);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 0x50, 0x50, 0x50);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }
        else
        {
            titleBar.ButtonForegroundColor = Color.FromArgb(255, 0x1B, 0x1B, 0x1B);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 0x6B, 0x6B, 0x6B);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 0xE4, 0xE4, 0xE4);
            titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 0x1B, 0x1B, 0x1B);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 0xD8, 0xD8, 0xD8);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 0x1B, 0x1B, 0x1B);
        }
    }

    private void TocToggle_Click(object sender, RoutedEventArgs e)
    {
        _tocPreference = TocToggleMenuItem.IsChecked;
        SetTocVisible(_tocPreference);
        AppSettingsService.Update(s => s.TocVisible = _tocPreference);
    }

    private void SetTocVisible(bool visible)
    {
        TocPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TocSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TocColumn.Width = visible ? new GridLength(_tocWidth) : new GridLength(0);
        TocSplitterColumn.Width = visible ? new GridLength(4) : new GridLength(0);
        TocToggleMenuItem.IsChecked = _tocPreference;
    }

    private void StatusBarToggle_Click(object sender, RoutedEventArgs e)
    {
        SetStatusBarVisible(StatusBarToggleMenuItem.IsChecked);
        AppSettingsService.Update(s => s.StatusBarVisible = _statusBarVisible);
    }

    private void SetStatusBarVisible(bool visible)
    {
        _statusBarVisible = visible;
        StatusBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        StatusBarToggleMenuItem.IsChecked = visible;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _commands.ZoomIn();

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _commands.ZoomOut();

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => _commands.ZoomReset();

    private void TocList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_suppressTocClick)
        {
            return;
        }

        if (e.ClickedItem is TocItem item && !string.IsNullOrEmpty(item.Id))
        {
            _commands.ScrollToHeading(item.Id);
        }
    }

    private void PrintMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!HasOpenDocument)
        {
            return;
        }

        try
        {
            _viewerHost.ShowPrintUi();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _ = ShowErrorAsync("Не удалось открыть диалог печати.\n\n" + ex.Message);
        }
    }

    private async void ExportPdfMenu_Click(object sender, RoutedEventArgs e) => await ExportPdfAsync();

    private async Task ExportPdfAsync()
    {
        if (!HasOpenDocument)
        {
            return;
        }

        try
        {
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("PDF", [".pdf"]);
            picker.DefaultFileExtension = ".pdf";
            picker.SuggestedFileName = string.IsNullOrWhiteSpace(_session.CurrentPath)
                ? "document"
                : Path.GetFileNameWithoutExtension(_session.CurrentPath);

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            if (!await _viewerHost.PrintToPdfAsync(file.Path))
            {
                await ShowErrorAsync("Не удалось сохранить PDF.");
                return;
            }

            FlashStatus("PDF сохранён");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Не удалось экспортировать PDF: " + ex.Message);
        }
    }

    private bool HasOpenDocument =>
        _viewerHost.HasCore && EmptyState.Visibility != Visibility.Visible;

    private async void MarkdownSyntaxHelp_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "help", "Markdown-syntax.md");
        if (!File.Exists(path))
        {
            await ShowErrorAsync("Не найден встроенный справочник:\n" + path);
            return;
        }

        await OpenPathAsync(path);
    }

    private async void AboutMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root || root.XamlRoot is null)
        {
            return;
        }

        await AboutDialog.ShowAsync(root.XamlRoot, root.RequestedTheme);
    }

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => Close();

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Открыть";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault(f => FileService.IsMarkdownPath(f.Path));
        if (file is not null)
        {
            await OpenPathAsync(file.Path);
        }
    }

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var ext in FileService.MarkdownExtensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenPathAsync(file.Path);
        }
    }

    private async Task OpenPathAsync(
        string path,
        bool isReload = false,
        bool addToRecent = true,
        string? displayTitle = null,
        string? fragment = null)
    {
        string? error = null;
        try
        {
            if (!isReload)
            {
                StatusText.Text = "Открытие…";
            }

            var result = await _session.LoadAsync(new DocumentLoadRequest
            {
                Path = path,
                IsReload = isReload,
                AddToRecent = addToRecent,
                DisplayTitle = displayTitle,
                Fragment = fragment,
                HelpRoot = GetHelpRoot()
            });

            if (result.SkippedUnchanged)
            {
                return;
            }

            _viewerHost.MapDocumentFolder(result.DocumentDirectory);
            TitleText.Text = result.Title;
            Title = result.Title + " — Просмотр Markdown";
            SetDocumentUiVisible(true);
            RefreshDocumentStatus();

            if (!result.IsReload)
            {
                FlashStatus($"Открыто за {result.ElapsedMilliseconds} мс");
                if (result.AddToRecent)
                {
                    RecentFilesService.Add(result.Path);
                    RebuildRecentMenu();
                }
            }

            if (!_viewerHost.HasCore)
            {
                _viewerHost.ShowContent(
                    result.HtmlFragment,
                    result.HasMermaid,
                    fragment: result.Fragment);
                await PrewarmViewerAsync();
                return;
            }

            _viewerHost.ShowContent(
                result.HtmlFragment,
                result.HasMermaid,
                restoreScroll: result.IsReload,
                fragment: result.Fragment);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer open
        }
        catch (DocumentLoadException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = "Не удалось открыть файл: " + ex.Message;
        }

        if (error is not null)
        {
            await ShowErrorAsync(error);
        }
    }

    private void SetDocumentUiVisible(bool visible)
    {
        if (visible)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            // Never touch WebView2.Opacity — it breaks the HWND surface (blank viewer, TOC ok).
            Viewer.Visibility = Visibility.Visible;
            SetTocVisible(_tocPreference);
        }
        else
        {
            Viewer.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyState.Opacity = 1;
            TocPanel.Visibility = Visibility.Collapsed;
            TocSplitter.Visibility = Visibility.Collapsed;
            TocColumn.Width = new GridLength(0);
            TocSplitterColumn.Width = new GridLength(0);
            TocToggleMenuItem.IsChecked = _tocPreference;
            _viewerHost.ClearCachedContent();
        }

        TocToggleMenuItem.IsEnabled = visible;
        FindMenuItem.IsEnabled = visible;
        ReloadMenuItem.IsEnabled = visible;
        PrintMenuItem.IsEnabled = visible;
        ExportPdfMenuItem.IsEnabled = visible;
        CopyTextMenuItem.IsEnabled = visible;
        CopyHtmlMenuItem.IsEnabled = visible;
    }

    private void FlashStatus(string message)
    {
        if (!_statusBarVisible)
        {
            return;
        }

        StatusText.Text = message;
        _statusFlashTimer.Stop();
        _statusFlashTimer.Start();
    }

    private void RefreshDocumentStatus()
    {
        if (!_session.IsOpen || string.IsNullOrWhiteSpace(_session.CurrentPath))
        {
            StatusText.Text = "Готово";
            return;
        }

        var sizeKb = Math.Max(1, _session.FileSize / 1024.0);
        StatusText.Text =
            $"{Path.GetFileName(_session.CurrentPath)} · {sizeKb:0.#} КБ · {_session.OpenedAt.ToLocalTime():HH:mm:ss}";
    }

    private async void UnregisterAssociations_Click(object sender, RoutedEventArgs e)
    {
        FileAssociationService.Unregister();
        FlashStatus("Ассоциации «Открыть с помощью» удалены");
        await Task.CompletedTask;
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Просмотр Markdown",
            Content = message,
            CloseButtonText = "ОК",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
