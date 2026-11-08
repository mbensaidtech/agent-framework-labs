using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RestApiMCPServer.Hosting;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>Writes the health report as JSON: the overall status and the status of each check (by default, only "Healthy").</summary>
public static class HealthReportWriter
{
    public static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            service = "orders-mcp-server",
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => new { status = entry.Value.Status.ToString(), entry.Value.Description })
        }));
    }
}
