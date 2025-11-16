using PluginContracts;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TimePlugin;

public sealed class TimeEndpoints : IEndpointModule
{
    public string Name => "time";

    public void Register(IPluginEndpointRegistry r)
    {
        r.AddGet("/time/utc", (Func<object>)(() => new { utc = DateTime.UtcNow }));
        r.AddGet("/time/local", (Func<object>)(() => new { local = DateTime.Now, tz = TimeZoneInfo.Local.Id }));
        r.AddGet("/time/tz/{id}", (Func<string, object>)(id =>
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                return new { id, now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz) };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }));
        r.AddGet("/time/culture/{name}", (Func<string, object>)(name =>
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(name);
                return new { name, culture.DisplayName, culture.DateTimeFormat.ShortDatePattern };
            }
            catch (Exception ex)
            {
                return new { error = ex.Message };
            }
        }));
        r.AddGet("/time/runtime", (Func<object>)(() => new
        {
            os = RuntimeInformation.OSDescription,
            framework = RuntimeInformation.FrameworkDescription,
            processStart = System.Diagnostics.Process.GetCurrentProcess().StartTime
        }));
    }

    public void Dispose() { }
}

// Build: dotnet build "c:\Users\Speid\source\dotnet tests\single\TimePlugin\TimePlugin.csproj" -c Release
// Output DLL: c:\Users\Speid\source\dotnet tests\single\TimePlugin\bin\Release\net10.0.0\TimePlugin.dll
// Copy to: c:\Users\Speid\source\dotnet tests\single\WebHost\bin\Debug\net10.0.0\Plugins\
