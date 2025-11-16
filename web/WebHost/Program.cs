using Microsoft.OpenApi.Models;            // ensure present
using Microsoft.OpenApi.Writers;           // added
using Microsoft.AspNetCore.Http;           // added for IHttpMethodMetadata
using System.Text.Json;
using System.Text; // added for UTF8Encoding
using WebHost;
using Scalar.AspNetCore; // added

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PluginEndpointDataSource>();
builder.Services.AddEndpointsApiExplorer(); // optional

var app = builder.Build();
var dataSource = app.Services.GetRequiredService<PluginEndpointDataSource>();

// Dynamic swagger spec (only plugin endpoints)
app.MapGet("/openapi/v1.json", (PluginEndpointDataSource ds) =>
{
    var doc = WebHost.DynamicOpenApi.Build(ds);
    using var ms = new MemoryStream();
    using var sw = new StreamWriter(ms, new UTF8Encoding(false), 1024, leaveOpen: true);
    var jsonWriter = new Microsoft.OpenApi.Writers.OpenApiJsonWriter(sw);
    doc.SerializeAsV3(jsonWriter);
    sw.Flush();
    return Results.Bytes(ms.ToArray(), "application/json");
});

// Scalar UI referencing dynamic spec
app.MapScalarApiReference(opts =>
{
    opts.Title = "Dynamic Plugin API";
    // removed: opts.Specification (not a valid property)
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
