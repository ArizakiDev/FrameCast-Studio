using System.Reflection;
using System.Text.Json;
using FrameCastStudio.Core;

namespace FrameCastStudio.App;

internal sealed class SettingsStore
{
    private readonly object _target;
    private readonly string[] _names;
    private readonly HashSet<string> _set;
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrameCastStudio", "settings.json");
    private System.Threading.Timer? _timer;
    public bool Loading { get; private set; }

    public SettingsStore(object target, IEnumerable<string> names)
    {
        _target = target; _names = names.ToArray(); _set = new HashSet<string>(_names);
    }

    private void MigrateLegacySettings()
    {
        try
        {
            if (File.Exists(_path)) return;
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenForge", "settings.json");
            if (!File.Exists(old)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.Copy(old, _path);
            Log.Write("Réglages importés depuis l'ancienne version (ScreenForge)");
        }
        catch (Exception ex) { Log.Error("Migration des réglages", ex); }
    }

    public void Load()
    {
        Loading = true;
        try
        {
            MigrateLegacySettings();
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var type = _target.GetType();
            foreach (var name in _names)
            {
                if (!doc.RootElement.TryGetProperty(name, out var el)) continue;
                var pi = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null || !pi.CanWrite) continue;
                try { pi.SetValue(_target, el.Deserialize(pi.PropertyType)); } catch { }
            }
            Log.Write("Réglages chargés", 1);
        }
        catch (Exception ex) { Log.Error("Chargement des réglages", ex); }
        finally { Loading = false; }
    }

    public void Touch(string? name)
    {
        if (Loading || name == null || !_set.Contains(name)) return;
        _timer ??= new System.Threading.Timer(_ => SaveNow());
        _timer.Change(500, System.Threading.Timeout.Infinite);
    }

    public void SaveNow()
    {
        try
        {
            var dict = new Dictionary<string, object?>();
            var type = _target.GetType();
            foreach (var name in _names)
                dict[name] = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(_target);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { Log.Error("Sauvegarde des réglages", ex); }
    }
}
