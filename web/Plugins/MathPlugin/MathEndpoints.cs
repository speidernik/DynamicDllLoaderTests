using PluginContracts;

namespace MathPlugin;

public sealed class MathEndpoints : IEndpointModule
{
    public string Name => "math";

    public void Register(IPluginEndpointRegistry r)
    {
        r.AddGet("/math/fib/{n:int}", (Func<int, object>)(n =>
        {
            if (n < 0 || n > 92) return new { error = "n must be 0..92" };
            long a = 0, b = 1;
            for (int i = 0; i < n; i++) { (a, b) = (b, a + b); }
            return new { n, value = a };
        }));
        r.AddGet("/math/factorial/{n:int}", (Func<int, object>)(n =>
        {
            if (n < 0 || n > 20) return new { error = "n must be 0..20" };
            long v = 1;
            for (int i = 2; i <= n; i++) v *= i;
            return new { n, value = v };
        }));
        r.AddGet("/math/prime/{n:int}", (Func<int, object>)(n =>
        {
            if (n < 2) return new { n, isPrime = false };
            for (int i = 2; i * i <= n; i++)
                if (n % i == 0) return new { n, isPrime = false };
            return new { n, isPrime = true };
        }));
        r.AddGet("/math/sum/{a:int}/{b:int}", (Func<int,int,object>)((a, b) => new { a, b, sum = a + b }));
    }

    public void Dispose() { }
}
