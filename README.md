# Jonah Andersson Tech store: an MCP server on Azure Functions, used by a Foundry hosted agent

Demo for **"Developing with AI: Building and Deploying Custom MCP Servers with Azure & .NET"**
(Experts Live Switzerland 2026, Jonah Andersson).

```
 User ──► Foundry hosted agent (.NET 10, Microsoft Agent Framework, Responses protocol)
               │  1. tools/list   2. tools/call      (MCP over Streamable HTTP)
               │  Authorization: Bearer <token for the agent's own Entra identity>
               ▼
          Azure Functions MCP server (.NET 10 isolated, Flex Consumption)
               search_catalog · get_product · request_booking
               Built-in auth (Entra): only allowed client apps get in. No keys.
```

| Folder | What it is |
|---|---|
| `mcp-server/` | The MCP server. `src/JonahAnderssonTech.StoreMcp` has three `[McpToolTrigger]` functions over the real Jonah Andersson Tech catalog in `catalog.json` (books, O'Reilly course, training, services). `infra/` is the Bicep from [Azure-Samples/remote-mcp-functions-dotnet](https://github.com/Azure-Samples/remote-mcp-functions-dotnet) (MIT) with one addition: `ALLOWED_CALLER_CLIENT_IDS`. |
| `hosted-agent/JonahAnderssonTech.StoreAgent` | The hosted agent. Connects with `McpClient`, adds an Entra token on every request (`EntraTokenHandler.cs`), and is hosted with `AddFoundryResponses`. Based on the Agent Framework sample `Hosted-McpTools`. |
| `frontend/JonahAnderssonTech.StoreWeb` | A local store chat page that calls the hosted agent and shows each MCP tool call. See section 6. |
| `.vscode/mcp.json` | Test the server from GitHub Copilot agent mode, locally and in Azure. |

> Package versions follow the official samples as of October 2026. Both projects were built, deployed and rehearsed end to end on 3 November 2026.

## Edit the catalog before the talk

`mcp-server/src/JonahAnderssonTech.StoreMcp/catalog.json` holds the store: your books, the O'Reilly course, your training and your services. Add course codes and workshop lengths when you have them. Prices are shown exactly as written, so set real prices or keep "On request". No code changes are needed: redeploy with `azd deploy`.

## Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools 4.5.0 or later, plus Azurite (Docker or the VS Code extension)
- Azure Developer CLI 1.36 or later with the Foundry extensions: `azd ext install azure.ai.agents` (or install the Foundry Dev Pack)
- Azure CLI (`az login`)
- An existing Foundry project with a chat model deployment, and **Foundry Project Manager** on that project
- Rights to create an Entra app registration (the server infra creates one)

## 1. Run the MCP server locally

```bash
# terminal 1
azurite --silent        # or "Azurite: Start" in VS Code
# terminal 2
cd mcp-server/src/JonahAnderssonTech.StoreMcp
cp local.settings.example.json local.settings.json   # first run only; git-ignored
func start
```

Endpoint: `http://localhost:7071/runtime/webhooks/mcp` (Streamable HTTP). Test it by either:

- **VS Code:** open `.vscode/mcp.json`, select **Start** on `jatech-store-local`, then ask Copilot (Agent mode) *"What books has Jonah Andersson written?"*
- **MCP Inspector:** `npx @modelcontextprotocol/inspector`, choose Streamable HTTP and paste the URL.

## 2. Deploy the MCP server to Azure

```bash
cd mcp-server
azd auth login
azd env new store-mcp-jatech
azd env set VNET_ENABLED false
azd env set ENABLE_AUTH true
azd env set PRE_AUTHORIZED_CLIENT_IDS aebc6443-996d-45c2-90f0-388ff96faa56   # Visual Studio Code
azd up

azd env get-value AZURE_FUNCTION_NAME     # -> func-xxxx
azd env get-value ENTRA_IDENTIFIER_URI    # -> api://...  (the token audience)
```

The MCP endpoint is `https://<AZURE_FUNCTION_NAME>.azurewebsites.net/runtime/webhooks/mcp`. Built-in auth returns **401** to anyone not signed in, and only client apps on the allowed list get through.

To check it from VS Code, start `jatech-store-azure` in `.vscode/mcp.json`, enter the function app name, and sign in with Entra when prompted.

## 3. Deploy the hosted agent

`azd ai agent init` scaffolds into a working folder, so run it from an empty directory **outside** this repo:

```bash
mkdir ~/jatech-agent-work && cd ~/jatech-agent-work
azd ai agent init -t <path-to-repo>/hosted-agent/JonahAnderssonTech.StoreAgent/azure.yaml -d <model-deployment> --project-id <foundry-project-resource-id>
cd jatech-store-agent

azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME <model-deployment>
azd env set MCP_SERVER_URL       https://<AZURE_FUNCTION_NAME>.azurewebsites.net/runtime/webhooks/mcp
azd env set MCP_SERVER_AUDIENCE  <ENTRA_IDENTIFIER_URI>

azd provision
azd deploy
```

**Use `gpt-6.1-terra` for the model deployment.** All seven demo prompts were rehearsed on each model:

| Model | All prompts correct | Seconds per turn |
|---|---|---|
| `gpt-6.1-terra` | Yes | 1.9 to 7.7 |
| `gpt-6.1-sol` | Yes | 3.6 to 10.9 |
| `gpt-5` | Yes | 8 to 22 |
| `gpt-5.4-mini` | No: never called `request_booking` | 1.8 to 5.6 |
| `gpt-4.1-mini` | No: never called `request_booking` | 2 to 14 |

To change model, run `azd env set AZURE_AI_MODEL_DEPLOYMENT_NAME <deployment>` and then `azd deploy`.

**On Windows, run these from PowerShell.** Git Bash rewrites arguments that start with `/`, so `--project-id` fails with a format error.

## 4. Allow the agent's identity to call the server

Every hosted agent gets its own Microsoft Entra **agent identity** at deploy time. Find its client ID in the Foundry portal (the agent's details page) or with `azd ai agent show` (use **Instance Identity Client ID**, not the blueprint), then add it to the server's allowed callers:

```bash
cd <path-to-repo>/mcp-server
azd env set ALLOWED_CALLER_CLIENT_IDS <agent-identity-client-id>
azd provision
```

Then redeploy the agent so it reconnects at startup (the first start fails with 401 until the identity is allowed):

```bash
cd ~/jatech-agent-work/jatech-store-agent
azd deploy
azd ai agent invoke --new-conversation "What books has Jonah Andersson written?"
azd ai agent monitor --follow      # optional: stream the agent's logs
```

## 5. Run the agent locally (rehearsal and fallback)

```bash
cd hosted-agent/JonahAnderssonTech.StoreAgent
cp .env.example .env      # set FOUNDRY_PROJECT_ENDPOINT and FOUNDRY_MODEL
az login
dotnet run                # agent on http://localhost:8088

curl -sS -X POST http://localhost:8088/responses -H "Content-Type: application/json" \
  -d '{"input":"Which trainings does Jonah offer?","stream":false}'
```

With the defaults in `.env`, the local agent calls the **local** Functions host, so no auth is involved.

**Call the deployed server from your laptop (optional).** Pre-authorize the Azure CLI app on the server as well as VS Code (`azd env set PRE_AUTHORIZED_CLIENT_IDS "aebc6443-996d-45c2-90f0-388ff96faa56,04b07795-8ddb-461a-bbee-02f9e1bf7b46"`, then `azd provision`), then set `MCP_SERVER_URL` and `MCP_SERVER_AUDIENCE` in `.env` to the Azure values.

## 6. Run the store front (optional)

A chat page for the deployed agent, with a side panel showing each tool call, its arguments and the server's answer. Refusals get a red **Server said no** badge.

```bash
az login
cd frontend/JonahAnderssonTech.StoreWeb
cp appsettings.local.example.json appsettings.local.json   # first run only; git-ignored
dotnet run                # http://localhost:5080
```

Set `Foundry:AgentResponsesUrl` in `appsettings.local.json` to your agent's Responses URL. Run the app from that folder so it finds `wwwroot` and its settings. The Foundry endpoint needs an Entra token, so the app calls the agent with your Azure CLI sign-in. It has no sign-in of its own, so **keep it on localhost**. Hosting it needs its own authentication first. It loads nothing from the internet except the agent call.

## Security notes, which map to the "Secure it" slide

- **No keys.** The agent authenticates with its own Entra agent identity. The function app's built-in auth checks the token audience and the allowed client apps.
- **The server enforces the rules, not the prompt.** `request_booking` only accepts bookable items (training and services), validates name, email and message, refuses duplicates within a running instance, and only creates a request with status `PendingReviewByJonah`. Nothing is confirmed or invoiced. Prices come only from `catalog.json`, so the agent can't invent a price or discount.
- **Least privilege.** Set `MCP_ALLOWED_TOOLS=search_catalog,get_product` to give a read-only agent.
- **Your settings stay out of git.** `.env`, `local.settings.json`, `appsettings.local.json` and `.azure/` are git-ignored. Copy the `.example` files and keep real values there.
- **Local apps stay local.** The store front and the local agent use your Azure sign-in and have no login of their own, so they listen on localhost only, and the store front refuses other `Host` headers. Keep it that way on shared networks.
- **Observability.** Every tool call is logged on the server, and the logs reach Application Insights through OpenTelemetry. They show the tool and its arguments, not the caller.
- **Demo shortcuts.** Booking requests are kept in memory, so a restart or a second instance forgets them and duplicates can get through. Production needs a shared store. Also, `host.json` sets the MCP webhook to `Anonymous` because Entra built-in auth is the gate. Don't deploy with `ENABLE_AUTH=false`, or the endpoint is open to anyone.

## Clean up

```bash
cd mcp-server && azd down
# Don't run azd down in the agent folder when it uses an existing Foundry project:
# its environment points at that project's resource group. Delete only the agent:
az rest --method delete --url "<project-endpoint>/agents/jatech-store-agent" \
  --url-parameters api-version=v1 force=true --resource https://ai.azure.com
```

## Sources

- [Quickstart: custom remote MCP server on Azure Functions](https://learn.microsoft.com/azure/azure-functions/scenario-custom-remote-mcp-server)
- [MCP tool trigger for Azure Functions](https://learn.microsoft.com/azure/azure-functions/functions-bindings-mcp-tool-trigger)
- [Connect an MCP server on Azure Functions to Foundry Agent Service](https://learn.microsoft.com/azure/azure-functions/functions-mcp-foundry-tools)
- [Host Agent Framework agents as Foundry hosted agents](https://learn.microsoft.com/azure/foundry/how-to/develop/framework-hosted-agents)
- [Hosted agent token service](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-token-service)
- [Agent Framework sample: Hosted-McpTools](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/04-hosting/FoundryHostedAgents/responses/Hosted-McpTools)

## License

[MIT](LICENSE). The Bicep templates in `mcp-server/infra` are based on a Microsoft sample, under the MIT license in [`LICENSE-sample.md`](mcp-server/infra/LICENSE-sample.md).
