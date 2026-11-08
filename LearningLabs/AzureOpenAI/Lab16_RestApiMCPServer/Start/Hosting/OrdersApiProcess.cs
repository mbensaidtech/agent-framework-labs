using System.Diagnostics;
using CommonUtilities;

namespace RestApiMCPServer.Hosting;

// ============================================
// PROVIDED - do not edit
// ============================================

/// <summary>
/// Lab convenience: makes sure the REST microservice (the OrdersApi project, next to Start/ and Solution/) is running.
/// When <c>OrdersApi:BaseUrl</c> answers on /health, the running microservice is used (for example one you started yourself in
/// another terminal). Otherwise, if <c>OrdersApi:StartIfNotRunning</c> is true, it is started as a <b>separate process</b>
/// (<c>dotnet run</c>) and stopped with the lab. In production the microservice is deployed and run by its own team: the MCP
/// server only knows its URL and its API key.
/// </summary>
public sealed class OrdersApiProcess : IAsyncDisposable
{
    private readonly Process? _process;

    private OrdersApiProcess(Process? process) => _process = process;

    public static async Task<OrdersApiProcess> EnsureRunningAsync(OrdersApiSettings settings)
    {
        using HttpClient http = new() { BaseAddress = new Uri(settings.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(2) };
        if (await IsHealthyAsync(http))
        {
            ColoredConsole.WritePrimaryLogLine($"REST microservice: OrdersApi already running at {settings.BaseUrl}");
            return new OrdersApiProcess(null);
        }

        if (!settings.StartIfNotRunning)
        {
            ColoredConsole.WriteWarningLine($"REST microservice: OrdersApi does not answer at {settings.BaseUrl} (OrdersApi:StartIfNotRunning is false).");
            return new OrdersApiProcess(null);
        }

        string project = FindProject();
        Process process = new()
        {
            StartInfo = new ProcessStartInfo("dotnet", ["run", "--project", project, "--", "--urls", settings.BaseUrl])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        List<string> errors = [];
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.Add(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        OrdersApiProcess started = new(process);

        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(120))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"OrdersApi stopped (exit code {process.ExitCode}) before it was ready: {string.Join(Environment.NewLine, errors.TakeLast(10))}");
            }

            if (await IsHealthyAsync(http))
            {
                ColoredConsole.WritePrimaryLogLine($"REST microservice: OrdersApi started as a separate process (PID {process.Id}) at {settings.BaseUrl}");
                return started;
            }

            await Task.Delay(500);
        }

        await started.DisposeAsync();
        throw new TimeoutException($"OrdersApi did not answer at {settings.BaseUrl}/health within 120 s.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
    }

    private static async Task<bool> IsHealthyAsync(HttpClient http)
    {
        try
        {
            using HttpResponseMessage response = await http.GetAsync("health");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // The program runs from <lab>/Start|Solution/bin/Debug/net10.0: look for <lab>/OrdersApi/OrdersApi.csproj upwards
    private static string FindProject()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "OrdersApi", "OrdersApi.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("OrdersApi/OrdersApi.csproj not found above " + AppContext.BaseDirectory);
    }
}
