using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using System.Collections.Concurrent;
using PluginContracts; // added

namespace WebHost;

public sealed class PluginEndpointDataSource : EndpointDataSource, PluginContracts.IPluginEndpointRegistry
{
    private readonly ConcurrentDictionary<string, List<Endpoint>> _pluginEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _cts = new();

    public override IReadOnlyList<Endpoint> Endpoints =>
        _pluginEndpoints.Values.SelectMany(l => l).ToList();

    public override IChangeToken GetChangeToken() => new CancellationChangeToken(_cts.Token);

    private void Add(string plugin, Endpoint ep)
    {
        var list = _pluginEndpoints.GetOrAdd(plugin, _ => new List<Endpoint>());
        list.Add(ep);
        Refresh();
    }

    private void Refresh()
    {
        var old = _cts;
        _cts = new();
        old.Cancel();
        old.Dispose();
    }

    public void RemovePlugin(string plugin)
    {
        if (_pluginEndpoints.Remove(plugin, out _))
            Refresh();
    }

    public void AddGet(string pattern, Delegate handler) => AddEndpoint(pattern, "GET", handler);
    public void AddPost(string pattern, Delegate handler) => AddEndpoint(pattern, "POST", handler);

    private void AddEndpoint(string pattern, string httpMethod, Delegate handler)
    {
        var rd = RequestDelegateFactory.Create(handler).RequestDelegate;
        var routePattern = RoutePatternFactory.Parse(pattern);
        var builder = new RouteEndpointBuilder(rd, routePattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(new[] { httpMethod }));
        builder.DisplayName = $"Plugin:{pattern}"; // required prefix for inclusion in dynamic swagger
        var ep = builder.Build();
        // plugin name = first segment after leading slash if present
        var pluginName = pattern.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown";
        Add(pluginName, ep);
    }
}
