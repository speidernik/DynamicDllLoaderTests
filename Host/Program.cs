using Host;

var baseDir = AppContext.BaseDirectory;
var pluginsDir = Path.Combine(baseDir, "Plugins");
Directory.CreateDirectory(pluginsDir);

using var manager = new PluginManager(pluginsDir, Console.Out);
manager.Start();

Console.WriteLine($"Watching: {pluginsDir}");
Console.WriteLine("Drop plugin DLLs here to load. Delete/replace to unload/reload. Press 'q' then Enter to quit.");

while (true)
{
    var line = Console.ReadLine();
    if (string.Equals(line, "q", StringComparison.OrdinalIgnoreCase)) break;
}
