using PluginContracts;
using System.Runtime.InteropServices;

namespace SystemInfoPlugin;

public sealed class SystemInfoEndpoints : IEndpointModule
{
    public string Name => "system";

    public void Register(IPluginEndpointRegistry r)
    {
        r.AddGet("/system/env/{key}", (Func<string, object>)(key =>
        {
            var value = Environment.GetEnvironmentVariable(key);
            return value is null ? new { key, found = false } : new { key, found = true, value };
        }));
        r.AddGet("/system/runtime", (Func<object>)(() => new
        {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        }));
        r.AddGet("/system/assemblies", (Func<object>)(() =>
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .OrderBy(n => n)
                .ToArray();
            return new { count = loaded.Length, assemblies = loaded };
        }));
    }

    public void Dispose() { }
}
