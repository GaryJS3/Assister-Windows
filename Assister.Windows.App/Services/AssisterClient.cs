using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Assister.Windows.App.Services;

internal sealed class AssisterClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public AssisterClient(string endpoint, string token)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a valid HTTP or HTTPS server origin.");
        if (uri.Scheme != Uri.UriSchemeHttps && !IsPrivateDevelopmentHost(uri.Host))
            throw new ArgumentException("Use HTTPS, or HTTP with a private development-LAN address (for example, http://10.0.0.197:5080).");
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new ArgumentException("Enter a server origin without a path, credentials, or query string.");

        token = NormalizeToken(token);
        if (token.Length is < 24 or > 512)
            throw new ArgumentException("Enter the raw rich-client token (24–512 characters), without the 'Bearer ' prefix.");

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public static string NormalizeToken(string token)
    {
        token = token.Trim();
        return token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token[7..].Trim() : token;
    }

    internal static bool IsPrivateDevelopmentHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(host, out var address)) return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168);
    }

    public async Task<JsonDocument> ConnectAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/client/protocol", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("The server rejected this client token (HTTP 401). Check that it is the raw rich-client token configured on this server.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) for /api/client/protocol. Check the server URL and rich-client API availability.", null, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonDocument>(Json, cancellationToken)
            ?? throw new InvalidDataException("The server returned an empty protocol response.");
        if (document.RootElement.GetProperty("protocolVersion").GetInt32() != 1)
        {
            document.Dispose();
            throw new NotSupportedException("This server uses an unsupported rich-client protocol version.");
        }
        return document;
    }

    public async Task<Guid> CreateConversationAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("api/client/conversations", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return result.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<IReadOnlyList<InteractionSnapshot>> GetHistoryAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        return await _http.GetFromJsonAsync<InteractionSnapshot[]>($"api/client/conversations/{conversationId}", Json, cancellationToken) ?? [];
    }

    public async Task<InteractionSnapshot> SubmitAsync(Guid conversationId, string message, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"api/client/conversations/{conversationId}/interactions",
            new SubmitInteraction(message, idempotencyKey, Speak: true), Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InteractionSnapshot>(Json, cancellationToken)
            ?? throw new InvalidDataException("The server returned an empty interaction.");
    }

    public async Task CancelAsync(Guid interactionId, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"api/client/interactions/{interactionId}/cancel", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> DownloadAudioAsync(Guid interactionId, CancellationToken cancellationToken)
    {
        const int limit = 8 * 1024 * 1024 + 65536;
        using var response = await _http.GetAsync($"api/client/interactions/{interactionId}/audio", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response audio exceeds the client limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + count > limit) throw new InvalidDataException("Response audio exceeds the client limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    public async Task ReportPlaybackAsync(Guid interactionId, Guid playbackId, string state, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"api/client/interactions/{interactionId}/playback", new { state, playbackId }, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> HasAudioAsync(Guid interactionId, CancellationToken cancellationToken)
    {
        using var events = await _http.GetFromJsonAsync<JsonDocument>($"api/client/interactions/{interactionId}/events?afterSequence=0", Json, cancellationToken);
        return events?.RootElement.ValueKind == JsonValueKind.Array && events.RootElement.EnumerateArray().Any(item => item.TryGetProperty("type", out var type) && type.GetString() == "tts.audio");
    }

    public async Task RestoreTimelineAsync(Guid interactionId, Action<JsonElement> applyEvent, CancellationToken cancellationToken)
    {
        long cursor = 0;
        while (true)
        {
            using var events = await _http.GetFromJsonAsync<JsonDocument>($"api/client/interactions/{interactionId}/events?afterSequence={cursor}", Json, cancellationToken);
            if (events?.RootElement.ValueKind != JsonValueKind.Array) return;
            var previous = cursor;
            foreach (var item in events.RootElement.EnumerateArray())
            {
                applyEvent(item);
                if (item.TryGetProperty("sequence", out var sequence)) cursor = Math.Max(cursor, sequence.GetInt64());
            }
            if (cursor == previous || events.RootElement.GetArrayLength() < 256) return;
        }
    }

    public async Task<Guid> UploadVoiceAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        if (pcm.Length is <= 0 or > 960000 || pcm.Length % 2 != 0) throw new ArgumentException("Recording must be 16 kHz mono PCM and no longer than 30 seconds.");
        using var content = new ByteArrayContent(pcm);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/pcm");
        using var response = await _http.PostAsync("api/client/attachments?name=voice.pcm&source=microphone", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return result.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<InteractionSnapshot> SubmitVoiceAsync(Guid conversationId, Guid attachmentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"api/client/conversations/{conversationId}/interactions",
            new { message = "Voice input", idempotencyKey, audioAttachmentId = attachmentId, speak = true }, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InteractionSnapshot>(Json, cancellationToken)
            ?? throw new InvalidDataException("The server returned an empty voice interaction.");
    }

    public async IAsyncEnumerable<JsonDocument> ObserveAsync(Guid interactionId, long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = afterSequence;
        while (!cancellationToken.IsCancellationRequested)
        {
            using (var check = await _http.GetAsync($"api/client/interactions/{interactionId}/events?afterSequence={cursor}", cancellationToken))
                check.EnsureSuccessStatusCode();

            var address = new Uri(_http.BaseAddress!, $"api/client/interactions/{interactionId}/stream?afterSequence={cursor}");
            var socketAddress = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws" }.Uri;
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", _http.DefaultRequestHeaders.Authorization!.ToString());
            try { await socket.ConnectAsync(socketAddress, cancellationToken); }
            catch (WebSocketException)
            {
                await Task.Delay(500, cancellationToken);
                continue;
            }

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                JsonDocument? document;
                try { document = await ReceiveMessageAsync(socket, cancellationToken); }
                catch (WebSocketException) { break; }
                if (document is null) break;
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "subscription.ready")
                {
                    if (root.GetProperty("protocolVersion").GetInt32() != 1) { document.Dispose(); throw new NotSupportedException("Unsupported event protocol version."); }
                    document.Dispose();
                    continue;
                }
                if (!root.TryGetProperty("sequence", out var sequence)) { document.Dispose(); continue; }
                var next = sequence.GetInt64();
                if (next <= cursor) { document.Dispose(); continue; }
                if (next != cursor + 1) { document.Dispose(); break; }
                cursor = next;
                var terminal = root.TryGetProperty("type", out type) && type.GetString() is "interaction.completed" or "interaction.failed" or "interaction.cancelled";
                yield return document;
                if (terminal) yield break;
            }
            await Task.Delay(500, cancellationToken);
        }
    }

    private static async Task<JsonDocument?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var segment = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(segment, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Expected a JSON text event.");
            buffer.Write(segment, 0, result.Count);
            if (buffer.Length > 1024 * 1024) throw new InvalidDataException("Event exceeds the 1 MiB client limit.");
            if (result.EndOfMessage) return JsonDocument.Parse(buffer.ToArray());
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record SubmitInteraction(string Message, string IdempotencyKey, bool Speak);
    public sealed record InteractionSnapshot(Guid Id, Guid ConversationId, string Status, string Input,
        string Response, DateTimeOffset CreatedAt, long LastSequence, Guid? RunId, bool CancelRequested);
}
