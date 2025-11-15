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
