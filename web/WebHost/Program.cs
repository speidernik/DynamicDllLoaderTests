using Microsoft.OpenApi.Models;            // ensure present
using Microsoft.OpenApi.Writers;           // added
using Microsoft.AspNetCore.Http;           // added for IHttpMethodMetadata
using System.Text.Json;
using System.Text; // added for UTF8Encoding
using WebHost;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PluginEndpointDataSource>();
builder.Services.AddEndpointsApiExplorer(); // optional

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<PluginEndpointDataSource>();

// Dynamic swagger spec (only plugin endpoints)
app.MapGet("/swagger/v1/swagger.json", (PluginEndpointDataSource ds) =>
{
    var doc = WebHost.DynamicOpenApi.Build(ds);
    using var ms = new MemoryStream();
    using var sw = new StreamWriter(ms, new UTF8Encoding(false), 1024, leaveOpen: true);
    var jsonWriter = new Microsoft.OpenApi.Writers.OpenApiJsonWriter(sw);
    doc.SerializeAsV3(jsonWriter);
    sw.Flush();
    return Results.Bytes(ms.ToArray(), "application/json");
});

// Swagger UI (points to dynamic spec)
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/swagger/v1/swagger.json", "Dynamic Plugin API v1");
    o.RoutePrefix = "swagger";
});

// Static endpoints (not included in document)
app.MapGet("/", () => "WebHost running. Drop plugin DLLs into Plugins folder.");
app.MapGet("/_plugins", () => dataSource.Endpoints.Select(e => e.DisplayName).ToArray());

// Routing for plugin endpoints
app.UseRouting();
app.UseEndpoints(endpoints =>
{
    endpoints.DataSources.Add(dataSource);
});

var pluginsDir = Path.Combine(AppContext.BaseDirectory, "Plugins");
var manager = new PluginManager(pluginsDir, dataSource, Console.Out);
manager.Start();

app.Lifetime.ApplicationStopping.Register(() => manager.Dispose());

app.Run();
