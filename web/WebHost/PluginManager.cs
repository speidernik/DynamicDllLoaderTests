using System.Collections.Concurrent;
using System.Reflection;
using PluginContracts;

namespace WebHost;

public sealed class PluginManager : IDisposable
{
    private readonly string _pluginsDir;
    private readonly PluginEndpointDataSource _dataSource;
    private readonly TextWriter _log;
    private FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginHandle> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debouncers = new(StringComparer.OrdinalIgnoreCase);

    private record PluginHandle(string Path, PluginLoadContext Ctx, IEndpointModule Module);

    public PluginManager(string pluginsDir, PluginEndpointDataSource dataSource, TextWriter log)
    {
        _pluginsDir = pluginsDir;
        _dataSource = dataSource;
        _log = log;
    }

    public void Start()
    {
        Directory.CreateDirectory(_pluginsDir);
        foreach (var dll in Directory.EnumerateFiles(_pluginsDir, "*.dll"))
            DebounceReload(dll);

        _watcher = new(_pluginsDir, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
        };
        _watcher.Created += (_, e) => DebounceReload(e.FullPath);
        _watcher.Changed += (_, e) => DebounceReload(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Unload(e.OldFullPath);
            DebounceReload(e.FullPath);
        };
        _watcher.Deleted += (_, e) => Unload(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void DebounceReload(string path)
    {
        var key = Normalize(path);
        var cts = _debouncers.AddOrUpdate(key, _ => new CancellationTokenSource(), (_, old) =>
        {
            old.Cancel();
            return new CancellationTokenSource();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
                Reload(path);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.WriteLine($"[ERR] {ex.Message}"); }
            finally
            {
                if (_debouncers.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                    _debouncers.TryRemove(key, out _);
                cts.Dispose();
            }
        });
    }

    private void Reload(string path)
    {
        if (!File.Exists(path)) return;
        lock (_gate)
        {
            Unload(path);
            TryLoad(path);
        }
    }

    private void TryLoad(string path)
    {
        try
        {
            var ctx = new PluginLoadContext(Path.GetDirectoryName(path)!);
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var asm = ctx.LoadFromStream(fs);

            var type = asm.GetTypes().FirstOrDefault(t =>
                typeof(IEndpointModule).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

            if (type is null)
            {
                _log.WriteLine($"[WARN] No IEndpointModule in {Path.GetFileName(path)}");
                ctx.Unload();
                return;
            }

            var module = (IEndpointModule)Activator.CreateInstance(type)!;
            module.Register(_dataSource);
            _loaded[Normalize(path)] = new PluginHandle(path, ctx, module);
            _log.WriteLine($"[INFO] Loaded module: {module.Name}");
        }
        catch (ReflectionTypeLoadException rtle)
        {
            var msgs = string.Join("; ", rtle.LoaderExceptions.Select(e => e.Message));
            _log.WriteLine($"[ERR] {msgs}");
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[ERR] Load {Path.GetFileName(path)} failed: {ex.Message}");
        }
    }

    private void Unload(string path)
    {
        var key = Normalize(path);
        if (_loaded.Remove(key, out var handle))
        {
            var h = handle!; // assert non-null
            try
            {
                _dataSource.RemovePlugin(h.Module.Name);
                h.Module.Dispose();
                h.Ctx.Unload();
                _log.WriteLine($"[INFO] Unloaded module: {h.Module.Name}");
            }
            catch (Exception ex) { _log.WriteLine($"[WARN] Unload error: {ex.Message}"); }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static string Normalize(string p) => Path.GetFullPath(p);

    public void Dispose()
    {
        _watcher?.Dispose();
        foreach (var h in _loaded.Values)
        {
            try
            {
                _dataSource.RemovePlugin(h.Module.Name);
                h.Module.Dispose();
                h.Ctx.Unload();
            }
            catch { }
        }
        _loaded.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
