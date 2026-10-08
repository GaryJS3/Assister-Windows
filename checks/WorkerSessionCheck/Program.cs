using Assister.Windows.App.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
var session = new BackgroundWorkerSession();
CancellationToken oldToken = default;
await session.ReplaceAsync(token =>
{
    oldToken = token;
    return Task.Delay(Timeout.Infinite, token);
});
await session.StopAsync();
// The old bug left a disposed source installed while playback prevented a restart.
await session.ReplaceAsync(_ => Task.CompletedTask);
await session.StopAsync();
await session.StopAsync();
Require(oldToken.IsCancellationRequested, "Retired callbacks must remain safe and cancelled.");

var stopping = Signal();
var cleanup = Signal();
var workers = 0;
var maxWorkers = 0;
async Task WorkerBody(CancellationToken token, bool slowCleanup)
{
    var active = Interlocked.Increment(ref workers);
    maxWorkers = Math.Max(maxWorkers, active);
    try { await Task.Delay(Timeout.Infinite, token); }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
        if (slowCleanup) { stopping.TrySetResult(); await cleanup.Task; }
    }
    finally { Interlocked.Decrement(ref workers); }
}
await session.ReplaceAsync(token => WorkerBody(token, slowCleanup: true));
var replace = session.ReplaceAsync(token => WorkerBody(token, slowCleanup: false));
await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
var concurrentStop = session.StopAsync();
Require(!replace.IsCompleted && !concurrentStop.IsCompleted && workers == 1,
    "Replacement and Stop must await microphone cleanup.");
cleanup.SetResult();
await Task.WhenAll(replace, concurrentStop).WaitAsync(TimeSpan.FromSeconds(5));
Require(maxWorkers == 1 && workers == 0, "Microphone workers overlapped or leaked.");

var replacements = Enumerable.Range(0, 40).Select(_ =>
    session.ReplaceAsync(token => WorkerBody(token, slowCleanup: false))).ToArray();
await Task.WhenAll(replacements).WaitAsync(TimeSpan.FromSeconds(5));
await session.StopAsync();
Require(maxWorkers == 1 && workers == 0, "Repeated restarts overlapped or leaked.");
await session.ReplaceAsync(token => WorkerBody(token, slowCleanup: false));
session.Close();
session.Close();
await session.StopAsync();
var restarted = false;
await session.ReplaceAsync(_ => { restarted = true; return Task.CompletedTask; });
Require(!restarted && workers == 0, "Shutdown allowed a late worker restart.");

var failedSession = new BackgroundWorkerSession();
try { await failedSession.ReplaceAsync(_ => throw new InvalidOperationException("Fixture startup failed.")); }
catch (InvalidOperationException) { }
await failedSession.StopAsync();
await failedSession.ReplaceAsync(_ => Task.CompletedTask);
await failedSession.StopAsync();
Console.WriteLine("PASS: paused/repeated stop, safe retired callbacks, cleanup serialization, 40 restarts without overlap, shutdown/late restart, startup failure recovery.");
