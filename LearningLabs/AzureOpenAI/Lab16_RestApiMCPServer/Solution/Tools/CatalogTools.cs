using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RestApiMCPServer.RestClient;

namespace RestApiMCPServer.Tools;

/// <summary>
/// Catalog tools: two read endpoints of the microservice, GET /api/products and GET /api/products/{sku}.
/// [McpServerToolType] lets WithTools&lt;CatalogTools&gt;() find the [McpServerTool] methods. The class is created for each call,
/// with its constructor parameters (the typed HTTP client of the microservice) taken from dependency injection.
/// </summary>
[McpServerToolType]
public sealed partial class CatalogTools(OrdersApiClient ordersApi)
{
    // Solution of TODO 1: one tool per REST endpoint you choose to expose.
    // - Name: snake_case, the verb of the operation, unique on the server (what the model calls)
    // - Title: for humans (MCP Inspector, VS Code)
    // - [Description] on the method and on each parameter: this is all the model knows to decide when and how to call the tool
    // - ReadOnly = true, OpenWorld = false: annotations (hints) for the hosts, e.g. no confirmation needed for a read
    // - UseStructuredContent = true: the SDK publishes an output schema generated from the return type, and sends the result
    //   as structured content (JSON object) in addition to the text
    [McpServerTool(Name = "search_products", Title = "Search the catalog", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Searches the products of the online shop catalog, with their price in euros and their stock. " +
                 "Use it to find products by category and maximum price, or to check what is in stock.")]
    public async Task<ProductList> SearchProducts(
        [Description("Category to filter on: keyboards, mice, monitors or headsets. Omit it to search every category.")] string? category = null,
        [Description("Maximum unit price in euros, VAT included. Omit it for no limit.")] decimal? maxPrice = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Product> products = await ordersApi.SearchProductsAsync(category, maxPrice, cancellationToken);
        return new ProductList(products.Count, products);
    }

    // Solution of TODO 2: GET /api/products/{sku}
    [McpServerTool(Name = "get_product", Title = "Get a product", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets one product of the catalog by its SKU: name, category, price in euros and stock.")]
    public async Task<Product> GetProduct(
        [Description("SKU of the product, e.g. KB-100.")] string sku,
        CancellationToken cancellationToken = default)
    {
        // Solution of TODO 2: validate the arguments before they reach the REST API. A model can send anything:
        // an McpException is returned to the client as a tool error with its message (isError: true).
        if (!SkuFormat().IsMatch(sku))
        {
            throw new McpException($"'{sku}' is not a valid SKU. A SKU looks like KB-100: two letters, a dash and three digits.");
        }

        return await ordersApi.GetProductAsync(sku.ToUpperInvariant(), cancellationToken);
    }

    [GeneratedRegex("^[A-Za-z]{2}-[0-9]{3}$")]
    internal static partial Regex SkuFormat();
}
