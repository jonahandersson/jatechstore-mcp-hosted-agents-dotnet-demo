// Jonah Andersson Tech store assistant: a Foundry hosted agent (Microsoft Agent Framework, .NET 10)
// that uses the tools from our MCP server on Azure Functions.
//
//   Foundry hosted agent  ──(Streamable HTTP + Entra token)──►  Azure Functions MCP server
//        │                                                       search_catalog
//        └─ model in the Foundry project                          get_product
//                                                                 request_booking
//
// No keys anywhere: the agent gets a token for its own Entra agent identity, and the
// function app's built-in auth only accepts callers on its allowed-applications list.

using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using DotNetEnv;
using JonahAnderssonTech.StoreAgent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

// Local runs read .env. In Foundry, the platform injects the environment variables.
Env.TraversePath().Load();

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is not set.");

var projectEndpoint = new Uri(Required("FOUNDRY_PROJECT_ENDPOINT"));
var deployment = Environment.GetEnvironmentVariable("FOUNDRY_MODEL")
    ?? Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("Set AZURE_AI_MODEL_DEPLOYMENT_NAME (or FOUNDRY_MODEL).");

// e.g. https://func-xxxx.azurewebsites.net/runtime/webhooks/mcp  or  http://localhost:7071/runtime/webhooks/mcp
var mcpServerUrl = new Uri(Required("MCP_SERVER_URL"));

// The function app's Application ID URI (azd output ENTRA_IDENTIFIER_URI), e.g. api://jatech-store-xyz.
// Leave empty for a local, unauthenticated Functions host.
var mcpAudience = Environment.GetEnvironmentVariable("MCP_SERVER_AUDIENCE");

// In Foundry, DefaultAzureCredential resolves to the hosted agent's own Entra agent identity.
// Locally, set AZURE_TOKEN_CREDENTIALS=dev so it uses your Azure CLI / azd sign-in.
TokenCredential credential = new DefaultAzureCredential();

// ── Connect to the MCP server ────────────────────────────────────────────────
HttpMessageHandler transportHandler = new HttpClientHandler
{
    UseCookies = false,
    AllowAutoRedirect = false, // never forward a bearer token to another host
};

if (!string.IsNullOrWhiteSpace(mcpAudience))
{
    transportHandler = new EntraTokenHandler(credential, $"{mcpAudience.TrimEnd('/')}/.default")
    {
        InnerHandler = transportHandler,
    };
}

var mcpHttpClient = new HttpClient(transportHandler);

Console.WriteLine($"Connecting to MCP server at {mcpServerUrl} ...");

await using var mcpClient = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = mcpServerUrl,
    Name = "Jonah Andersson Tech store (Azure Functions MCP)",
    TransportMode = HttpTransportMode.StreamableHttp,
}, mcpHttpClient));

IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();
Console.WriteLine($"MCP tools discovered: {string.Join(", ", mcpTools.Select(t => t.Name))}");

// Least privilege: optionally allow only some tools, e.g. MCP_ALLOWED_TOOLS=search_catalog,get_product
var allowList = Environment.GetEnvironmentVariable("MCP_ALLOWED_TOOLS")?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

List<AITool> tools = [.. mcpTools
    .Where(t => allowList is null || allowList.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
    .Cast<AITool>()];

Console.WriteLine($"MCP tools enabled for the agent: {string.Join(", ", tools.Select(t => t.Name))}");

// ── Create the agent ─────────────────────────────────────────────────────────
var agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "jatech-store-agent";

AIAgent agent = new AIProjectClient(projectEndpoint, credential)
    .AsAIAgent(
        model: deployment,
        name: agentName,
        description: "Store assistant for Jonah Andersson Tech, using tools from an MCP server on Azure Functions.",
        instructions: """
            You are the store assistant for Jonah Andersson Tech, the company of Jonah Andersson:
            Microsoft MVP, Microsoft Certified Trainer and author of Learning Microsoft Azure (O'Reilly).
            - Use the tools to find books, courses, training and services. Never invent products, prices,
              dates, discounts or availability. If the price says "On request", say exactly that.
            - For books and on-demand courses, share the link from get_product.
            - For training, workshops and services, offer to send a booking request. Collect the person's name,
              email and what they need (team size, dates, location, goals). Use what they already told you:
              once you have a name, an email and any of those details, don't ask for more.
            - Before calling request_booking, repeat the product, name, email and message back once and wait for a
              clear "yes". As soon as they say yes, call request_booking. Don't ask them to confirm again.
            - If someone asks to book a book or on-demand course, call request_booking anyway, without asking them
              to confirm, and report what it returns. The server decides what can be booked, not you.
            - Explain that Jonah reviews every request personally and that nothing is confirmed automatically.
            - If a tool returns an error message, explain it in plain words and ask for what is missing.
            - Refer to Jonah by name and don't use pronouns for Jonah.
            - Keep answers short and friendly.
            """,
        tools: tools);

// ── Host it as a Foundry hosted agent (Responses protocol) ──────────────────
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFoundryResponses(agent);

var app = builder.Build();
app.MapFoundryResponses();
app.Run();
