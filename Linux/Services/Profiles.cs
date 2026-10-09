using System.Text.Json;

namespace FrameCastStudio.Linux.Services;

public sealed class SettingsProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool BuiltIn { get; set; }
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    public string Kind => BuiltIn ? "Intégré" : "Perso";
    public string Display => $"{Name}  ·  {Kind}";
    public override string ToString() => Display;
}

public static class ProfileCatalog
{
    /// <summary>Réglages portés par un profil. La clé de stream n'en fait volontairement pas partie.</summary>
    public static readonly string[] Keys =
    {
        "Fps", "ScalePercent", "Codec", "RateMode", "BitrateKbps", "Qp", "GopSeconds", "BFramesEnabled",
        "ColorSpace", "AudioBitrateKbps", "AudioSampleRate", "SelectedService",
    };

    private static SettingsProfile P(string name, string desc, params (string Key, object Value)[] values)
    {
        var p = new SettingsProfile { Name = name, Description = desc, BuiltIn = true };
        foreach (var (k, v) in values) p.Values[k] = JsonSerializer.SerializeToElement(v);
        return p;
    }

    public static List<SettingsProfile> BuiltIn() => new()
    {
        P("YouTube 1080p60", "Direct YouTube : 60 fps, H.264 CBR 9 000 kbps, image clé toutes les 2 s.",
            ("SelectedService", "YouTube"), ("Fps", 60), ("ScalePercent", 100), ("Codec", "H264"), ("RateMode", "CBR"),
            ("BitrateKbps", 9000), ("GopSeconds", 2), ("BFramesEnabled", false), ("AudioBitrateKbps", 128), ("AudioSampleRate", 48000), ("ColorSpace", "BT.709")),
        P("Twitch 1080p60", "Direct Twitch : 60 fps, H.264 CBR 6 000 kbps (plafond conseillé), audio 160 kbps.",
            ("SelectedService", "Twitch"), ("Fps", 60), ("ScalePercent", 100), ("Codec", "H264"), ("RateMode", "CBR"),
            ("BitrateKbps", 6000), ("GopSeconds", 2), ("BFramesEnabled", false), ("AudioBitrateKbps", 160), ("AudioSampleRate", 48000), ("ColorSpace", "BT.709")),
        P("Twitch léger 30 fps", "Pour une connexion moyenne : 30 fps, H.264 CBR 4 500 kbps.",
            ("SelectedService", "Twitch"), ("Fps", 30), ("ScalePercent", 100), ("Codec", "H264"), ("RateMode", "CBR"),
            ("BitrateKbps", 4500), ("GopSeconds", 2), ("BFramesEnabled", false), ("AudioBitrateKbps", 128), ("AudioSampleRate", 48000), ("ColorSpace", "BT.709")),
        P("Qualité max (enregistrement)", "60 fps, H.264 en qualité constante (QP 18), audio 192 kbps. Fichiers volumineux.",
            ("Fps", 60), ("ScalePercent", 100), ("Codec", "H264"), ("RateMode", "CQP"), ("Qp", 18d), ("GopSeconds", 2),
            ("BFramesEnabled", false), ("AudioBitrateKbps", 192), ("AudioSampleRate", 48000), ("ColorSpace", "BT.709")),
        P("Fichiers légers (enregistrement)", "30 fps, H.264 VBR 3 500 kbps, audio 128 kbps.",
            ("Fps", 30), ("ScalePercent", 100), ("Codec", "H264"), ("RateMode", "VBR"), ("BitrateKbps", 3500), ("GopSeconds", 2),
            ("BFramesEnabled", false), ("AudioBitrateKbps", 128), ("AudioSampleRate", 48000), ("ColorSpace", "BT.709")),
    };

    public static List<SettingsProfile> LoadCustom()
    {
        try
        {
            if (!File.Exists(Paths.ProfilesFile)) return new();
            var list = JsonSerializer.Deserialize<List<SettingsProfile>>(File.ReadAllText(Paths.ProfilesFile)) ?? new();
            foreach (var p in list) p.BuiltIn = false;
            return list.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        }
        catch (Exception ex) { Log.Error("Chargement des profils", ex); return new(); }
    }

    public static void SaveCustom(IEnumerable<SettingsProfile> all)
    {
        try
        {
            string tmp = Paths.ProfilesFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all.Where(p => !p.BuiltIn).ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Paths.ProfilesFile, true);
        }
        catch (Exception ex) { Log.Error("Sauvegarde des profils", ex); }
    }
}
