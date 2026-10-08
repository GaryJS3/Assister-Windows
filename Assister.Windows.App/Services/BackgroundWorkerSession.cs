namespace Assister.Windows.App.Services;

// Serializes microphone worker replacement, including asynchronous native cleanup.
internal sealed class BackgroundWorkerSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _lifetime;
    private Task _worker = Task.CompletedTask;
    private bool _closed;

    public async Task ReplaceAsync(Func<CancellationToken, Task>? start)
    {
        await _gate.WaitAsync();
        try
        {
            CancellationTokenSource? previous;
            lock (_sync)
            {
                previous = _lifetime;
                _lifetime = null;
            }
            try
            {
                previous?.Cancel();
                await _worker;
            }
            catch (OperationCanceledException) when (previous?.IsCancellationRequested == true) { }
            finally
            {
                previous?.Dispose();
                _worker = Task.CompletedTask;
            }

            lock (_sync)
            {
                if (_closed || start is null) return;
                var lifetime = new CancellationTokenSource();
                _lifetime = lifetime;
                try { _worker = start(lifetime.Token); }
                catch
                {
                    _lifetime = null;
                    lifetime.Dispose();
                    throw;
                }
            }
        }
        finally { _gate.Release(); }
    }

    public Task StopAsync() => ReplaceAsync(null);

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            _lifetime?.Cancel();
        }
    }
}
