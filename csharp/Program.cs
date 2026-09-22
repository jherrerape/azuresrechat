using Azure.Identity;
using Microsoft.Extensions.Configuration;
using SreAgentChat;

const string Cyan = "\u001b[96m";
const string Green = "\u001b[92m";
const string Red = "\u001b[91m";
const string Reset = "\u001b[0m";

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SREAGENT_")
    .AddCommandLine(args)
    .Build();

var options = configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    options.Validate();
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"{Red}Error de configuración: {ex.Message}{Reset}");
    return 1;
}

var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = false
});

using var client = new SreAgentClient(credential);

try
{
    Console.WriteLine("Resolviendo endpoint del agente...");
    Console.WriteLine($"Endpoint: {await client.ResolveEndpointAsync(options, cts.Token)}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{Red}Error: {ex.Message}{Reset}");
    return 1;
}

string? threadId = null;
var seen = new HashSet<string>(StringComparer.Ordinal);
var printed = 0;

Console.WriteLine("Comandos: /nuevo  /hilos  /abrir <id>  /aprobaciones  /aprobar <id>  /rechazar <id>  /salir");
Console.WriteLine(new string('-', 70));

while (!cts.IsCancellationRequested)
{
    Console.Write($"\n{Cyan}Tú> {Reset}");
    var input = Console.ReadLine()?.Trim();

    if (input is null) break;
    if (input.Length == 0) continue;

    if (input.StartsWith('/'))
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1].Trim() : null;

        if (command is "/salir" or "/exit") break;

        try
        {
            switch (command)
            {
                case "/nuevo":
                    threadId = null;
                    seen.Clear();
                    printed = 0;
                    Console.WriteLine("Nueva conversación: se creará al enviar el próximo mensaje.");
                    break;

                case "/hilos":
                    foreach (var thread in await client.ListThreadsAsync(cts.Token))
                        Console.WriteLine($"  [{thread.Id}] {thread.Title}");
                    break;

                case "/abrir":
                    if (argument is null)
                    {
                        Console.WriteLine("Indica el id del thread.");
                        break;
                    }
                    threadId = argument;
                    seen.Clear();
                    printed = 0;
                    foreach (var message in await client.GetMessagesAsync(threadId, cts.Token))
                    {
                        var preview = message.Content.Length > 120 ? message.Content[..120] : message.Content;
                        Console.WriteLine($"  {message.Role}: {preview}");
                    }
                    break;

                case "/aprobaciones":
                    if (threadId is null)
                    {
                        Console.WriteLine("Aún no hay conversación activa.");
                        break;
                    }
                    var approvals = await client.GetApprovalsAsync(threadId, cts.Token);
                    if (approvals.Count == 0) Console.WriteLine("No hay aprobaciones pendientes.");
                    foreach (var approval in approvals)
                        Console.WriteLine($"  [{approval.Id}] {approval.Summary}");
                    break;

                case "/aprobar":
                case "/rechazar":
                    if (threadId is null || argument is null)
                    {
                        Console.WriteLine("Necesitas una conversación activa y el id de la aprobación.");
                        break;
                    }
                    await client.DecideApprovalAsync(threadId, argument, command == "/aprobar", cts.Token);
                    Console.WriteLine("Decisión enviada.");
                    break;

                default:
                    Console.WriteLine($"Comando desconocido: {command}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{Red}Error: {ex.Message}{Reset}");
        }

        continue;
    }

    try
    {
        if (threadId is null)
        {
            threadId = await client.CreateThreadAsync(input, cts.Token);
            Console.WriteLine($"Thread: {threadId}");
        }
        else
        {
            await client.SendMessageAsync(threadId, input, cts.Token);
        }

        await StreamResponseAsync(threadId, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n(interrumpido)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\n{Red}Error: {ex.Message}{Reset}");
    }
}

return 0;

async Task StreamResponseAsync(string thread, CancellationToken ct)
{
    var deadline = DateTimeOffset.UtcNow.AddSeconds(options.ResponseTimeoutSeconds);
    var delay = TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds));
    var gotReply = false;

    Console.Write("Agente está pensando");

    while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
    {
        await Task.Delay(delay, ct);

        IReadOnlyList<ChatMessage> messages;
        try
        {
            messages = await client.GetMessagesAsync(thread, ct);
        }
        catch (HttpRequestException)
        {
            Console.Write(".");
            continue;
        }

        var newMessages = messages.Skip(printed).ToList();
        printed = messages.Count;
        var turnDone = false;

        foreach (var message in newMessages)
        {
            var key = message.Id ?? $"{message.Role}:{message.Content.GetHashCode()}";
            if (!seen.Add(key)) continue;
            if (message.Role.Contains("user", StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine();
            Console.WriteLine($"\n{Green}Agente> {message.Content}{Reset}");
            gotReply = true;
            turnDone = message.IsComplete;
        }

        if (turnDone) return;
        if (!gotReply) Console.Write(".");
    }

    if (!gotReply)
    {
        Console.WriteLine();
        Console.WriteLine("(sin respuesta dentro del tiempo de espera)");
    }
}
