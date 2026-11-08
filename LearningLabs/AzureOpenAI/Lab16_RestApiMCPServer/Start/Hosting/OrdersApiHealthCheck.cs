using Microsoft.Extensions.Diagnostics.HealthChecks;
using RestApiMCPServer.RestClient;

namespace RestApiMCPServer.Hosting;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// Health check of the dependency of the MCP server: the REST microservice answers on its /health endpoint.
/// The container platform (Docker, Kubernetes, Azure Container Apps) probes /health of the MCP server: an MCP server whose
/// microservice is down is reported Degraded, so that its tools are not used blindly.
/// </summary>
public sealed class OrdersApiHealthCheck(OrdersApiClient ordersApi) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await ordersApi.IsHealthyAsync(cancellationToken)
            ? HealthCheckResult.Healthy("OrdersApi answers.")
            : HealthCheckResult.Degraded("OrdersApi does not answer.");
}
