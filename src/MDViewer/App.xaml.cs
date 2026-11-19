using MDViewer.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MDViewer;

public partial class App : Application
{
    private MainWindow? _window;
    private SingleInstanceService? _singleInstance;

    public static string? LaunchFilePath { get; private set; }
    public static string? LaunchFragment { get; private set; }

    public App()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "--unregister-associations", StringComparison.OrdinalIgnoreCase)))
        {
            FileAssociationService.Unregister();
            Exit();
            return;
        }

        var target = FileAssociationService.FindMarkdownTarget(args);
        LaunchFilePath = target.Path;
        LaunchFragment = target.Fragment;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            var notify = LaunchFilePath;
            if (!string.IsNullOrEmpty(LaunchFragment))
            {
                notify = notify + "#" + LaunchFragment;
            }

            SingleInstanceService.TryNotifyRunningInstance(notify);
            Exit();
            return;
        }

        FileAssociationService.EnsureRegistered();

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        };
        _singleInstance.ActivationRequested += path =>
        {
            if (_window is null)
            {
                return;
            }

            _window.DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Normal,
                async () =>
                {
                    if (_window is not null)
                    {
                        await _window.OpenFromActivationAsync(path);
                    }
                });
        };
        _singleInstance.StartServer();
        _window.Activate();
    }
}
