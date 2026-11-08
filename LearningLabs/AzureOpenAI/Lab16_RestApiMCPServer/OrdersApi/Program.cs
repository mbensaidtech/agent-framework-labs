// ============================================
// PROVIDED - do not edit
// ============================================
// OrdersApi: the EXISTING microservice of the provider team (catalog and orders of an online shop).
// It is a plain ASP.NET Core REST API: it knows nothing about MCP nor AI agents, and the lab never changes it.
// The MCP server of the lab (Start/ or Solution/) is another program that calls this API over HTTP.
//
// Run it alone:   dotnet run            (listens on http://localhost:5300, setting "Urls")
// Try it:         curl -H "X-Api-Key: dev-backoffice-key" http://localhost:5300/api/products
//
// Data is kept in memory and seeded at startup: every restart gives the same catalog and orders.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<Store>();

WebApplication app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Service-to-service authentication: every caller has its own API key (OrdersApi:ApiKeys, "caller name": "key").
// The caller name is kept with the orders it places. /health stays anonymous for the orchestrator (Docker, Kubernetes...).
Dictionary<string, string> apiKeys = app.Configuration.GetSection("OrdersApi:ApiKeys").Get<Dictionary<string, string>>() ?? [];
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        string presented = context.Request.Headers["X-Api-Key"].ToString();
        string? caller = apiKeys.FirstOrDefault(entry => FixedTimeEquals(entry.Value, presented)).Key;
        if (caller is null)
        {
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized",
                detail: "A valid X-Api-Key header is required.").ExecuteAsync(context);
            return;
        }

        context.Items["caller"] = caller;
    }

    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "OrdersApi" }));

// ----- Catalog -----

app.MapGet("/api/products", (string? category, decimal? maxPrice, Store store) =>
    store.Products.Values
        .Where(p => category is null || p.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
        .Where(p => maxPrice is null || p.Price <= maxPrice)
        .OrderBy(p => p.Sku));

app.MapGet("/api/products/{sku}", (string sku, Store store) =>
    store.Products.TryGetValue(sku.ToUpperInvariant(), out Product? product)
        ? Results.Ok(product)
        : Results.Problem(statusCode: 404, title: "Product not found", detail: $"Product {sku} does not exist."));

// ----- Orders -----

app.MapGet("/api/orders/{orderId}", (string orderId, Store store) =>
{
    // Simulated incident: the database of this order is unavailable (to see how a 500 reaches the MCP client)
    if (orderId.Equals("ORD-0500", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Simulated outage: connection to orders-db-03.internal:5432 refused (pool exhausted).");
    }

    return store.Orders.TryGetValue(orderId.ToUpperInvariant(), out Order? order)
        ? Results.Ok(order)
        : Results.Problem(statusCode: 404, title: "Order not found", detail: $"Order {orderId} does not exist.");
});

app.MapPost("/api/orders", (PlaceOrderRequest request, HttpContext context, Store store) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId) || !store.Customers.ContainsKey(request.CustomerId.ToUpperInvariant()))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["customerId"] = [$"Customer '{request.CustomerId}' does not exist."] });
    }

    if (request.Lines is not { Count: >= 1 and <= 10 })
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["lines"] = ["An order has 1 to 10 lines."] });
    }

    // The caller placing the order (the MCP server) may say on whose behalf it acts: kept for the audit
    string caller = (string)context.Items["caller"]!;
    string onBehalfOf = context.Request.Headers["X-On-Behalf-Of"].ToString();
    string placedBy = string.IsNullOrWhiteSpace(onBehalfOf) ? caller : $"{caller} on behalf of {onBehalfOf}";

    lock (store)
    {
        List<OrderLine> lines = [];
        foreach (OrderLineRequest line in request.Lines)
        {
            if (line.Quantity is < 1 or > 50)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["quantity"] = [$"The quantity of {line.Sku} must be between 1 and 50."] });
            }

            if (!store.Products.TryGetValue(line.Sku.ToUpperInvariant(), out Product? product))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["sku"] = [$"Product {line.Sku} does not exist."] });
            }

            if (product.Stock < line.Quantity)
            {
                return Results.Problem(statusCode: 409, title: "Not enough stock",
                    detail: $"Only {product.Stock} unit(s) of {product.Sku} ({product.Name}) left in stock, {line.Quantity} requested.");
            }

            lines.Add(new OrderLine(product.Sku, product.Name, line.Quantity, product.Price));
        }

        foreach (OrderLine line in lines)
        {
            Product product = store.Products[line.Sku];
            store.Products[line.Sku] = product with { Stock = product.Stock - line.Quantity };
        }

        Order order = new(store.NextOrderId(), request.CustomerId.ToUpperInvariant(), "Pending", lines,
            lines.Sum(l => l.Quantity * l.UnitPrice), DateTimeOffset.UtcNow, placedBy);
        store.Orders[order.OrderId] = order;
        return Results.Created($"/api/orders/{order.OrderId}", order);
    }
});

app.MapPost("/api/orders/{orderId}/cancel", (string orderId, Store store) =>
{
    if (!store.Orders.TryGetValue(orderId.ToUpperInvariant(), out Order? order))
    {
        return Results.Problem(statusCode: 404, title: "Order not found", detail: $"Order {orderId} does not exist.");
    }

    if (order.Status != "Pending")
    {
        return Results.Problem(statusCode: 409, title: "Order cannot be cancelled", detail: $"Order {orderId} is {order.Status}.");
    }

    store.Orders[order.OrderId] = order with { Status = "Cancelled" };
    return Results.Ok(store.Orders[order.OrderId]);
});

app.MapDelete("/api/orders/{orderId}", (string orderId, Store store) =>
    store.Orders.TryRemove(orderId.ToUpperInvariant(), out _) ? Results.NoContent() : Results.NotFound());

app.MapGet("/api/customers/{customerId}", (string customerId, Store store) =>
    store.Customers.TryGetValue(customerId.ToUpperInvariant(), out Customer? customer) ? Results.Ok(customer) : Results.NotFound());

app.Run();

static bool FixedTimeEquals(string expected, string presented) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));

record Product(string Sku, string Name, string Category, decimal Price, int Stock);
record OrderLine(string Sku, string Name, int Quantity, decimal UnitPrice);
record Order(string OrderId, string CustomerId, string Status, IReadOnlyList<OrderLine> Lines, decimal Total, DateTimeOffset CreatedAt, string PlacedBy);
record Customer(string CustomerId, string Name, string Email, string Phone);
record OrderLineRequest(string Sku, int Quantity);
record PlaceOrderRequest(string CustomerId, List<OrderLineRequest>? Lines);

sealed class Store
{
    private int _lastOrderNumber = 1003;

    public ConcurrentDictionary<string, Product> Products { get; } = new(new Dictionary<string, Product>
    {
        ["KB-100"] = new("KB-100", "Compact mechanical keyboard", "keyboards", 89.90m, 25),
        ["KB-200"] = new("KB-200", "Wireless ergonomic keyboard", "keyboards", 129.00m, 8),
        ["KB-300"] = new("KB-300", "Low-profile office keyboard", "keyboards", 39.90m, 0),
        ["MS-100"] = new("MS-100", "Wireless silent mouse", "mice", 24.90m, 60),
        ["MS-200"] = new("MS-200", "Vertical ergonomic mouse", "mice", 59.00m, 3),
        ["MN-270"] = new("MN-270", "27-inch QHD monitor", "monitors", 299.00m, 12),
        ["HS-500"] = new("HS-500", "Noise-cancelling headset", "headsets", 149.00m, 5),
    });

    public ConcurrentDictionary<string, Order> Orders { get; } = new(new Dictionary<string, Order>
    {
        ["ORD-1001"] = new("ORD-1001", "CUST-042", "Shipped", [new("MN-270", "27-inch QHD monitor", 2, 299.00m)], 598.00m,
            new DateTimeOffset(2026, 10, 30, 9, 15, 0, TimeSpan.Zero), "backoffice"),
        ["ORD-1002"] = new("ORD-1002", "CUST-042", "Pending", [new("KB-100", "Compact mechanical keyboard", 1, 89.90m), new("MS-100", "Wireless silent mouse", 1, 24.90m)], 114.80m,
            new DateTimeOffset(2026, 11, 6, 14, 2, 0, TimeSpan.Zero), "backoffice"),
        ["ORD-1003"] = new("ORD-1003", "CUST-077", "Cancelled", [new("HS-500", "Noise-cancelling headset", 1, 149.00m)], 149.00m,
            new DateTimeOffset(2026, 11, 7, 8, 40, 0, TimeSpan.Zero), "backoffice"),
    });

    // Personal data: this endpoint exists for the back office, and is deliberately NOT exposed through MCP
    public ConcurrentDictionary<string, Customer> Customers { get; } = new(new Dictionary<string, Customer>
    {
        ["CUST-042"] = new("CUST-042", "Nadia Benali", "nadia.benali@example.com", "+33 6 12 34 56 78"),
        ["CUST-077"] = new("CUST-077", "Tom Leroy", "tom.leroy@example.com", "+33 6 98 76 54 32"),
    });

    public string NextOrderId() => $"ORD-{Interlocked.Increment(ref _lastOrderNumber)}";
}
