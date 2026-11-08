using System.ComponentModel;

namespace RestApiMCPServer.RestClient;

// ============================================
// PROVIDED - do not edit
// ============================================
// The data the MCP server reads from and sends to the REST microservice. They are also the results of the MCP tools:
// with UseStructuredContent, the MCP SDK generates the output schema of a tool from these types, descriptions included.
// The MCP server owns these types: the microservice can add fields without changing what the agents see.

[Description("A product of the catalog.")]
public record Product(
    [property: Description("Stock keeping unit, the id of the product, e.g. KB-100.")] string Sku,
    [property: Description("Name of the product.")] string Name,
    [property: Description("Category: keyboards, mice, monitors or headsets.")] string Category,
    [property: Description("Unit price in euros, VAT included.")] decimal Price,
    [property: Description("Units in stock. 0 means out of stock.")] int Stock);

[Description("The products found.")]
public record ProductList(
    [property: Description("Number of products found.")] int Count,
    [property: Description("The products, sorted by SKU.")] IReadOnlyList<Product> Products);

[Description("A line of an order.")]
public record OrderLine(
    [property: Description("SKU of the product.")] string Sku,
    [property: Description("Name of the product.")] string Name,
    [property: Description("Quantity ordered.")] int Quantity,
    [property: Description("Unit price in euros when the order was placed.")] decimal UnitPrice);

[Description("An order of a customer.")]
public record Order(
    [property: Description("Id of the order, e.g. ORD-1002.")] string OrderId,
    [property: Description("Id of the customer, e.g. CUST-042.")] string CustomerId,
    [property: Description("Pending, Shipped or Cancelled.")] string Status,
    [property: Description("The lines of the order.")] IReadOnlyList<OrderLine> Lines,
    [property: Description("Total in euros.")] decimal Total,
    [property: Description("When the order was placed (UTC).")] DateTimeOffset CreatedAt,
    [property: Description("Who placed the order: the caller of the microservice, and on whose behalf.")] string PlacedBy);

[Description("A line of a new order.")]
public record OrderLineRequest(
    [property: Description("SKU of the product, e.g. KB-100.")] string Sku,
    [property: Description("Quantity, from 1 to 50.")] int Quantity);

/// <summary>Body of POST /api/orders.</summary>
public record PlaceOrderRequest(string CustomerId, IReadOnlyList<OrderLineRequest> Lines);

/// <summary>
/// A call to the microservice that did not succeed: its HTTP status code and the problem details it returned
/// (title, detail, validation errors), or no status code when the microservice could not be reached or did not answer in time.
/// </summary>
public sealed class OrdersApiException(int? statusCode, string title, string? detail, Exception? inner = null)
    : Exception($"OrdersApi {(statusCode is null ? "unreachable" : $"answered {statusCode}")}: {title} {detail}", inner)
{
    /// <summary>HTTP status code of the answer; null when there was no answer (connection refused, timeout).</summary>
    public int? StatusCode { get; } = statusCode;

    public string Title { get; } = title;

    /// <summary>The detail of the problem, written by the microservice for its callers (validation errors included).</summary>
    public string? Detail { get; } = detail;
}
