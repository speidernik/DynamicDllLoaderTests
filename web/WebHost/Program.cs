using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text;
using WebHost;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// Ensure log directory exists
var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDirectory);

// Configure Serilog before building the host
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logDirectory, ".log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 90,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1))
    .CreateLogger();

try
{
    Log.Information("========================================");
    Log.Information("🚀 WebHost Application Starting");
    Log.Information("📝 Log Directory: {LogDirectory}", logDirectory);
    Log.Information("========================================");

    var builder = WebApplication.CreateBuilder(args);

    // Replace default logging with Serilog
    builder.Host.UseSerilog();

    // Configuration
    builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables();

    // Services
    builder.Services.AddSingleton<PluginEndpointDataSource>();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddHealthChecks()
        .AddCheck<PluginHealthCheck>("plugins");

    // CORS (configure as needed)
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    // Response compression
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
    });

    var app = builder.Build();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();

    // Exception handling
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "An internal error occurred. Please try again later.",
                    traceId = context.TraceIdentifier
                });
            });
        });
        app.UseHsts();
    }

    // Security headers
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    });

    app.UseHttpsRedirection();
    app.UseResponseCompression();
    app.UseCors();

    var dataSource = app.Services.GetRequiredService<PluginEndpointDataSource>();

    // Health checks
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    // Dynamic swagger spec (only plugin endpoints)
    app.MapGet("/openapi/v1.json", (PluginEndpointDataSource ds, ILogger<Program> log) =>
    {
        try
        {
            var doc = WebHost.DynamicOpenApi.Build(ds);
            using var ms = new MemoryStream();
            using var sw = new StreamWriter(ms, new UTF8Encoding(false), 1024, leaveOpen: true);
            var jsonWriter = new Microsoft.OpenApi.Writers.OpenApiJsonWriter(sw);
            doc.SerializeAsV3(jsonWriter);
            sw.Flush();
            return Results.Bytes(ms.ToArray(), "application/json");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to generate OpenAPI specification");
            return Results.Problem("Failed to generate API specification", statusCode: 500);
        }
    })
    .WithTags("System")
    .Produces(200, contentType: "application/json")
    .Produces(500);

    // Scalar UI referencing dynamic spec
    app.MapScalarApiReference(opts =>
    {
        opts.Title = "Dynamic Plugin API";
        opts.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });

    // Static endpoints (not included in document)
    app.MapGet("/", () => new
    {
        service = "WebHost",
        status = "running",
        message = "Drop plugin DLLs into Plugins folder",
        version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"
    })
    .WithTags("System")
    .ExcludeFromDescription();

    app.MapGet("/_plugins", (PluginEndpointDataSource ds) => Results.Ok(new
    {
        count = ds.Endpoints.Count,
        plugins = ds.Endpoints
            .Where(e => e.DisplayName?.StartsWith("Plugin:") == true)
            .Select(e => new
            {
                name = e.DisplayName,
                route = (e as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern?.RawText,
                metadata = e.Metadata.Select(m => m.GetType().Name).ToArray()
            })
            .ToArray()
    }))
    .WithTags("System")
    .ExcludeFromDescription();

    // Routing for plugin endpoints
    app.UseRouting();
    app.UseEndpoints(endpoints =>
    {
        endpoints.DataSources.Add(dataSource);
    });

    // Plugin Manager initialization
    var pluginsDir = builder.Configuration["PluginsDirectory"]
        ?? Path.Combine(AppContext.BaseDirectory, "Plugins");

    var pluginConfig = builder.Configuration.GetSection("PluginManager");
    var manager = new WebHost.PluginManager(
        pluginsDir,
        dataSource,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PluginManager>()
    );

    manager.EnableHotSwap = pluginConfig.GetValue("EnableHotSwap", true);
    manager.GracePeriodSeconds = pluginConfig.GetValue("GracePeriodSeconds", 30);

    try
    {
        manager.Start();
        Log.Information("✅ Plugin manager started successfully");
        Log.Information("    • Watch Directory: {PluginsDirectory}", pluginsDir);
        Log.Information("    • Hot-Swap: {HotSwapEnabled}", manager.EnableHotSwap ? "Enabled" : "Disabled");
        Log.Information("    • Grace Period: {GracePeriodSeconds}s", manager.GracePeriodSeconds);
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "❌ Failed to start plugin manager");
        throw;
    }

    // Graceful shutdown
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("🛑 Application stopping - disposing plugin manager");
        manager.Dispose();
    });

    lifetime.ApplicationStopped.Register(() =>
    {
        Log.Information("✅ Application stopped gracefully");
    });

    Log.Information("========================================");
    Log.Information("✅ WebHost configured successfully");
    Log.Information("🌐 Starting web server on {Environment}", app.Environment.EnvironmentName);
    Log.Information("========================================");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Application terminated unexpectedly");
    throw;
}
finally
{
    Log.Information("========================================");
    Log.Information("🛑 WebHost shutting down");
    Log.Information("========================================");
    await Log.CloseAndFlushAsync();
}
