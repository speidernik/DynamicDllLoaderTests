namespace PluginContracts;

public interface IFeature : IDisposable
{
    string Name { get; }
    void Start();
}
