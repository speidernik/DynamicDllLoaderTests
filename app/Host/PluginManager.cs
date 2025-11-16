using System.Collections.Concurrent;
using System.Reflection;
using PluginContracts;

namespace Host;

public sealed class PluginManager : IDisposable
{
    private readonly string _pluginsDir;
    private readonly TextWriter _log;
    private FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginHandle> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debouncers = new(StringComparer.OrdinalIgnoreCase);

    private record PluginHandle(PluginLoadContext Ctx, IFeature Instance, Assembly Assembly, string PluginPath);

    public PluginManager(string pluginsDir, TextWriter log)
    {
        _pluginsDir = pluginsDir;
        _log = log;
    }

    public void Start()
    {
        foreach (var dll in Directory.EnumerateFiles(_pluginsDir, "*.dll", SearchOption.TopDirectoryOnly))
            SafeDebouncedReload(dll);

        _watcher = new FileSystemWatcher(_pluginsDir, "*.dll")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
        };
        _watcher.Created += (_, e) => SafeDebouncedReload(e.FullPath);
        _watcher.Changed += (_, e) => SafeDebouncedReload(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            SafeUnload(e.OldFullPath);
            SafeDebouncedReload(e.FullPath);
        };
        _watcher.Deleted += (_, e) => SafeUnload(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void SafeDebouncedReload(string path)
    {
        // Ignore non-plugin noise (e.g., shared contracts)
        var file = Path.GetFileName(path);
        if (string.Equals(file, "PluginContracts.dll", StringComparison.OrdinalIgnoreCase))
            return;

        var key = Normalize(path);

        // Cancel previous debounce, but do NOT dispose here (let the owning task dispose its own CTS)
        var cts = _debouncers.AddOrUpdate(key, _ => new CancellationTokenSource(), (_, old) =>
        {
            old.Cancel();
            return new CancellationTokenSource();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, cts.Token); // debounce bursts
                cts.Token.ThrowIfCancellationRequested();
                Reload(path);
            }
            catch (OperationCanceledException) { /* debounced */ }
            catch (ObjectDisposedException) { /* CTS was disposed by its own task; safe to ignore */ }
            catch (Exception ex) { _log.WriteLine($"[ERR] Reload {path}: {ex.Message}"); }
            finally
            {
                // Only remove if this CTS is still the current one for the key
                if (_debouncers.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                {
                    _debouncers.TryRemove(key, out _);
                }
                cts.Dispose();
            }
        });
    }

    private void Reload(string path)
    {
        if (!WaitForReadable(path, TimeSpan.FromSeconds(5)))
        {
            _log.WriteLine($"[WARN] Timeout waiting for {path} to become readable.");
            return;
        }

        lock (_gate)
        {
            SafeUnload(path);
            TryLoad(path);
        }
    }

    private void SafeUnload(string path)
    {
        var key = Normalize(path);
        lock (_gate)
        {
            if (_loaded.Remove(key, out var handle))
            {
                try
                {
                    _log.WriteLine($"[INFO] Unloading: {Path.GetFileName(path)} ({handle.Instance.Name})");
                    handle.Instance.Dispose();
                }
                catch (Exception ex)
                {
                    _log.WriteLine($"[WARN] Dispose failed: {ex.Message}");
                }

                var ctx = handle.Ctx;
                ctx.Unload();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }

    private void TryLoad(string path)
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(path)!;
            var ctx = new PluginLoadContext(pluginDir);

            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var asm = ctx.LoadFromStream(fs);

            var type = asm.GetTypes()
                .FirstOrDefault(t => typeof(IFeature).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract);

            if (type is null)
            {
                _log.WriteLine($"[WARN] No IFeature implementation in {Path.GetFileName(path)}");
                ctx.Unload();
                return;
            }

            var instance = (IFeature)Activator.CreateInstance(type)!;
            instance.Start();

            var key = Normalize(path);
            _loaded[key] = new PluginHandle(ctx, instance, asm, path);

            _log.WriteLine($"[INFO] Loaded: {Path.GetFileName(path)} ({instance.Name})");
        }
        catch (ReflectionTypeLoadException rtle)
        {
            var msgs = string.Join("; ", rtle.LoaderExceptions.Select(e => e.Message));
            _log.WriteLine($"[ERR] Load {Path.GetFileName(path)}: {msgs}");
        }
        catch (Exception ex)
        {
            _log.WriteLine($"[ERR] Load {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static bool WaitForReadable(string path, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length >= 0) return true;
            }
            catch { }
            Thread.Sleep(50);
        }
        return false;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate)
        {
            foreach (var kv in _loaded.Values.ToArray())
            {
                try { kv.Instance.Dispose(); } catch { }
                kv.Ctx.Unload();
            }
            _loaded.Clear();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}