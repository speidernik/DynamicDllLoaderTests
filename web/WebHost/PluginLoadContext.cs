using System.Reflection;
using System.Runtime.Loader;

namespace WebHost;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDir;
    private static readonly string ContractsAssemblyName = "PluginContracts";

    public PluginLoadContext(string pluginDir) : base(isCollectible: true) => _pluginDir = pluginDir;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
            return null;

        var candidate = Path.Combine(_pluginDir, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
        {
            using var fs = File.Open(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return LoadFromStream(fs);
        }
        return null;
    }
}
