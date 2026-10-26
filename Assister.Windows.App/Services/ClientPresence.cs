using System.Net.Http;

namespace Assister.Windows.App.Services;

// One lease per connection. Stop renewal before withdrawing so a late renewal cannot revive it.
internal sealed class ClientPresence(AssisterClient client, Action<bool> reportStatus,
    TimeSpan? renewalInterval = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task _renewal = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await client.RegisterAsync(cancellationToken);
        _renewal = RenewAsync();
    }

    private async Task RenewAsync()
    {
        var token = _lifetime.Token;
        try
        {
            while (true)
            {
                await Task.Delay(renewalInterval ?? TimeSpan.FromSeconds(30), token);
                try
                {
                    await client.RegisterAsync(token);
                    reportStatus(true);
                }
                catch (HttpRequestException) { reportStatus(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { reportStatus(false); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _renewal;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await client.WithdrawPresenceAsync(timeout.Token); }
        catch (HttpRequestException) { } // Offline clients expire on the server after 90 seconds.
        catch (OperationCanceledException) { }
        finally { _lifetime.Dispose(); }
    }
}
