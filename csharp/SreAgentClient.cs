using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace SreAgentChat;

public sealed record ChatMessage(string Role, string Content, string? Id, bool IsComplete);

public sealed record ThreadSummary(string Id, string Title);

public sealed record PendingApproval(string Id, string Summary);

public sealed class SreAgentClient : IDisposable
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string DataPlaneScope = "https://azuresre.dev/.default";
    private const string ApiVersion = "2025-05-01-preview";

    private readonly TokenCredential _credential;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private AccessToken _dataToken;

    public SreAgentClient(TokenCredential credential) => _credential = credential;

    public string Endpoint { get; private set; } = string.Empty;

    public async Task<string> ResolveEndpointAsync(AgentOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.AgentEndpoint))
            return Endpoint = Normalize(options.AgentEndpoint);

        var url = $"https://management.azure.com/subscriptions/{options.SubscriptionId}" +
                  $"/resourceGroups/{options.ResourceGroup}" +
                  $"/providers/Microsoft.App/agents/{options.AgentName}?api-version={ApiVersion}";

        var armToken = await _credential.GetTokenAsync(new TokenRequestContext([ArmScope]), ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken.Token);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ARM GET falló ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var endpoint = doc.RootElement.GetProperty("properties").GetProperty("agentEndpoint").GetString();

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("El agente aún no expone 'agentEndpoint'. ¿Está aprovisionado e iniciado?");

        return Endpoint = Normalize(endpoint);
    }

    public async Task<IReadOnlyList<ThreadSummary>> ListThreadsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "/api/v1/threads", null, ct);

        var threads = new List<ThreadSummary>();
        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            var id = ReadString(item, "id");
            if (id is not null)
                threads.Add(new ThreadSummary(id, ReadString(item, "title") ?? "(sin título)"));
        }

        return threads;
    }

    public async Task<string> CreateThreadAsync(string text, CancellationToken ct)
    {
        var payload = new { StartMessage = new { Text = text } };
        using var doc = await SendAsync(HttpMethod.Post, "/api/v1/threads", payload, ct);

        return ReadString(doc.RootElement, "id", "threadId")
               ?? throw new InvalidOperationException($"No se pudo crear el thread: {doc.RootElement}");
    }

    public async Task SendMessageAsync(string threadId, string text, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"/api/v1/threads/{threadId}/messages", new { Text = text }, ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string threadId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"/api/v1/threads/{threadId}/messages", null, ct);

        var messages = new List<ChatMessage>();
        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            if (ParseMessage(item) is { } message)
                messages.Add(message);
        }

        return messages;
    }

    public async Task<IReadOnlyList<PendingApproval>> GetApprovalsAsync(string threadId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"/api/v1/approvals/{threadId}", null, ct);

        var approvals = new List<PendingApproval>();
        foreach (var item in EnumerateCollection(doc.RootElement))
        {
            var id = ReadString(item, "id", "approvalId", "requestId");
            if (id is not null)
            {
                var summary = ReadString(item, "summary", "description", "title", "action") ?? "(sin descripción)";
                approvals.Add(new PendingApproval(id, summary));
            }
        }

        return approvals;
    }

    public async Task DecideApprovalAsync(string threadId, string approvalId, bool approve, CancellationToken ct)
    {
        var payload = new { Decision = approve ? "Approved" : "Rejected" };
        using var _ = await SendAsync(HttpMethod.Post, $"/api/v1/approvals/{threadId}/{approvalId}/decision", payload, ct);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Endpoint + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetDataPlaneTokenAsync(ct));

        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {path} devolvió {(int)response.StatusCode}: {body}");

        if (string.IsNullOrWhiteSpace(body))
            return JsonDocument.Parse("{}");

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private async Task<string> GetDataPlaneTokenAsync(CancellationToken ct)
    {
        if (_dataToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return _dataToken.Token;

        _dataToken = await _credential.GetTokenAsync(new TokenRequestContext([DataPlaneScope]), ct);
        return _dataToken.Token;
    }

    private static ChatMessage? ParseMessage(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var content = ReadContent(item);
        if (string.IsNullOrWhiteSpace(content)) return null;

        // El rol viene anidado en author.role: "User" o "SREAgent".
        var role = (item.TryGetProperty("author", out var author) ? ReadString(author, "role") : null)
                   ?? ReadString(item, "role", "sender")
                   ?? "assistant";

        var isComplete = !item.TryGetProperty("isComplete", out var complete)
                         || complete.ValueKind != JsonValueKind.False;

        return new ChatMessage(role, content.Trim(), ReadString(item, "id", "messageId"), isComplete);
    }

    private static string? ReadContent(JsonElement item)
    {
        foreach (var name in new[] { "text", "content", "message", "body" })
        {
            if (!item.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Array)
            {
                var parts = value.EnumerateArray()
                    .Select(part => part.ValueKind == JsonValueKind.String
                        ? part.GetString()
                        : ReadString(part, "text", "content"))
                    .Where(part => !string.IsNullOrWhiteSpace(part));

                var joined = string.Join(Environment.NewLine, parts);
                if (joined.Length > 0) return joined;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadString(value, "text", "content", "value");
                if (nested is not null) return nested;
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateCollection(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray();

        foreach (var name in new[] { "value", "messages", "items", "data", "approvals", "threads" })
        {
            if (root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
                return array.EnumerateArray();
        }

        return [];
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string Normalize(string endpoint)
    {
        var value = endpoint.Trim().TrimEnd('/');
        return value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
    }

    public void Dispose() => _http.Dispose();
}
