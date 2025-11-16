# Line-by-line explanation (concise) — Program.cs & DynamicOpenApi.cs

This document explains the purpose and behavior of the main runtime files:
- Program.cs — application startup, logging, middleware, endpoints and plugin manager lifecycle.
- DynamicOpenApi.cs — runtime OpenAPI (Swagger) document builder for dynamic plugin routes.

---

## Program.cs — overall structure & intent
High-level intent: configure logging, build a WebApplication, register services (plugin endpoint source, health checks, CORS, compression), map endpoints (health, OpenAPI for plugins, UI, static info), start a plugin manager, and run the app while ensuring graceful shutdown and robust error logging.

Key sections explained:

1) Using directives (top of file)
- using Microsoft.OpenApi.Models; etc.
  - Purpose: import types used later (OpenAPI models, ASP.NET Core routing/HTTP primitives, Serilog).
  - Why: avoids fully-qualified names; pulls in extension methods and types used in the file.

2) Ensure log directory exists
- var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
- Directory.CreateDirectory(logDirectory);
  - Purpose: calculate runtime logs folder and create it if missing.
  - Why: Serilog writes files there; creating it prevents write failures at startup.

3) Configure Serilog
- Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(...)
    .WriteTo.File(path: Path.Combine(logDirectory, ".log"), ...)
    .CreateLogger();
  - Purpose: set up a global logger with levels, enrichers and sinks (console + rolling file).
  - Why: central structured logging; override Microsoft noise; enrich with machine/thread context to aid diagnostics.
  - Important options:
    - MinimumLevel.Override("Microsoft", ...) reduces noisy framework logs.
    - WriteTo.File uses daily rolling and retention for logs.

4) Top-level try/catch/finally
- try { ... app.RunAsync(); } catch (Exception ex) { Log.Fatal(...) } finally { Log.CloseAndFlushAsync(); }
  - Purpose: ensure errors during startup are logged fatally and the logger is flushed on exit.
  - Why: guarantees you capture startup failures and cleanly close file handles.

5) Builder creation and Serilog integration
- var builder = WebApplication.CreateBuilder(args);
- builder.Host.UseSerilog();
  - Purpose: create the host builder and replace default logging with Serilog.
  - Why: ensures all host logs go through the configured Serilog sinks.

6) Configuration sources
- builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
- builder.Configuration.AddEnvironmentVariables();
  - Purpose: load appsettings.json (optional) and environment variables into IConfiguration.
  - Why: runtime config override and hot reload of JSON if changed.

7) Services registration
- builder.Services.AddSingleton<PluginEndpointDataSource>();
  - Purpose: register a singleton data source that the plugin manager will add endpoints to.
  - Why: central place for exposing plugin endpoints to routing and OpenAPI builder.

- builder.Services.AddEndpointsApiExplorer();
  - Purpose: enables minimal API endpoint metadata discovery.
  - Why: Swashbuckle/other tools may use it.

- builder.Services.AddHealthChecks().AddCheck<PluginHealthCheck>("plugins");
  - Purpose: register health checks and a plugin-specific check.
  - Why: expose /health endpoints for liveness/readiness.

- CORS and Response Compression service registrations:
  - AddCors(...) sets the default policy using AllowedOrigins from configuration.
  - AddResponseCompression(...) enables compression for responses.
  - Why: standard production concerns (CORS and bandwidth optimization).

8) Build app and get logger
- var app = builder.Build();
- var logger = app.Services.GetRequiredService<ILogger<Program>>();
  - Purpose: compile the configured app pipeline and retrieve an ILogger for Program.
  - Why: create host and resolve services for runtime use.

9) Exception handling middleware
- if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();
  else app.UseExceptionHandler(errorApp => { errorApp.Run(async context => { ... }) }); app.UseHsts();
  - Purpose: different error handling behavior for dev vs production.
  - Why: developer page gives rich errors in dev; production returns sanitized JSON and enforces HSTS.

10) Security headers middleware
- app.Use(async (context, next) => { context.Response.Headers["X-Content-Type-Options"] = "nosniff"; ... await next(); });
  - Purpose: add common HTTP security headers to every response.
  - Why: protects against content sniffing, clickjacking, XSS and enforces referrer policy.

11) app.UseHttpsRedirection(); app.UseResponseCompression(); app.UseCors();
  - Purpose: add essential middleware (redirect to HTTPS, enable compression and CORS).
  - Why: standard production best practices.

12) Retrieve PluginEndpointDataSource
- var dataSource = app.Services.GetRequiredService<PluginEndpointDataSource>();
  - Purpose: access the endpoint collection the plugin manager will populate.
  - Why: used by endpoints like /_plugins and the dynamic OpenAPI builder.

13) Health checks mapping
- app.MapHealthChecks("/health");
- app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready")});
  - Purpose: expose liveness and readiness endpoints.
  - Why: orchestration and monitoring systems rely on these endpoints.

14) Dynamic OpenAPI endpoint
- app.MapGet("/openapi/v1.json", (PluginEndpointDataSource ds, ILogger<Program> log) => { try { var doc = DynamicOpenApi.Build(ds); ... return Results.Bytes(...); } catch (...) { log.LogError(...); return Results.Problem(...); } })
    .WithTags("System")
    .Produces(200, contentType: "application/json")
    .Produces(500);
  - Purpose: build an OpenAPI (Swagger) JSON document at runtime containing only the plugin endpoints discovered in the PluginEndpointDataSource.
  - Why: plugin routes are dynamic; we need to generate API spec at runtime to feed UI/tools.
  - Implementation notes:
    - Build(ds) inspects endpoints and composes an OpenApiDocument.
    - The document is serialized to JSON bytes and returned as application/json.
    - Errors are caught and result in 500 with a logged error.

15) Scalar UI mapping
- app.MapScalarApiReference(opts => { opts.Title = "Dynamic Plugin API"; opts.WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient); });
  - Purpose: map Scalar UI (from Scalar.AspNetCore) to reference the dynamic spec.
  - Why: provides a developer UI that can use the dynamic OpenAPI spec.

16) Static endpoints (/ and /_plugins)
- app.MapGet("/", () => new { service = "WebHost", ... }).WithTags("System").ExcludeFromDescription();
  - Purpose: return a simple JSON status payload for root.
  - Why: quick health/info page; excluded from plugin-only OpenAPI.

- app.MapGet("/_plugins", (PluginEndpointDataSource ds) => Results.Ok(new { count = ds.Endpoints.Count, plugins = ds.Endpoints.Where(...).Select(...) }))
  - Purpose: return runtime data about loaded plugin endpoints.
  - Why: debugging and operational visibility.

17) PluginManager initialization and start
- var pluginsDir = builder.Configuration["PluginsDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "Plugins");
- var gracePeriodSeconds = builder.Configuration.GetValue("PluginManager:GracePeriodSeconds", 30);
- var manager = new WebHost.PluginManager(pluginsDir, dataSource, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<PluginManager>(), gracePeriodSeconds);
- manager.Start();
- Logging success/failure around Start.
  - Purpose: instantiate and start the plugin manager which:
    - watches the Plugins folder,
    - loads plugin assemblies,
    - registers their endpoints into the PluginEndpointDataSource,
    - supports hot-swap with a grace period.
  - Why: dynamic loading of plugin DLLs is the main feature of this host.

18) Graceful shutdown registration
- var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
- lifetime.ApplicationStopping.Register(() => { Log.Information("... disposing plugin manager"); manager.Dispose(); });
- lifetime.ApplicationStopped.Register(() => { Log.Information("✅ Application stopped gracefully"); });
  - Purpose: ensure plugin manager is disposed when the host begins shutting down.
  - Why: cleanly unload plugin contexts and release resources.

19) Final startup logs and app.RunAsync()
- Log.Information("✅ WebHost configured successfully"); await app.RunAsync();
  - Purpose: final start log and run the web server loop until signaled to stop.
  - Why: runs Kestrel/host and serves requests.

---

## DynamicOpenApi.cs — intent & key methods

File intent: inspect the runtime RouteEndpoints exposed by PluginEndpointDataSource and produce an OpenApiDocument (OpenAPI v3) containing only plugin routes, path parameters and HTTP methods.

Primary methods:

1) public static OpenApiDocument Build(PluginEndpointDataSource ds)
- Iterates over ds.Endpoints
- Filters endpoints by DisplayName starting with "Plugin:" (case-insensitive)
- For each RouteEndpoint:
  - Converts the RoutePattern to an OpenAPI path (PatternToOpenApiPath)
  - Ensures an OpenApiPathItem exists for that path
  - Extracts allowed HTTP methods via IHttpMethodMetadata in endpoint.Metadata
  - For each method:
    - Creates OpenApiOperation with Summary = routeEp.DisplayName and Tag derived from ExtractPluginTag(...)
    - Adds a default 200 response
    - For each route parameter in pattern.Parameters, creates a path OpenApiParameter with inferred schema type via InferParamType(...)
    - Maps HTTP method string to OperationType enum and registers the operation under the path item
- Returns the assembled OpenApiDocument with Info, Paths and Components.

Why: plugins can add arbitrary routes at runtime. The host cannot know the API surface at compile time, so it builds an OpenAPI spec dynamically to enable docs and UI.

2) PatternToOpenApiPath(RoutePattern pattern)
- Converts ASP.NET RoutePattern parts into an OpenAPI path string.
  - Literal parts -> literal content
  - Parameter parts -> {name}
- Joins segments with '/'
- Ensures leading '/' and handles root case.
- Why: OpenAPI expects brace syntax for path parameters; ASP.NET route patterns need conversion.

3) InferParamType(RoutePatternParameterPart p)
- Checks parameter policies (e.g., "int", "bool") and returns OpenAPI primitive types ("integer", "boolean", "string").
- Why: OpenAPI parameter schema needs a primitive type; inferring from route constraints improves accuracy.

4) ExtractPluginTag(string? displayName)
- If displayName null/empty -> "plugin"
- Else, finds ':' and takes the part after it; trims leading/trailing slashes; splits on '/' and returns the first segment.
- Why: group plugin endpoints in the OpenAPI UI by plugin name or first path segment, making docs clearer.

---

## PluginManager.cs — intent & detailed explanation

File intent: manage the lifecycle of plugin DLLs loaded from a directory. Responsibilities include:
- Watching a folder for .dll file changes (created, modified, renamed, deleted).
- Loading plugin assemblies into isolated AssemblyLoadContexts (PluginLoadContext).
- Registering plugin endpoints with the PluginEndpointDataSource.
- Supporting hot-swap: when a plugin is updated, keep the old version alive for a grace period (to finish in-flight requests) then dispose it.
- Graceful unload and disposal of all plugins and resources when the manager is disposed.

---

### Class structure and fields

```csharp
public sealed class PluginManager : IDisposable
```
- sealed: prevents inheritance.
- IDisposable: ensures cleanup can be called explicitly or via using/lifetime events.

Fields:

1) `private readonly string _pluginsDir;`
   - Purpose: path to the directory containing plugin DLLs.
   - Why: all file operations and FileSystemWatcher point here.

2) `private readonly PluginEndpointDataSource _dataSource;`
   - Purpose: the shared endpoint collection where plugin routes are registered.
   - Why: the routing system reads from this data source; plugins add endpoints via Register(_dataSource).

3) `private readonly ILogger<PluginManager> _logger;`
   - Purpose: structured logging for plugin manager operations.
   - Why: logs loading, unloading, errors and debug info with context.

4) `private readonly int _gracePeriodSeconds;`
   - Purpose: time (in seconds) to wait before disposing an old plugin version after hot-swap.
   - Why: allows in-flight HTTP requests to complete using the old plugin before unloading it.

5) `private FileSystemWatcher? _watcher;`
   - Purpose: monitors the plugins directory for file system events (created, changed, renamed, deleted).
   - Why: enables hot-swap by reacting to DLL changes without restarting the host.

6) `private readonly object _gate = new();`
   - Purpose: lock object for synchronizing access to _loaded dictionary.
   - Why: multiple threads (file watcher events, debounce tasks) may try to modify _loaded simultaneously.

7) `private readonly Dictionary<string, PluginHandle> _loaded = new(StringComparer.OrdinalIgnoreCase);`
   - Purpose: stores currently loaded plugins keyed by normalized file path.
   - Why: track which plugins are active and access their PluginLoadContext and IEndpointModule for unload/hot-swap.

8) `private readonly ConcurrentDictionary<string, CancellationTokenSource> _debouncers = new(StringComparer.OrdinalIgnoreCase);`
   - Purpose: debounce reload operations per file path.
   - Why: file system events can fire multiple times rapidly for one change; debouncing avoids reloading the same plugin repeatedly.

9) `private readonly List<(DateTime UnloadTime, List<IDisposable> Plugins)> _pendingUnload = new();`
   - Purpose: queue of old plugin instances scheduled for disposal after a grace period.
   - Why: hot-swap keeps old versions alive for grace period; this list tracks when each should be disposed.

10) `private readonly object _unloadLock = new();`
    - Purpose: lock for synchronizing access to _pendingUnload list.
    - Why: ProcessPendingUnloads and hot-swap operations may run concurrently.

11) `private volatile bool _disposed;`
    - Purpose: flag indicating the manager has been disposed.
    - Why: prevents operations after disposal and ensures thread-safe disposal check.

12) `private record PluginHandle(string Path, PluginLoadContext Ctx, IEndpointModule Module);`
    - Purpose: immutable record holding plugin state: file path, load context and module instance.
    - Why: bundles related data for each loaded plugin in one structure.

---

### Constructor

```csharp
public PluginManager(string pluginsDir, PluginEndpointDataSource dataSource, ILogger<PluginManager> logger, int gracePeriodSeconds = 30)
{
    _pluginsDir = pluginsDir ?? throw new ArgumentNullException(nameof(pluginsDir));
    _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _gracePeriodSeconds = gracePeriodSeconds;
}
```
- Parameters: directory path, endpoint data source, logger and grace period (default 30s).
- Validation: throws if any required parameter is null.
- Why: initialize state before Start() is called.

---

### LoadedPlugins property

```csharp
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
```
- Purpose: expose currently loaded plugin names and file names as read-only dictionary.
- Lock: ensures thread-safe read of _loaded.
- Why: allows external code (like a /_plugins endpoint) to query loaded plugins without internal access.

---

### Start() method

```csharp
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
        UnloadImmediate(e.OldFullPath);
        DebounceReload(e.FullPath);
    };
    _watcher.Deleted += (_, e) =>
    {
        _logger.LogInformation("🗑️ Plugin file deleted: {FileName}", Path.GetFileName(e.FullPath));
        UnloadImmediate(e.FullPath);
    };
    _watcher.Error += (_, e) =>
    {
        _logger.LogError(e.GetException(), "⚠️ File system watcher encountered an error");
    };
    _watcher.EnableRaisingEvents = true;

    _logger.LogInformation("✅ Plugin Manager started successfully (Hot-Swap Mode)");
    _logger.LogInformation("    • Grace Period: {GracePeriodSeconds}s", _gracePeriodSeconds);
    _logger.LogInformation("    • Watch Directory: {PluginsDirectory}", _pluginsDir);
}
```

Purpose: initialize the plugin manager and start watching for file changes.

Steps:
1. Check if disposed; throw if so.
2. Create plugins directory if missing (ensures it exists).
3. Enumerate existing .dll files and call DebounceReload for each (initial load).
4. Create FileSystemWatcher for the directory filtering *.dll files:
   - NotifyFilter: react to file name, size and last write time changes.
   - Event handlers:
     - Created: log and debounce reload when a new DLL appears.
     - Changed: log and debounce reload when a DLL is modified.
     - Renamed: log, unload the old path immediately and debounce reload the new path.
     - Deleted: log and unload immediately.
     - Error: log if the watcher encounters an error (e.g., directory deleted).
5. EnableRaisingEvents = true: start monitoring.
6. Log success with grace period and directory info.

Why: sets up reactive hot-swap by monitoring file system changes and loading plugins on demand.

---

### DebounceReload(string path)

```csharp
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
```

Purpose: debounce rapid file system events for the same file by cancelling prior pending reloads and scheduling a new one after a delay (250ms).

Steps:
1. Return immediately if disposed.
2. Normalize path to a full path key.
3. Use AddOrUpdate on _debouncers:
   - If key doesn't exist, create a new CancellationTokenSource.
   - If key exists, cancel and dispose the old CTS and create a new one.
4. Start a background task (Task.Run):
   - Await 250ms delay with the cancellation token.
   - If not cancelled, call Reload(path).
   - Catch OperationCanceledException (expected if a newer event replaces this one).
   - Catch other exceptions and log them.
   - Finally: remove the CTS from _debouncers if it's still the current one; dispose CTS.

Why: file system events can fire multiple times for a single change (e.g., Changed fires twice); debouncing ensures we reload only once after changes settle. 250ms is usually enough to coalesce events.

---

### Reload(string path)

```csharp
private void Reload(string path)
{
    if (!File.Exists(path) || _disposed) return;

    lock (_gate)
    {
        var key = Normalize(path);
        
        if (_loaded.TryGetValue(key, out var oldHandle))
        {
            _logger.LogInformation("🔄 Hot-swapping plugin: {PluginName} v{Version}", 
                oldHandle.Module.Name,
                oldHandle.Module.GetType().Assembly.GetName().Version?.ToString() ?? "unknown");
            
            lock (_unloadLock)
            {
                var unloadTime = DateTime.UtcNow.AddSeconds(_gracePeriodSeconds);
                _pendingUnload.Add((unloadTime, new List<IDisposable> { oldHandle.Module }));
                _logger.LogInformation("    ⏳ Old version scheduled for disposal at {UnloadTime:HH:mm:ss} (grace period: {Seconds}s)",
                    unloadTime, _gracePeriodSeconds);
            }
            
            _loaded.Remove(key);
            _dataSource.RemovePlugin(oldHandle.Module.Name);
        }

        TryLoad(path);
        ProcessPendingUnloads();
    }
}
```

Purpose: reload a plugin file by:
- If already loaded, schedule the old version for delayed disposal (hot-swap).
- Remove old endpoints from the data source.
- Load the new version.
- Process any pending unloads whose grace period has expired.

Steps:
1. Check if file exists and manager not disposed; return if not.
2. Lock _gate for thread-safe access to _loaded.
3. Normalize path to key.
4. If plugin already loaded:
   - Log hot-swap with plugin name and version.
   - Lock _unloadLock and add the old module to _pendingUnload with unloadTime = now + grace period.
   - Remove from _loaded and from _dataSource.
5. Call TryLoad(path) to load the new version.
6. Call ProcessPendingUnloads() to dispose old versions whose time has come.

Why: hot-swap strategy: keep old plugin alive for grace period to allow in-flight requests to finish, then dispose. New plugin is loaded immediately and serves new requests.

---

### TryLoad(string path)

```csharp
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
```

Purpose: load a single plugin DLL into an isolated AssemblyLoadContext, find an IEndpointModule implementation, instantiate it, register its endpoints and add it to _loaded.

Steps:
1. Create a PluginLoadContext for the plugin directory.
2. Retry loop (up to 5 attempts) to handle file locking:
   - Open the file with FileShare.ReadWrite | FileShare.Delete (allows concurrent access and deletion by other processes).
   - Load the assembly from the stream into the context.
   - Scan types for the first IEndpointModule implementation (must be a non-abstract class).
   - If not found, log warning, unload context and return.
   - If found, create an instance via Activator.CreateInstance.
   - Call module.Register(_dataSource) to let the plugin add its endpoints.
   - Store the plugin in _loaded with a PluginHandle record.
   - Log success with plugin name, version and file name.
   - Return successfully.
   - If IOException (file locked), retry after 100ms delay (up to 5 times).
3. If all retries fail, log an error.
4. Catch ReflectionTypeLoadException separately:
   - Extract and log distinct loader exception messages (helps diagnose missing dependencies).
   - Unload context.
5. Catch other exceptions:
   - Log error.
   - Unload context.

Why: file locking is common during hot-swap (build tools may lock the file briefly); retries work around transient locks. Isolated AssemblyLoadContext allows unloading and avoids conflicts. Reflection is used to discover and instantiate the plugin type.

---

### UnloadImmediate(string path)

```csharp
private void UnloadImmediate(string path)
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
```

Purpose: immediately unload a plugin (used when a file is deleted or renamed).

Steps:
1. Normalize path to key.
2. Attempt to remove the plugin from _loaded.
3. If found:
   - Log unload start.
   - Remove plugin endpoints from _dataSource.
   - Dispose the module (IDisposable).
   - Unload the AssemblyLoadContext.
   - Log success.
   - Catch exceptions and log warnings (continue even if disposal fails).
4. Force GC to collect and finalize to help unload the assembly.

Why: explicit unload ensures resources are released and endpoints are removed immediately (no grace period because the file is gone).

---

### ProcessPendingUnloads()

```csharp
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
```

Purpose: dispose old plugin instances whose grace period has expired.

Steps:
1. Lock _unloadLock for thread-safe access to _pendingUnload.
2. Get current UTC time.
3. Filter _pendingUnload to entries where UnloadTime <= now.
4. If any found, log debug message with count.
5. For each entry:
   - For each plugin (IDisposable) in the list:
     - Try to dispose; log debug on success.
     - Catch exceptions and log warnings (continue even if one fails).
6. Remove all processed entries from _pendingUnload.

Why: called during Reload to clean up old plugin versions after their grace period. Ensures delayed disposal happens eventually and doesn't accumulate indefinitely.

---

### Normalize(string p)

```csharp
private static string Normalize(string p) => Path.GetFullPath(p);
```

Purpose: convert a file path to a full absolute path.

Why: ensures all path comparisons use the same format (relative vs absolute paths could otherwise cause mismatches).

---

### Dispose()

```csharp
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
```

Purpose: clean up all resources when the manager is disposed (typically on application shutdown).

Steps:
1. Check if already disposed; return if so (idempotent).
2. Set _disposed = true.
3. Log disposal start.
4. Dispose the FileSystemWatcher (stops monitoring).
5. Lock _gate:
   - If any plugins loaded, log count.
   - For each loaded plugin:
     - Remove endpoints from _dataSource.
     - Dispose the module.
     - Unload the context.
     - Log debug per plugin.
     - Catch exceptions and log warnings (continue even if one fails).
   - Clear _loaded dictionary.
6. Lock _unloadLock:
   - If any pending unloads, log count.
   - For each pending entry:
     - For each plugin, try to dispose (ignore exceptions during shutdown).
   - Clear _pendingUnload list.
7. Force GC to collect and finalize.
8. Log disposal success.

Why: ensures all plugins are unloaded, contexts are freed and file watcher is stopped. Graceful shutdown behavior that prevents resource leaks.

---

### Summary of hot-swap flow

1. File is modified → FileSystemWatcher fires Changed event.
2. DebounceReload is called → cancels prior pending reloads for the same file and schedules a new one after 250ms.
3. After delay, Reload is called:
   - If plugin already loaded, move old module to _pendingUnload with unloadTime = now + grace period.
   - Remove old endpoints from _dataSource.
   - TryLoad loads the new version into a fresh AssemblyLoadContext.
   - New plugin registers its endpoints via module.Register(_dataSource).
   - ProcessPendingUnloads disposes old plugins whose grace period has expired.
4. New requests hit the new plugin; old in-flight requests continue using the old plugin until grace period elapses.
5. After grace period, old plugin is disposed and context is unloaded.

Why: zero-downtime hot-swap without restarting the host. In-flight requests are safe because the old plugin remains loaded temporarily.

---

## Additional notes on PluginManager

- Thread safety: _gate lock protects _loaded; _unloadLock protects _pendingUnload; _debouncers is ConcurrentDictionary (thread-safe).
- FileSystemWatcher events are raised on background threads; locks ensure safe access to shared state.
- GC.Collect() calls help force assembly unloading (AssemblyLoadContext.Unload is cooperative; GC must actually collect).
- PluginLoadContext is assumed to be a custom AssemblyLoadContext (not shown) that enables isolated loading and unloading of plugin assemblies.
- Grace period strategy: keeps old plugin alive for N seconds (default 30) to allow time for active HTTP requests to finish before disposing.

---

If you want a fully "annotated" Program.cs file where every single source line is commented inline, I can produce that as a separate file (Program.cs.annotated.cs) showing the original lines with inline comments. This file currently adds a concise explanation and maps the design. Request the annotated version and I will add it.
