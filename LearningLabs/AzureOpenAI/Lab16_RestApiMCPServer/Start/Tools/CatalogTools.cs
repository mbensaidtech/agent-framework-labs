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
#pragma warning disable CS9113 // ordersApi is used by the tools you write below
[McpServerToolType]
public sealed partial class CatalogTools(OrdersApiClient ordersApi)
#pragma warning restore CS9113
{
    // TODO 1: Expose GET /api/products as the MCP tool "search_products"
    // - [McpServerTool(Name = "search_products", Title = "Search the catalog", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    //   Name: snake_case, what the model calls. Title: for humans. ReadOnly/OpenWorld: annotations (hints) for the MCP hosts.
    //   UseStructuredContent: the SDK publishes an output schema generated from the return type and sends structured content.
    // - [Description("Searches the products of the online shop catalog, with their price in euros and their stock. Use it to find
    //   products by category and maximum price, or to check what is in stock.")] on the method: the model reads it to choose the tool
    // - Parameters, each with a [Description]: string? category = null (keyboards, mice, monitors or headsets),
    //   decimal? maxPrice = null (euros, VAT included), and CancellationToken cancellationToken = default (not part of the schema)
    // Hint: public async Task<ProductList> SearchProducts(...)
    //       { IReadOnlyList<Product> products = await ordersApi.SearchProductsAsync(category, maxPrice, cancellationToken);
    //         return new ProductList(products.Count, products); }

    // TODO 2: Expose GET /api/products/{sku} as the MCP tool "get_product" (Title "Get a product", read-only, closed world,
    // structured content), description "Gets one product of the catalog by its SKU: name, category, price in euros and stock."
    // and one parameter: string sku ("SKU of the product, e.g. KB-100.").
    // Validate the argument before it reaches the REST API (a model can send anything, "../admin" for example):
    // Hint: if (!SkuFormat().IsMatch(sku)) throw new McpException($"'{sku}' is not a valid SKU. A SKU looks like KB-100: two letters, a dash and three digits.");
    //       return await ordersApi.GetProductAsync(sku.ToUpperInvariant(), cancellationToken);

    // Provided: the format of a SKU
    [GeneratedRegex("^[A-Za-z]{2}-[0-9]{3}$")]
    internal static partial Regex SkuFormat();
}
