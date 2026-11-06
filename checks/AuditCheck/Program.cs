using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Assister.Windows.App;
using Assister.Windows.App.Services;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Token = new('x', 32);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    [STAThread]
    private static void Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var task = RunAsync();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (task.IsCompleted) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        task.GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        Check(AssisterClient.IsPrivateDevelopmentHost("127.0.0.1") && AssisterClient.IsPrivateDevelopmentHost("::1") &&
            !AssisterClient.IsPrivateDevelopmentHost("8.8.8.8"), "loopback development hosts accepted; public HTTP rejected");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var listener = new HttpListener();
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var origin = $"http://localhost:{((IPEndPoint)probe.LocalEndpoint).Port}/";
        probe.Stop();
        listener.Prefixes.Add(origin);
        listener.Start();
        using var client = new AssisterClient(origin, Token);
        async Task<HttpListenerContext> Request()
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Check(request.Request.Headers["Authorization"] == "Bearer " + Token, "authenticated fixture request");
            return request;
        }
        static async Task Reply(HttpListenerContext context, object body, int status = 200)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        var id = Guid.NewGuid();
        var observed = Task.Run(async () =>
        {
            var sequences = new List<long>();
            await foreach (var item in client.ObserveAsync(id, 0, deadline.Token))
                using (item) sequences.Add(item.RootElement.GetProperty("sequence").GetInt64());
            return sequences;
        });
        await Reply(await Request(), new { }, 503);
        var first = await Request();
        Check(first.Request.QueryString["afterSequence"] == "0", "503 retry preserves replay cursor");
        await Reply(first, Enumerable.Range(1, 256).Select(sequence => new { sequence, type = "response.delta" }));
        var second = await Request();
        Check(second.Request.QueryString["afterSequence"] == "256", "full replay page advances cursor");
        await Reply(second, new[] { new { sequence = 256, type = "response.delta" }, new { sequence = 257, type = "tts.audio" }, new { sequence = 258, type = "interaction.completed" } });
        Check((await observed).SequenceEqual(Enumerable.Range(1, 258).Select(n => (long)n)), "transient recovery, paginated terminal replay and duplicate suppression");

        var streamed = Task.Run(async () =>
        {
            var sequences = new List<long>();
            await foreach (var item in client.ObserveAsync(id, 0, deadline.Token))
                using (item) sequences.Add(item.RootElement.GetProperty("sequence").GetInt64());
            return sequences;
        });
        await Reply(await Request(), Array.Empty<object>());
        var upgrade = await Request();
        Check(upgrade.Request.IsWebSocketRequest, "empty HTTP replay transitions to live WebSocket");
        using (var socket = (await upgrade.AcceptWebSocketAsync(null)).WebSocket)
        {
            var payload = Encoding.UTF8.GetBytes("{\"sequence\":1,\"type\":\"response.delta\",\"data\":{\"text\":\"Hello\"}}");
            await socket.SendAsync(payload, System.Net.WebSockets.WebSocketMessageType.Text, true, deadline.Token);
            await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "fixture reconnect", deadline.Token);
            var recovery = await Request();
            Check(recovery.Request.QueryString["afterSequence"] == "1", "socket reconnect resumes after last delivered event");
            await Reply(recovery, new[] { new { sequence = 1, type = "response.delta" }, new { sequence = 2, type = "interaction.completed" } });
        }
        Check((await streamed).SequenceEqual(new long[] { 1, 2 }), "WebSocket interruption recovers without duplicate output");

        var audio = client.HasAudioAsync(id, deadline.Token);
        await Reply(await Request(), Enumerable.Range(1, 256).Select(sequence => new { sequence, type = "step.started" }));
        var next = await Request();
        Check(next.Request.QueryString["afterSequence"] == "256", "audio lookup reads subsequent page");
        await Reply(next, new[] { new { sequence = 257, type = "tts.audio" } });
        Check(await audio, "audio beyond first 256 events is discoverable");

        var rejected = Task.Run(async () =>
        {
            try { await foreach (var item in client.ObserveAsync(id, 0, deadline.Token)) item.Dispose(); }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized) { return true; }
            return false;
        });
        await Reply(await Request(), new { }, 401);
        Check(await rejected, "authentication failure surfaces without an infinite retry");

        using (var cancelled = new CancellationTokenSource())
        {
            var pending = Task.Run(async () =>
            {
                try { await foreach (var item in client.ObserveAsync(id, 0, cancelled.Token)) item.Dispose(); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { return true; }
                return false;
            });
            await Reply(await Request(), new { }, 503);
            cancelled.Cancel();
            Check(await pending, "retry delay cancels promptly");
        }

        var temporary = Path.Combine(Path.GetTempPath(), "Assister-Audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var main = new MainWindow(); // Never shown: no startup, updater, microphone or real server access.
        void Field(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(main, value);
        Task Send() => (Task)typeof(MainWindow).GetMethod("SendMessageAsync", Private)!.Invoke(main, null)!;
        using var connection = new CancellationTokenSource();
        Field("_settings", AppSettings.Load(Path.Combine(temporary, "settings.json")));
        Field("_client", client);
        Field("_connectionLifetime", connection);
        var conversation = Guid.NewGuid();
        Field("_conversationId", conversation);
        var input = (TextBox)main.FindName("MessageInput");
        try
        {
            input.Text = "First request";
            var sending = Send();
            var submission = await Request();
            using var body = await JsonDocument.ParseAsync(submission.Request.InputStream);
            var key = body.RootElement.GetProperty("idempotencyKey").GetString();
            await Send(); // Simulated repeated Enter while response is delayed.
            Check(!sending.IsCompleted, "duplicate Enter returns while original request is pending");
            input.Text = "Next draft";
            await Reply(submission, new { }, 503);
            await sending;
            Check(input.Text == "Next draft", "failed submission preserves edited draft");
            input.Text = "First request";
            sending = Send();
            submission = await Request();
            using var retry = await JsonDocument.ParseAsync(submission.Request.InputStream);
            Check(retry.RootElement.GetProperty("idempotencyKey").GetString() == key, "uncertain submission reuses idempotency key");
            input.Text = "Next draft";
            await Reply(submission, new { id, conversationId = conversation, status = "queued", input = "First request", response = "", createdAt = DateTimeOffset.UtcNow, lastSequence = 0 });
            await sending;
            Check(input.Text == "Next draft", "successful submission preserves text entered during request");
            await Reply(await Request(), new[] { new { sequence = 1, type = "interaction.completed", data = new { } } });
            for (var attempt = 0; attempt < 100 && typeof(MainWindow).GetField("_activeInteraction", Private)!.GetValue(main) is not null; attempt++)
                await Task.Delay(10);
            Check(typeof(MainWindow).GetField("_activeInteraction", Private)!.GetValue(main) is null, "terminal replay releases active request");
            Check(File.Exists(Path.Combine(temporary, "settings.json")), "activity persists to isolated settings file");
            input.Text = "First request";
            sending = Send();
            submission = await Request();
            await Reply(submission, new { id, conversationId = conversation, status = "completed", input = "First request", response = "Done", createdAt = DateTimeOffset.UtcNow, lastSequence = 1 });
            await sending;
            Check(typeof(MainWindow).GetField("_activeInteraction", Private)!.GetValue(main) is null,
                "idempotent response already restored in history does not create an orphan active request");
        }
        finally
        {
            connection.Cancel();
            main.Close();
            Directory.Delete(temporary, true);
        }
    }
}
