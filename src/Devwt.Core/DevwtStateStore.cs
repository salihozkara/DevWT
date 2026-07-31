using System.Text.Json;

namespace Devwt.Core;

public sealed class DevwtStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public DevwtStateStore(string? stateRoot = null)
    {
        StateRoot = DevwtStateDefaults.ResolveStateRoot(stateRoot);
    }

    public string StateRoot { get; }

    private string RepositoriesPath => Path.Combine(StateRoot, "repos.json");

    private string ContextsPath => Path.Combine(StateRoot, "contexts.json");

    private string RoutingPath => Path.Combine(StateRoot, "routing.json");

    private string RuntimeSettingsPath => Path.Combine(StateRoot, "runtime.json");

    public DevwtRepositoryState LoadRepositories() =>
        Load(RepositoriesPath, DevwtRepositoryState.Empty);

    public DevwtContextState LoadContexts() =>
        DevwtPortShift.Normalize(Load(ContextsPath, DevwtContextState.Empty));

    public DevwtRoutingState LoadRouting() =>
        DevwtRoutingState.Normalize(Load(RoutingPath, DevwtRoutingState.Empty));

    public DevwtRuntimeSettings LoadRuntimeSettings() =>
        Load(RuntimeSettingsPath, DevwtRuntimeSettings.Empty);

    public void SaveRepositories(DevwtRepositoryState state) =>
        Save(RepositoriesPath, state);

    public void SaveContexts(DevwtContextState state) =>
        Save(ContextsPath, state);

    public void SaveRouting(DevwtRoutingState state) =>
        Save(RoutingPath, DevwtRoutingState.Normalize(state));

    public void SaveRuntimeSettings(DevwtRuntimeSettings state) =>
        Save(RuntimeSettingsPath, state);

    private static T Load<T>(string path, T empty)
    {
        if (!File.Exists(path))
        {
            return empty;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? empty;
    }

    private static void Save<T>(string path, T state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
