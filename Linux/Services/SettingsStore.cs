using System.Reflection;
using System.Text.Json;

namespace FrameCastStudio.Linux.Services;

/// <summary>Sauvegarde/chargement par réflexion d'une liste de propriétés d'un objet (fichier JSON dans ~/.config).</summary>
public sealed class SettingsStore
{
    private readonly object _target;
    private readonly string[] _names;
    private readonly HashSet<string> _set;
    private System.Threading.Timer? _timer;
    public bool Loading { get; private set; }

    public SettingsStore(object target, IEnumerable<string> names)
    {
        _target = target; _names = names.ToArray(); _set = new HashSet<string>(_names);
    }

    public void Load()
    {
        Loading = true;
        try
        {
            if (!File.Exists(Paths.SettingsFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.SettingsFile));
            var type = _target.GetType();
            foreach (var name in _names)
            {
                if (!doc.RootElement.TryGetProperty(name, out var el)) continue;
                var pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null || !pi.CanWrite) continue;
                try { pi.SetValue(_target, el.Deserialize(pi.PropertyType)); } catch { }
            }
            Log.Write("Réglages chargés");
        }
        catch (Exception ex) { Log.Error("Chargement des réglages", ex); }
        finally { Loading = false; }
    }

    public void Touch(string? name)
    {
        if (Loading || name == null || !_set.Contains(name)) return;
        _timer ??= new System.Threading.Timer(_ => SaveNow());
        _timer.Change(600, Timeout.Infinite);
    }

    public void SaveNow()
    {
        try
        {
            var dict = new Dictionary<string, object?>();
            var type = _target.GetType();
            foreach (var name in _names)
                dict[name] = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_target);
            string tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Paths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error("Sauvegarde des réglages", ex); }
    }
}
