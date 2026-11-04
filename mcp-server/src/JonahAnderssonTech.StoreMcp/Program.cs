using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using JonahAnderssonTech.StoreMcp;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Every tool call becomes a trace in Application Insights.
// Only export to Azure Monitor when a connection string exists (not when running locally).
var otel = builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    // Application Insights has local auth disabled, so the exporter signs in with the
    // function app's user-assigned identity (ClientId comes from the Bicep app setting).
    var clientId = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_AUTHENTICATION_STRING")?
        .Split(';').FirstOrDefault(p => p.StartsWith("ClientId=", StringComparison.OrdinalIgnoreCase))?["ClientId=".Length..];

    otel.UseAzureMonitorExporter(options =>
    {
        if (!string.IsNullOrEmpty(clientId))
        {
            options.Credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
        }
    });
}

// The Jonah Andersson Tech catalog, loaded from catalog.json.
builder.Services.AddSingleton<StoreCatalog>();

// Optional arguments for search_catalog are declared here rather than as [McpToolProperty] parameters.
builder
    .ConfigureMcpTool("search_catalog")
    .WithProperty("query", McpToolPropertyType.String,
        "Optional keywords, e.g. 'azure', 'mcp', 'career'. Leave empty to list everything.", required: false)
    .WithProperty("category", McpToolPropertyType.String,
        "Optional filter: Book, Course, Training or Service.", required: false);

builder.Build().Run();
