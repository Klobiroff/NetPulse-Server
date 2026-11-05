using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetPulse.Plugins;
using NetPulse.Server.Configuration;

namespace NetPulse.Server.Plugins;

public sealed class PluginManifest
{
    public string Name { get; set; } = string.Empty;

    public string Assembly { get; set; } = string.Empty;

    public string Factory { get; set; } = string.Empty;

    public string? Auth { get; set; }
}

public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "NetPulse.Plugins",
        "NetPulse.Common",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    };

    private readonly string _folder;

    public PluginLoadContext(string name, string folder)
        : base(name, isCollectible: true) => _folder = folder;

    public Assembly LoadMainAssembly(string path) => LoadFromBytes(path);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || SharedAssemblies.Contains(assemblyName.Name))
        {
            return null;
        }

        var path = Path.Combine(_folder, assemblyName.Name + ".dll");
        return File.Exists(path) ? LoadFromBytes(path) : null;
    }

    private Assembly LoadFromBytes(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        var pdb = Path.ChangeExtension(path, ".pdb");

        if (!File.Exists(pdb))
        {
            return LoadFromStream(stream);
        }

        using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
        return LoadFromStream(stream, symbols);
    }
}

public sealed class LoadedPlugin
{
    private readonly object _gate = new();
    private int _leases;
    private bool _superseded;
    private bool _unloaded;

    public LoadedPlugin(string name, string folder, int version, PluginLoadContext? context, IPluginFactory? factory, IAuthPlugin? auth)
    {
        Name = name;
        Folder = folder;
        Version = version;
        Context = context;
        Factory = factory;
        Auth = auth;
    }

    public string Name { get; }

    public string Folder { get; }

    public int Version { get; }

    public PluginLoadContext? Context { get; }

    public IPluginFactory? Factory { get; }

    public IAuthPlugin? Auth { get; }

    public int ActiveLeases
    {
        get
        {
            lock (_gate)
            {
                return _leases;
            }
        }
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_unloaded)
            {
                return false;
            }

            _leases++;
            return true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _leases--;
            UnloadIfIdle();
        }
    }

    public void Supersede()
    {
        lock (_gate)
        {
            _superseded = true;
            UnloadIfIdle();
        }
    }

    private void UnloadIfIdle()
    {
        if (!_superseded || _leases > 0 || _unloaded)
        {
            return;
        }

        _unloaded = true;
        Context?.Unload();
    }
}

public sealed class PluginManager : IDisposable
{
    private readonly PluginsOptions _options;
    private readonly ILogger<PluginManager> _logger;
    private readonly string _directory;
    private readonly AuthPluginContext _authContext;
    private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timer> _debounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _loadGate = new();
    private FileSystemWatcher? _watcher;
    private int _version;

    public PluginManager(IOptions<NetPulseOptions> options, ILogger<PluginManager> logger, IHttpClientFactory httpFactory)
    {
        _options = options.Value.Plugins;
        _logger = logger;
        _authContext = new AuthPluginContext(logger, httpFactory);
        _directory = Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(AppContext.BaseDirectory, _options.Directory);
    }

    public string Directory => _directory;

    public IReadOnlyCollection<LoadedPlugin> Plugins => _plugins.Values.ToArray();

    public IAuthPlugin? AuthPlugin { get; set; }

    public string DefaultPluginName => _options.Default;

    public int HookTimeoutMs => Math.Max(1, _options.HookTimeoutMs);

    public int AuthTimeoutMs => Math.Max(1, _options.AuthTimeoutMs);

    public void Start()
    {
        System.IO.Directory.CreateDirectory(_directory);

        foreach (var folder in System.IO.Directory.GetDirectories(_directory))
        {
            Reload(Path.GetFileName(folder));
        }

        if (!string.IsNullOrWhiteSpace(_options.Auth) && _plugins.TryGetValue(_options.Auth, out var authHost))
        {
            AuthPlugin ??= authHost.Auth;
        }

        if (!string.IsNullOrWhiteSpace(_options.Auth) && AuthPlugin is null)
        {
            throw new InvalidOperationException($"Plugins:Auth is set to '{_options.Auth}' but no authentication plugin could be loaded; refusing to start without authentication");
        }

        if (_options.HotReload)
        {
            StartWatching();
        }
    }

    public void Register(string name, IPluginFactory factory, IAuthPlugin? auth = null) =>
        _plugins[name] = new LoadedPlugin(name, string.Empty, Interlocked.Increment(ref _version), null, factory, auth);

    public LoadedPlugin? Lease(string name)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!_plugins.TryGetValue(name, out var plugin))
            {
                return null;
            }

            if (plugin.TryAcquire())
            {
                return plugin;
            }
        }

        return null;
    }

    public IReadOnlyDictionary<string, string> GetConfig(string pluginName) =>
        _options.Settings.TryGetValue(pluginName, out var settings)
            ? settings
            : new Dictionary<string, string>();

    public bool Reload(string folderName)
    {
        lock (_loadGate)
        {
            var folder = Path.Combine(_directory, folderName);

            if (!System.IO.Directory.Exists(folder))
            {
                RemoveFolder(folderName);
                return false;
            }

            try
            {
                var loaded = Load(folder);

                if (loaded is null)
                {
                    return false;
                }

                if (_plugins.TryGetValue(loaded.Name, out var old))
                {
                    _plugins[loaded.Name] = loaded;
                    old.Supersede();
                }
                else
                {
                    _plugins[loaded.Name] = loaded;
                }

                if (!string.IsNullOrWhiteSpace(_options.Auth) && string.Equals(_options.Auth, loaded.Name, StringComparison.OrdinalIgnoreCase))
                {
                    AuthPlugin = loaded.Auth;
                }

                _logger.LogInformation("Loaded plugin {Plugin} v{Version} from {Folder}", loaded.Name, loaded.Version, folderName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {Folder}; keeping previous version if any", folderName);
                return false;
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();

        foreach (var timer in _debounce.Values)
        {
            timer.Dispose();
        }

        foreach (var plugin in _plugins.Values)
        {
            plugin.Supersede();
        }
    }

    private LoadedPlugin? Load(string folder)
    {
        var manifestPath = Path.Combine(folder, "plugin.json");

        if (!File.Exists(manifestPath))
        {
            _logger.LogDebug("Skipping {Folder}: no plugin.json", folder);
            return null;
        }

        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidOperationException("Empty plugin.json");

        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Assembly) || (string.IsNullOrWhiteSpace(manifest.Factory) && string.IsNullOrWhiteSpace(manifest.Auth)))
        {
            throw new InvalidOperationException("plugin.json requires name, assembly and a factory or auth type");
        }

        var version = Interlocked.Increment(ref _version);
        var context = new PluginLoadContext($"{manifest.Name}#{version}", folder);

        try
        {
            var assembly = context.LoadMainAssembly(Path.Combine(folder, manifest.Assembly));
            IPluginFactory? factory = null;

            if (!string.IsNullOrWhiteSpace(manifest.Factory))
            {
                var factoryType = assembly.GetType(manifest.Factory, throwOnError: true)!;
                factory = Activator.CreateInstance(factoryType) as IPluginFactory
                    ?? throw new InvalidOperationException($"{manifest.Factory} does not implement IPluginFactory");
            }

            var auth = CreateAuth(assembly, manifest, manifest.Name);
            return new LoadedPlugin(manifest.Name, folder, version, context, factory, auth);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private IAuthPlugin? CreateAuth(Assembly assembly, PluginManifest manifest, string pluginName)
    {
        if (string.IsNullOrWhiteSpace(manifest.Auth) || !string.Equals(_options.Auth, pluginName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var type = assembly.GetType(manifest.Auth, throwOnError: true)!;
        var config = GetConfig(pluginName);
        var withContext = type.GetConstructor(new[] { typeof(IReadOnlyDictionary<string, string>), typeof(IAuthPluginContext) });
        var withConfig = type.GetConstructor(new[] { typeof(IReadOnlyDictionary<string, string>) });
        var instance = withContext is not null
            ? withContext.Invoke(new object[] { config, _authContext })
            : withConfig is not null ? withConfig.Invoke(new object[] { config }) : Activator.CreateInstance(type);
        return instance as IAuthPlugin ?? throw new InvalidOperationException($"{manifest.Auth} does not implement IAuthPlugin");
    }

    private void RemoveFolder(string folderName)
    {
        var match = _plugins.Values.FirstOrDefault(p => string.Equals(Path.GetFileName(p.Folder), folderName, StringComparison.OrdinalIgnoreCase));

        if (match is not null && _plugins.TryRemove(match.Name, out var removed))
        {
            removed.Supersede();
            _logger.LogInformation("Unloaded plugin {Plugin}", removed.Name);
        }
    }

    private void StartWatching()
    {
        _watcher = new FileSystemWatcher(_directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime
        };

        _watcher.Changed += (_, e) => Schedule(e.FullPath);
        _watcher.Created += (_, e) => Schedule(e.FullPath);
        _watcher.Deleted += (_, e) => Schedule(e.FullPath);
        _watcher.Renamed += (_, e) => Schedule(e.FullPath);
        _watcher.EnableRaisingEvents = true;
    }

    private void Schedule(string fullPath)
    {
        var relative = Path.GetRelativePath(_directory, fullPath);
        var folderName = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (string.IsNullOrEmpty(folderName))
        {
            return;
        }

        var timer = _debounce.GetOrAdd(folderName, name => new Timer(_ => Reload(name), null, Timeout.Infinite, Timeout.Infinite));
        timer.Change(500, Timeout.Infinite);
    }
}
