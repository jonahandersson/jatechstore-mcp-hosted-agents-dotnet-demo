using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.Extensions.Logging;

namespace JonahAnderssonTech.StoreMcp;

/// <summary>
/// MCP tools for the Jonah Andersson Tech store: books, courses, training and services.
/// Each [McpToolTrigger] function becomes one tool that any MCP client can discover and call.
/// The descriptions are what the model reads, so they are written like API documentation.
/// </summary>
public class StoreTools(StoreCatalog catalog, ILogger<StoreTools> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ── Read-only tool ───────────────────────────────────────────────────────
    [Function(nameof(SearchCatalog))]
    public string SearchCatalog(
        [McpToolTrigger("search_catalog",
            "Searches the Jonah Andersson Tech store: books, on-demand courses, instructor-led training and services " +
            "(consulting, speaking, mentoring). Returns product IDs, titles, categories and prices exactly as listed.")]
            ToolInvocationContext context)
    {
        // Optional properties are declared in Program.cs (ConfigureMcpTool) and read here,
        // so a call without them still binds.
        var query = context.Arguments?.GetValueOrDefault("query")?.ToString();
        var category = context.Arguments?.GetValueOrDefault("category")?.ToString();

        logger.LogInformation("Tool {Tool} called. Query: {Query}, category: {Category}", context.Name, query, category);

        var results = catalog.Search(query, category);
        return results.Count > 0
            ? JsonSerializer.Serialize(results, Json)
            : "No matching products. Try fewer keywords or another category (Book, Course, Training, Service).";
    }

    // ── Read-only tool ───────────────────────────────────────────────────────
    [Function(nameof(GetProduct))]
    public string GetProduct(
        [McpToolTrigger("get_product",
            "Gets full details for one product: description, format, language, price, where to buy, " +
            "and whether it can be booked. Use the product ID from search_catalog.")]
            ToolInvocationContext context,
        [McpToolProperty("product_id", "The product ID, e.g. BOOK-LMA or WORKSHOP-MCP-AGENTS.", isRequired: true)]
            string productId)
    {
        logger.LogInformation("Tool {Tool} called for {ProductId}", context.Name, productId);

        if (!StoreCatalog.IsValidProductId(productId))
            return $"'{productId}' is not a valid product ID. Use search_catalog to find one.";

        var product = catalog.Find(productId);
        return product is not null
            ? JsonSerializer.Serialize(product, Json)
            : $"No product with ID {productId}. Use search_catalog to find the right one.";
    }

    // ── State-changing tool, with guardrails in the server ──────────────────
    [Function(nameof(RequestBooking))]
    public string RequestBooking(
        [McpToolTrigger("request_booking",
            "Sends a booking request for training, a workshop or a service to Jonah Andersson Tech. " +
            "Nothing is confirmed, scheduled or invoiced automatically: Jonah reviews every request personally. " +
            "Only call this after the user has confirmed the product, their name, their email and their message.")]
            ToolInvocationContext context,
        [McpToolProperty("product_id", "The product ID of a bookable item (Training or Service).", isRequired: true)]
            string productId,
        [McpToolProperty("contact_name", "The name of the person making the request.", isRequired: true)]
            string contactName,
        [McpToolProperty("contact_email", "Email address Jonah can reply to.", isRequired: true)]
            string contactEmail,
        [McpToolProperty("message", "What they need: team size, preferred dates, location, goals.", isRequired: true)]
            string message)
    {
        logger.LogInformation("Tool {Tool} called for {ProductId}", context.Name, productId);

        // The model is an untrusted caller: the server enforces the business rules, not the prompt.
        if (!StoreCatalog.IsValidProductId(productId))
            return $"'{productId}' is not a valid product ID.";

        var product = catalog.Find(productId);
        if (product is null)
            return $"No product with ID {productId}.";

        if (!product.Bookable)
            return $"'{product.Title}' can't be booked. It's a {product.Category.ToLowerInvariant()} " +
                   $"you can get here: {product.Url ?? catalog.Website}";

        if (string.IsNullOrWhiteSpace(contactName) || contactName.Trim().Length < 2)
            return "Please ask for the person's name.";

        if (!StoreCatalog.IsValidEmail(contactEmail))
            return "Please ask for a valid email address so Jonah can reply.";

        if (string.IsNullOrWhiteSpace(message) || message.Trim().Length < 10)
            return "Please ask what they need (team size, dates, location or goals) in a sentence or two.";

        if (message.Length > 1000)
            return "The message is too long. Summarize it in under 1000 characters.";

        var existing = catalog.FindOpenRequest(product.Id, contactEmail);
        if (existing is not null)
            return $"Request {existing.RequestId} for '{product.Title}' already exists for {contactEmail} " +
                   $"(status: {existing.Status}).";

        var request = catalog.CreateRequest(product, contactName, contactEmail, message);
        logger.LogWarning("Booking request {RequestId} for {ProductId} created, waiting for Jonah's review",
            request.RequestId, request.ProductId);

        return $"Request {request.RequestId} for '{request.ProductTitle}' was sent. Status: {request.Status}. " +
               $"Jonah reviews every request personally and replies to {request.ContactEmail}. " +
               "Nothing has been confirmed, scheduled or invoiced yet.";
    }
}
