using System.IO.Pipes;

namespace MDViewer.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\MDViewer.SingleInstance";
    private const string PipeName = "MDViewer.OpenPipe";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;
    private bool _ownsMutex;

    public event Action<string?>? ActivationRequested;

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
    }

    public bool IsPrimary => _ownsMutex;

    public void StartServer()
    {
        if (!_ownsMutex)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public static bool TryNotifyRunningInstance(string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(path ?? string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server);
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                ActivationRequested?.Invoke(string.IsNullOrWhiteSpace(line) ? null : line);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
        }

        _mutex.Dispose();
    }
}
