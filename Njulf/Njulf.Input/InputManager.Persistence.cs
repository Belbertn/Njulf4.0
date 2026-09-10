using System.Text.Json;
using System.Text.Json.Serialization;

namespace Njulf.Input;

public sealed partial class InputManager
{
    private sealed record BindingFile([property: JsonRequired] int Version, [property: JsonRequired] Dictionary<string, JsonElement> Actions);
    private sealed record ActionBindings([property: JsonRequired] BindingShape Type, [property: JsonRequired] InputValueMode Mode,
        [property: JsonRequired] BindingSpec[] Bindings);
    private static readonly JsonSerializerOptions BindingJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <inheritdoc />
    public void SaveBindings(string path)
    {
        EnsureUsable(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var actions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, state) in _actions)
        {
            var bindings = Enumerable.Range(0, state.Count).Select(state.GetSpec).ToArray();
            actions.Add(name, JsonSerializer.SerializeToElement(new ActionBindings(state.Shape, state.Mode, bindings), BindingJson));
        }
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new BindingFile(1, actions), BindingJson);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <inheritdoc />
    public bool LoadBindings(string path)
    {
        EnsureUsable(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_updating || _rebind != null) throw new InvalidOperationException("Load bindings outside input callbacks and capture sessions.");
        byte[] payload;
        try { payload = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        var file = JsonSerializer.Deserialize<BindingFile>(payload, BindingJson);
        if (file == null || file.Version != 1 || file.Actions == null) throw new InvalidDataException("Unsupported or invalid input binding file.");
        var apply = new List<Action>();
        foreach (var (name, data) in file.Actions)
        {
            if (!_actions.TryGetValue(name, out var state)) continue;
            var bindings = data.Deserialize<ActionBindings>(BindingJson);
            if (bindings == null || bindings.Type != state.Shape || bindings.Mode != state.Mode || bindings.Bindings == null || bindings.Bindings.Any(b => b == null))
                throw new InvalidDataException($"Bindings for '{name}' do not match its registered action.");
            apply.Add(state.PrepareBindings(bindings.Bindings));
        }
        foreach (var commit in apply) commit();
        return true;
    }
}
