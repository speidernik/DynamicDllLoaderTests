using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http;
using System.Linq;

namespace WebHost;

internal static class DynamicOpenApi
{
    public static OpenApiDocument Build(PluginEndpointDataSource ds)
    {
        var doc = new OpenApiDocument
        {
            Info = new OpenApiInfo { Title = "Dynamic Plugin API", Version = "v1" },
            Paths = new OpenApiPaths(),
            Components = new OpenApiComponents()
        };

        foreach (var ep in ds.Endpoints.Where(e => e.DisplayName?.StartsWith("Plugin:", StringComparison.OrdinalIgnoreCase) == true))
        {
            if (ep is not RouteEndpoint routeEp) continue;

            var pattern = routeEp.RoutePattern;
            var path = PatternToOpenApiPath(pattern);

            if (!doc.Paths.TryGetValue(path, out var pathItem))
            {
                pathItem = new OpenApiPathItem();
                doc.Paths[path] = pathItem;
            }

            var methods = routeEp.Metadata.OfType<IHttpMethodMetadata>()
                .SelectMany(m => m.HttpMethods)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var m in methods)
            {
                var op = new OpenApiOperation
                {
                    Summary = routeEp.DisplayName,
                    Tags = new List<OpenApiTag> { new() { Name = ExtractPluginTag(routeEp.DisplayName) } },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse { Description = "OK" }
                    }
                };

                foreach (var p in pattern.Parameters)
                {
                    op.Parameters.Add(new OpenApiParameter
                    {
                        Name = p.Name,
                        In = ParameterLocation.Path,
                        Required = true,
                        Schema = new OpenApiSchema { Type = InferParamType(p) }
                    });
                }

                var opType = m.ToLowerInvariant() switch
                {
                    "get" => OperationType.Get,
                    "post" => OperationType.Post,
                    "put" => OperationType.Put,
                    "delete" => OperationType.Delete,
                    "patch" => OperationType.Patch,
                    _ => (OperationType?)null
                };
                if (opType.HasValue)
                    pathItem.Operations[opType.Value] = op;
            }
        }

        return doc;
    }

    private static string PatternToOpenApiPath(RoutePattern pattern)
    {
        var segments = pattern.PathSegments.Select(seg =>
            string.Concat(seg.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart lit => lit.Content,
                RoutePatternParameterPart prm => "{" + prm.Name + "}",
                _ => ""
            })));
        var path = "/" + string.Join('/', segments).Trim('/');
        return path == "/" ? "/" : path;
    }

    private static string InferParamType(RoutePatternParameterPart p)
    {
        var policies = p.ParameterPolicies.Select(pol => pol.Content).ToList();
        if (policies.Contains("int")) return "integer";
        if (policies.Contains("bool")) return "boolean";
        return "string";
    }

    private static string ExtractPluginTag(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "plugin";
        var idx = displayName.IndexOf(':');
        var path = idx >= 0 ? displayName[(idx + 1)..] : displayName;
        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "plugin";
    }
}
