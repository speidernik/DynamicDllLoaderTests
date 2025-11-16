namespace PluginContracts;

public interface IEndpointModule : IDisposable
{
    string Name { get; }
    void Register(IPluginEndpointRegistry registry);
}

public interface IPluginEndpointRegistry
{
    void AddGet(string pattern, Delegate handler);
    void AddPost(string pattern, Delegate handler);
    // Can extend for Put/Delete/etc.
}
