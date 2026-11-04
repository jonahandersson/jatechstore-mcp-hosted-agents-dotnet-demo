// Store front for the Jonah Andersson Tech hosted agent: a local web page and a small proxy.
//
//   Browser ──► this app (localhost only) ──(your az login token)──► Foundry hosted agent ──► MCP server
//
// The Foundry endpoint needs an Entra token, so the browser can't call it directly. This app signs in
// with your Azure CLI login and has no authentication of its own, so it must stay on localhost.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
// Your own Foundry endpoint lives here. The file is git-ignored; copy appsettings.local.example.json.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true);
builder.WebHost.UseUrls("http://localhost:5080");
builder.Services.AddHttpClient("foundry", client => client.Timeout = TimeSpan.FromSeconds(120));

var app = builder.Build();

var agentUrl = app.Configuration["Foundry:AgentResponsesUrl"]
    ?? throw new InvalidOperationException("Foundry:AgentResponsesUrl is not set. Copy appsettings.local.example.json to appsettings.local.json and add your agent URL.");

TokenCredential credential = new ChainedTokenCredential(new AzureCliCredential(), new AzureDeveloperCliCredential());
var tokenRequest = new TokenRequestContext(["https://ai.azure.com/.default"]);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/chat", async (ChatRequest chat, IHttpClientFactory httpClients, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(chat.Message) || chat.Message.Length > 2000)
        return Results.BadRequest(new { error = "Write a message of up to 2000 characters." });

    var body = new JsonObject { ["input"] = chat.Message, ["stream"] = false };
    if (!string.IsNullOrEmpty(chat.PreviousResponseId))
        body["previous_response_id"] = chat.PreviousResponseId;

    try
    {
        var token = await credential.GetTokenAsync(tokenRequest, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, agentUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await httpClients.CreateClient("foundry").SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Agent returned {Status}: {Body}", (int)response.StatusCode, json);
            return Results.Json(new { error = $"The agent returned HTTP {(int)response.StatusCode}." }, statusCode: 502);
        }

        return Results.Ok(ChatReply.Parse(json));
    }
    catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
    {
        logger.LogError(ex, "Sign-in failed");
        return Results.Json(new { error = "Sign-in failed. Run az login and try again." }, statusCode: 502);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        logger.LogError(ex, "Agent call failed");
        return Results.Json(new { error = "The agent didn't answer in time. Try again." }, statusCode: 502);
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        logger.LogError(ex, "Agent reply could not be read");
        return Results.Json(new { error = "The agent couldn't finish that reply. Try again." }, statusCode: 502);
    }
});

app.Run();

internal sealed record ChatRequest(string? Message, string? PreviousResponseId);

internal sealed record ToolCall(string Name, string Arguments, string? Output);

internal sealed record ChatReply(string ResponseId, string Text, List<ToolCall> Tools)
{
    /// <summary>Reads the Responses payload: the assistant's text, plus each tool call matched to its result.</summary>
    public static ChatReply Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
            throw new InvalidOperationException($"Response status was {status.GetString()}.");

        var calls =new List<(string CallId, string Name, string Arguments)>();
        var outputs = new Dictionary<string, string>();
        var text = new StringBuilder();

        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            switch (item.GetProperty("type").GetString())
            {
                case "function_call":
                    calls.Add((
                        item.GetProperty("call_id").GetString() ?? "",
                        item.GetProperty("name").GetString() ?? "",
                        item.GetProperty("arguments").GetString() ?? "{}"));
                    break;

                case "function_call_output":
                    outputs[item.GetProperty("call_id").GetString() ?? ""] =
                        ReadToolOutput(item.GetProperty("output").GetString() ?? "");
                    break;

                case "message":
                    foreach (var part in item.GetProperty("content").EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var value))
                            text.Append(value.GetString());
                    }
                    break;
            }
        }

        var tools = calls.Select(c => new ToolCall(c.Name, c.Arguments, outputs.GetValueOrDefault(c.CallId))).ToList();
        return new ChatReply(root.GetProperty("id").GetString() ?? "", text.ToString(), tools);
    }

    // Tool results arrive wrapped as {"$type":"text","Text":"..."}.
    private static string ReadToolOutput(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Text", out var text)
                    ? text.GetString() ?? raw
                    : raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }
}
