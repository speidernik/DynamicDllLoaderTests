using PluginContracts;
using System.Timers;

namespace SamplePlugin;

public sealed class SampleFeature : IFeature
{
    private readonly System.Timers.Timer _timer;

    public string Name => "SampleFeature";

    public SampleFeature()
    {
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += OnTick;
        _timer.AutoReset = true;
    }

    public void Start()
    {
        Console.WriteLine($"[{Name}] Started");
        _timer.Start();
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        Console.WriteLine($"[{Name}] Tick {DateTime.Now:T}");
    }

    public void Dispose()
    {
        _timer.Elapsed -= OnTick;
        _timer.Stop();
        _timer.Dispose();
        Console.WriteLine($"[{Name}] Disposed");
    }
}

public sealed class SampleEndpoints : PluginContracts.IEndpointModule
{
    public string Name => "sample";

    public void Register(PluginContracts.IPluginEndpointRegistry registry)
    {
        // GET /sample/hello
        registry.AddGet("/sample/hello", (Func<object>)(() => new { message = $"Hello from plugin at {DateTime.UtcNow:O}" }));
        // GET /sample/add/{a}/{b}
        registry.AddGet("/sample/add/{a:int}/{b:int}", (Func<int,int,object>)((a, b) => new { a, b, sum = a + b }));
        // POST /sample/echo
        registry.AddPost("/sample/echo", (Func<HttpRequest, object>)(req => new
        {
            length = req.ContentLength ?? 0,
            contentType = req.ContentType
        }));
    }

    public void Dispose()
    {
        // Nothing to clean yet.
    }
}

// Time access (after dropping TimePlugin.dll):
//   GET http://localhost:5000/time/utc
//   GET http://localhost:5000/time/local
//   GET http://localhost:5000/time/tz/Europe/Berlin
//   GET http://localhost:5000/time/culture/de-DE
//   GET http://localhost:5000/time/runtime
// curl example: curl http://localhost:5000/time/utc

// Note: Only plugin endpoints (DisplayName starts with "Plugin:") are emitted to /swagger/v1/swagger.json dynamically.
// Plugin endpoints appear live in /swagger/v1/swagger.json (filtered by DisplayName "Plugin:")
// Swagger lists only active plugin endpoints (dynamic).
