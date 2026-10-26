using System.Net;
using System.Text.Json;
using Assister.Windows.App.Services;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static string FixtureOrigin()
{
    var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    socket.Start();
    var port = ((IPEndPoint)socket.LocalEndpoint).Port;
    socket.Stop();
    return $"http://localhost:{port}/";
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var listener = new HttpListener();
var origin = FixtureOrigin();
listener.Prefixes.Add(origin);
listener.Start();
var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var failures = 0;
var withdrawals = 0;
var fixture = Task.Run(async () =>
{
    var registrations = 0;
    while (true)
    {
        var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
        Require(context.Request.Headers["Authorization"] == "Bearer " + new string('x', 32), "Bearer missing.");
        if (context.Request.HttpMethod == "DELETE")
        {
            Require(context.Request.Url!.AbsolutePath == "/api/client/clients/current", "Wrong withdrawal route.");
            withdrawals++;
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }
        Require(context.Request.HttpMethod == "POST" && context.Request.Url!.AbsolutePath == "/api/client/clients/register", "Wrong registration route.");
        using var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: deadline.Token);
        var root = body.RootElement;
        Require(root.GetProperty("clientType").GetString() == "windows", "Wrong client type.");
        Require(root.GetProperty("deviceName").GetString() == Environment.MachineName, "Computer name missing.");
        Require(root.GetProperty("capabilities").GetArrayLength() == 0, "Unimplemented capability advertised.");
        Require(!root.TryGetProperty("owner", out _) && !root.TryGetProperty("clientId", out _), "Identity must come from token.");
        registrations++;
        context.Response.StatusCode = registrations == 2 ? 503 : 200;
        context.Response.Close();
    }
});
using var client = new AssisterClient(origin, new string('x', 32));
var presence = new ClientPresence(client, online =>
{
    if (!online) Interlocked.Increment(ref failures);
    else if (Volatile.Read(ref failures) > 0) recovered.TrySetResult();
}, TimeSpan.FromMilliseconds(25));
await presence.StartAsync(deadline.Token);
await recovered.Task.WaitAsync(deadline.Token);
await presence.DisposeAsync();
await fixture;
Require(failures == 1 && withdrawals == 1, "Recovery/withdrawal failed.");
Require(!listener.GetContextAsync().Wait(100), "Renewal continued after withdrawal.");
listener.Stop();

// An initial registration failure must reach the connection UI, rather than appear connected.
using var rejectedListener = new HttpListener();
var rejectedOrigin = FixtureOrigin();
rejectedListener.Prefixes.Add(rejectedOrigin);
rejectedListener.Start();
var rejection = Task.Run(async () =>
{
    var context = await rejectedListener.GetContextAsync().WaitAsync(deadline.Token);
    context.Response.StatusCode = 401;
    context.Response.Close();
});
using var rejectedClient = new AssisterClient(rejectedOrigin, new string('x', 32));
try
{
    await rejectedClient.RegisterAsync(deadline.Token);
    throw new Exception("Unauthorized registration accepted.");
}
catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized) { }
await rejection;
Console.WriteLine("PASS: authenticated registration payload, renewal, transient failure/recovery, withdrawal ordering, stopped renewal, and initial 401 rejection.");
