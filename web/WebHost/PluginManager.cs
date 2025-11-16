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
            _logger.LogInformation("📁 Plugin directory verified: {PluginsDirectory}", _pluginsDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to create plugin directory: {PluginsDirectory}", _pluginsDir);
            throw;
        }

        var dllFiles = Directory.EnumerateFiles(_pluginsDir, "*.dll").ToList();
        if (dllFiles.Any())
        {
            _logger.LogInformation("🔍 Scanning {PluginCount} plugin(s) in directory", dllFiles.Count);
            foreach (var dll in dllFiles)
            {
                DebounceReload(dll);
            }
        }
        else
        {
            _logger.LogInformation("📂 No plugins found in directory");
        }

        _watcher = new(_pluginsDir, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
        };
        _watcher.Created += (_, e) =>
        {
            _logger.LogInformation("➕ Plugin file created: {FileName}", Path.GetFileName(e.FullPath));
            DebounceReload(e.FullPath);
        };
        _watcher.Changed += (_, e) =>
        {
            _logger.LogInformation("📝 Plugin file modified: {FileName}", Path.GetFileName(e.FullPath));
            DebounceReload(e.FullPath);
        };
        _watcher.Renamed += (_, e) =>
        {
            _logger.LogInformation("🔄 Plugin file renamed: {OldName} → {NewName}", 
                Path.GetFileName(e.OldFullPath), Path.GetFileName(e.FullPath));
            Unload(e.OldFullPath);
            DebounceReload(e.FullPath);
        };
        _watcher.Deleted += (_, e) =>
        {
            _logger.LogInformation("🗑️ Plugin file deleted: {FileName}", Path.GetFileName(e.FullPath));
            Unload(e.FullPath);
        };
        _watcher.Error += (_, e) =>
        {
            _logger.LogError(e.GetException(), "⚠️ File system watcher encountered an error");
        };
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("✅ Plugin Manager started successfully");
        _logger.LogInformation("    • Hot-Swap: {HotSwapEnabled}", EnableHotSwap ? "Enabled" : "Disabled");
        _logger.LogInformation("    • Grace Period: {GracePeriodSeconds}s", GracePeriodSeconds);
        _logger.LogInformation("    • Watch Directory: {PluginsDirectory}", _pluginsDir);
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
                _logger.LogDebug("⏭️ Reload cancelled (debounced): {FileName}", Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during plugin reload: {FileName}", Path.GetFileName(path));
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
                _logger.LogInformation("🔄 Hot-swapping plugin: {PluginName} v{Version}", 
                    oldHandle.Module.Name,
                    oldHandle.Module.GetType().Assembly.GetName().Version?.ToString() ?? "unknown");
                
                lock (_unloadLock)
                {
                    var unloadTime = DateTime.UtcNow.AddSeconds(GracePeriodSeconds);
                    _pendingUnload.Add((unloadTime, new List<IDisposable> { oldHandle.Module }));
                    _logger.LogInformation("    ⏳ Old version scheduled for disposal at {UnloadTime:HH:mm:ss} (grace period: {Seconds}s)",
                        unloadTime, GracePeriodSeconds);
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
        var fileName = Path.GetFileName(path);
        
        try
        {
            _logger.LogDebug("📥 Loading plugin from: {FileName}", fileName);
            ctx = new PluginLoadContext(Path.GetDirectoryName(path)!);
            
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
                        _logger.LogWarning("⚠️ No IEndpointModule implementation found in {FileName}", fileName);
                        ctx.Unload();
                        return;
                    }

                    var module = (IEndpointModule)Activator.CreateInstance(type)!;
                    module.Register(_dataSource);
                    _loaded[Normalize(path)] = new PluginHandle(path, ctx, module);
                    
                    var version = module.GetType().Assembly.GetName().Version?.ToString() ?? "unknown";
                    _logger.LogInformation("✅ Plugin loaded successfully");
                    _logger.LogInformation("    • Name: {PluginName}", module.Name);
                    _logger.LogInformation("    • Version: {PluginVersion}", version);
                    _logger.LogInformation("    • File: {FileName}", fileName);
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    _logger.LogDebug("⏳ File locked, retrying... (attempt {Attempt}/5)", i + 1);
                    Thread.Sleep(100);
                }
            }
            
            _logger.LogError("❌ Failed to load plugin after 5 attempts: {FileName} (file is locked)", fileName);
        }
        catch (ReflectionTypeLoadException rtle)
        {
            var errors = rtle.LoaderExceptions
                .Where(e => e != null)
                .Select(e => e!.Message)
                .Distinct()
                .ToList();
            
            _logger.LogError("❌ Type load errors in {FileName}:", fileName);
            foreach (var error in errors)
            {
                _logger.LogError("    • {Error}", error);
            }
            ctx?.Unload();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to load plugin: {FileName}", fileName);
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
                _logger.LogInformation("🔌 Unloading plugin: {PluginName}", handle.Module.Name);
                _dataSource.RemovePlugin(handle.Module.Name);
                handle.Module.Dispose();
                handle.Ctx.Unload();
                _logger.LogInformation("    ✅ Plugin unloaded successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Error during plugin unload: {PluginName}", handle.Module.Name);
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

            if (toUnload.Any())
            {
                _logger.LogDebug("🧹 Processing {Count} pending plugin disposal(s)", toUnload.Count);
            }

            foreach (var (unloadTime, plugins) in toUnload)
            {
                foreach (var plugin in plugins)
                {
                    try
                    {
                        plugin.Dispose();
                        _logger.LogDebug("    ✅ Old plugin instance disposed after grace period");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "    ⚠️ Error disposing old plugin instance");
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

        _logger.LogInformation("🛑 Disposing Plugin Manager...");

        _watcher?.Dispose();
        
        lock (_gate)
        {
            if (_loaded.Any())
            {
                _logger.LogInformation("    Unloading {PluginCount} active plugin(s)", _loaded.Count);
            }
            
            foreach (var h in _loaded.Values)
            {
                try
                {
                    _dataSource.RemovePlugin(h.Module.Name);
                    h.Module.Dispose();
                    h.Ctx.Unload();
                    _logger.LogDebug("    • Disposed: {PluginName}", h.Module.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "    ⚠️ Error disposing plugin: {PluginName}", h.Module.Name);
                }
            }
            _loaded.Clear();
        }

        lock (_unloadLock)
        {
            if (_pendingUnload.Any())
            {
                _logger.LogDebug("    Cleaning up {Count} pending disposal(s)", _pendingUnload.Count);
            }
            
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

        _logger.LogInformation("✅ Plugin Manager disposed successfully");
    }
}
