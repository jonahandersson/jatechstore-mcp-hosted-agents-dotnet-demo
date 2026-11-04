using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JonahAnderssonTech.StoreMcp;

public sealed record Product(
    string Id,
    string Category,      // Book | Course | Training | Service
    string Title,
    string Description,
    string Format,
    string Language,
    string Price,         // shown exactly as written in catalog.json
    bool Bookable,
    string[] Tags,
    string? Publisher = null,
    string? Isbn = null,
    string? Url = null);

public sealed record ProductSummary(string Id, string Category, string Title, string Price);

public sealed record BookingRequest(
    string RequestId,
    string ProductId,
    string ProductTitle,
    string ContactName,
    string ContactEmail,
    string Message,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// The Jonah Andersson Tech catalog, loaded from catalog.json (edit that file, not this code).
/// Booking requests are kept in memory for the demo; in production they would go to a queue,
/// a database or a CRM, and each Flex Consumption instance would share that store.
/// </summary>
public sealed partial class StoreCatalog
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Product> _products;
    private readonly ConcurrentDictionary<string, BookingRequest> _requests = new(StringComparer.OrdinalIgnoreCase);

    public StoreCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "catalog.json");
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, ReadOptions)
            ?? throw new InvalidOperationException("catalog.json is empty.");

        _products = file.Products.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Website = file.Company.Website;
    }

    public string Website { get; }

    [GeneratedRegex(@"^[A-Z0-9-]{3,40}$", RegexOptions.IgnoreCase)]
    private static partial Regex ProductIdPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    public static bool IsValidProductId(string? id) => !string.IsNullOrWhiteSpace(id) && ProductIdPattern().IsMatch(id.Trim());

    public static bool IsValidEmail(string? email) => !string.IsNullOrWhiteSpace(email) && EmailPattern().IsMatch(email.Trim());

    public Product? Find(string id) => _products.GetValueOrDefault(id.Trim());

    public IReadOnlyList<ProductSummary> Search(string? query, string? category)
    {
        IEnumerable<Product> items = _products.Values;

        if (!string.IsNullOrWhiteSpace(category))
            items = items.Where(p => p.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            items = items.Where(p => words.Any(w =>
                p.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase))));
        }

        return items
            .OrderBy(p => p.Category).ThenBy(p => p.Title)
            .Select(p => new ProductSummary(p.Id, p.Category, p.Title, p.Price))
            .ToList();
    }

    public BookingRequest? FindOpenRequest(string productId, string email) =>
        _requests.Values.FirstOrDefault(r =>
            r.ProductId.Equals(productId.Trim(), StringComparison.OrdinalIgnoreCase) &&
            r.ContactEmail.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));

    public BookingRequest CreateRequest(Product product, string name, string email, string message)
    {
        var request = new BookingRequest(
            RequestId: $"REQ-{Random.Shared.Next(1000, 9999)}",
            ProductId: product.Id,
            ProductTitle: product.Title,
            ContactName: name.Trim(),
            ContactEmail: email.Trim(),
            Message: message.Trim(),
            Status: "PendingReviewByJonah",
            CreatedAt: DateTimeOffset.UtcNow);

        _requests[request.RequestId] = request;
        return request;
    }

    private sealed record CompanyInfo(string Name, string Website);
    private sealed record CatalogFile(CompanyInfo Company, List<Product> Products);
}
