// PluginContracts: shared interfaces (IFeature, IEndpointModule, IPluginEndpointRegistry) used by host and plugins for safe cross-ALC interaction.
using PluginContracts;
using System.Security.Cryptography;

namespace RandomPlugin;

public sealed class RandomEndpoints : IEndpointModule
{
    public string Name => "random";
    private readonly Random _r = new();

    public void Register(IPluginEndpointRegistry r)
    {
        r.AddGet("/random", (Func<object>)(() => new
        {
            guid = Guid.NewGuid(),
            value = _r.Next(),
            utc = DateTime.UtcNow
        }));

        r.AddGet("/random/int/{min:int}/{max:int}", (Func<int,int,object>)((min, max) =>
        {
            if (max <= min) return new { error = "max must be > min" };
            return new { min, max, value = _r.Next(min, max) };
        }));

        r.AddGet("/random/guid", (Func<object>)(() => new { value = Guid.NewGuid() }));

        r.AddGet("/random/bytes/{n:int}", (Func<int, object>)(n =>
        {
            if (n < 1 || n > 1024) return new { error = "n must be 1..1024" };
            var bytes = new byte[n];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            return new { n, base64 = Convert.ToBase64String(bytes) };
        }));
    }

    public void Dispose() { }
}
