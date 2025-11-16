using System.Collections.Concurrent;
using System.Reflection;
using PluginContracts;
using Microsoft.Extensions.Logging;

namespace WebHost;

public sealed class PluginManager : IDisposable
{
    private readonly string _pluginsDir;
    private readonly PluginEndpointDataSource _dataSource;
    private readonly ILogger<PluginManager> _logger;
    private FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginHandle> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debouncers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime UnloadTime, List<IDisposable> Plugins)> _pendingUnload = new();
    private readonly object _unloadLock = new();
    private volatile bool _disposed;

    private record PluginHandle(string Path, PluginLoadContext Ctx, IEndpointModule Module);

    public PluginManager(string pluginsDir, PluginEndpointDataSource dataSource, ILogger<PluginManager> logger)
    {
        _pluginsDir = pluginsDir ?? throw new ArgumentNullException(nameof(pluginsDir));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool EnableHotSwap { get; set; } = true;
    public int GracePeriodSeconds { get; set; } = 30;

    public IReadOnlyDictionary<string, string> LoadedPlugins
    {
        get
        {
            lock (_gate)
            {
                return _loaded.ToDictionary(
                    kvp => kvp.Value.Module.Name,
                    kvp => Path.GetFileName(kvp.Key)
                );
            }
        }
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PluginManager));

        try
        {
            Directory.CreateDirectory(_pluginsDir);
            _logger.LogInformation("Plugin directory created/verified: {PluginsDir}", _pluginsDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create plugin directory: {PluginsDir}", _pluginsDir);
            throw;
        }

        foreach (var dll in Directory.EnumerateFiles(_pluginsDir, "*.dll"))
        {
            DebounceReload(dll);
        }

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
        _watcher.Error += (_, e) =>
        {
            _logger.LogError(e.GetException(), "File system watcher error");
        };
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("Plugin manager started. Hot-swap: {HotSwap}, Grace period: {GracePeriod}s",
            EnableHotSwap, GracePeriodSeconds);
    }

    private void DebounceReload(string path)
    {
        if (_disposed) return;

        var key = Normalize(path);
        var cts = _debouncers.AddOrUpdate(key, _ => new CancellationTokenSource(), (_, old) =>
        {
            old.Cancel();
            old.Dispose();
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
            catch (OperationCanceledException)
            {
                // Expected when debouncing
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during plugin reload: {Path}", Path.GetFileName(path));
            }
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
        if (!File.Exists(path) || _disposed) return;

        lock (_gate)
        {
            var key = Normalize(path);
            
            if (EnableHotSwap && _loaded.TryGetValue(key, out var oldHandle))
            {
                // Schedule old plugin for delayed disposal
                lock (_unloadLock)
                {
                    var unloadTime = DateTime.UtcNow.AddSeconds(GracePeriodSeconds);
                    _pendingUnload.Add((unloadTime, new List<IDisposable> { oldHandle.Module }));
                    _logger.LogInformation("Scheduled plugin '{Name}' for disposal in {Seconds}s",
                        oldHandle.Module.Name, GracePeriodSeconds);
                }
                
                _loaded.Remove(key);
                _dataSource.RemovePlugin(oldHandle.Module.Name);
            }
            else
            {
                Unload(path);
            }

            TryLoad(path);
            ProcessPendingUnloads();
        }
    }

    private void TryLoad(string path)
    {
        PluginLoadContext? ctx = null;
        try
        {
            ctx = new PluginLoadContext(Path.GetDirectoryName(path)!);
            
            // Wait for file to be released by other processes
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var asm = ctx.LoadFromStream(fs);

                    var type = asm.GetTypes().FirstOrDefault(t =>
                        typeof(IEndpointModule).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

                    if (type is null)
                    {
                        _logger.LogWarning("No IEndpointModule found in {FileName}", Path.GetFileName(path));
                        ctx.Unload();
                        return;
                    }

                    var module = (IEndpointModule)Activator.CreateInstance(type)!;
                    module.Register(_dataSource);
                    _loaded[Normalize(path)] = new PluginHandle(path, ctx, module);
                    _logger.LogInformation("Loaded plugin: {Name} v{Version}",
                        module.Name,
                        module.GetType().Assembly.GetName().Version?.ToString() ?? "unknown");
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (ReflectionTypeLoadException rtle)
        {
            var msgs = string.Join("; ", rtle.LoaderExceptions.Select(e => e?.Message ?? "unknown"));
            _logger.LogError("Type load error in {FileName}: {Errors}", Path.GetFileName(path), msgs);
            ctx?.Unload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin: {FileName}", Path.GetFileName(path));
            ctx?.Unload();
        }
    }

    private void Unload(string path)
    {
        var key = Normalize(path);
        if (_loaded.Remove(key, out var handle))
        {
            try
            {
                _dataSource.RemovePlugin(handle.Module.Name);
                handle.Module.Dispose();
                handle.Ctx.Unload();
                _logger.LogInformation("Unloaded plugin: {Name}", handle.Module.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during plugin unload: {Name}", handle.Module.Name);
            }
            
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private void ProcessPendingUnloads()
    {
        lock (_unloadLock)
        {
            var now = DateTime.UtcNow;
            var toUnload = _pendingUnload.Where(p => p.UnloadTime <= now).ToList();

            foreach (var (unloadTime, plugins) in toUnload)
            {
                foreach (var plugin in plugins)
                {
                    try
                    {
                        plugin.Dispose();
                        _logger.LogDebug("Disposed old plugin instance after grace period");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing old plugin instance");
                    }
                }
            }

            _pendingUnload.RemoveAll(p => p.UnloadTime <= now);
        }
    }

    private static string Normalize(string p) => Path.GetFullPath(p);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("Disposing plugin manager");

        _watcher?.Dispose();
        
        lock (_gate)
        {
            foreach (var h in _loaded.Values)
            {
                try
                {
                    _dataSource.RemovePlugin(h.Module.Name);
                    h.Module.Dispose();
                    h.Ctx.Unload();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing plugin: {Name}", h.Module.Name);
                }
            }
            _loaded.Clear();
        }

        // Clean up any pending unloads immediately
        lock (_unloadLock)
        {
            foreach (var (_, plugins) in _pendingUnload)
            {
                foreach (var plugin in plugins)
                {
                    try { plugin.Dispose(); }
                    catch { /* ignore during shutdown */ }
                }
            }
            _pendingUnload.Clear();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _logger.LogInformation("Plugin manager disposed");
    }
}
