using PluginContracts;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http; // added

namespace EchoPlugin;

public sealed class EchoEndpoints : IEndpointModule
{
    public string Name => "echo";

    public void Register(IPluginEndpointRegistry r)
    {
        r.AddPost("/echo/plain", (Func<HttpRequest, Task<object>>)(async req =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            return new { length = bytes.Length, text = Encoding.UTF8.GetString(bytes) };
        }));

        r.AddPost("/echo/json", (Func<HttpRequest, Task<object>>)(async req =>
        {
            try
            {
                var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
                return new { received = doc.RootElement.Clone() };
            }
            catch (Exception ex) { return new { error = ex.Message }; }
        }));

        r.AddGet("/echo/ping", (Func<object>)(() => new { pong = DateTime.UtcNow }));
    }

    public void Dispose() { }
}

// Note: Scalar integration is in WebHost project; this plugin requires no changes for OpenAPI/Scalar.
