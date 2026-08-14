using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace DragAndDropTexturing.Onion;

[Serializable]
public sealed class OnionModDdtSettings
{
    public bool Enabled { get; set; } = true;
    public string TargetBodyPart { get; set; } = "body";
    public Dictionary<string, int> GroupSelections { get; set; } = new();
}

public sealed class OnionModData
{
    private static readonly ConcurrentDictionary<string, object> SaveGates = new(StringComparer.OrdinalIgnoreCase);

    public const string MetaFileName = "meta.json";
    public const string SettingsFileName = "ddt_settings.json";

    public static readonly string[] AdoptableExtensions = new[]
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".tex", ".tga"
    };

    [JsonIgnore]
    public string DirectoryPath { get; set; } = "";

    public OnionOverlayMeta Meta { get; set; } = new();
    public OnionModDdtSettings Settings { get; set; } = new();

    public string Name => string.IsNullOrWhiteSpace(Meta.Name) ? Path.GetFileName(DirectoryPath) : Meta.Name;
    public bool Enabled => Settings.Enabled;

    private static object GetGate(string path)
    {
        string key = string.IsNullOrEmpty(path) ? "" : Path.GetFullPath(path);
        return SaveGates.GetOrAdd(key, _ => new object());
    }

    public void SaveMeta()
    {
        if (string.IsNullOrEmpty(DirectoryPath)) return;
        lock (GetGate(DirectoryPath))
        {
            Meta.FormatVersion = 2;
            File.WriteAllText(Path.Combine(DirectoryPath, MetaFileName),
                JsonConvert.SerializeObject(Meta, Formatting.Indented));
        }
    }

    public void SaveSettings()
    {
        if (string.IsNullOrEmpty(DirectoryPath)) return;
        lock (GetGate(DirectoryPath))
        {
            File.WriteAllText(Path.Combine(DirectoryPath, SettingsFileName),
                JsonConvert.SerializeObject(Settings, Formatting.Indented));
        }
    }

    public void Save() { SaveMeta(); SaveSettings(); }

    public bool TryRenameDirectoryToName(string rootLibraryDirectory, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrEmpty(DirectoryPath)) return false;
            string fullPath = Path.GetFullPath(DirectoryPath);
            string sanitized = OnionPackageImporter.Sanitize(Name);

            if (sanitized.Length == 0 || string.Equals(Path.GetFileName(fullPath), sanitized, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(rootLibraryDirectory), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string targetDir = Path.Combine(rootLibraryDirectory, sanitized);
            int n = 2;
            while (Directory.Exists(targetDir))
            {
                if (string.Equals(Path.GetFullPath(targetDir), fullPath, StringComparison.OrdinalIgnoreCase))
                    return false;
                targetDir = Path.Combine(rootLibraryDirectory, $"{sanitized} ({n++})");
            }

            lock (GetGate(DirectoryPath))
            {
                Directory.Move(fullPath, targetDir);
                DirectoryPath = targetDir;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public int DeleteUnreferencedLayerFiles(IEnumerable<string> candidates)
    {
        if (string.IsNullOrEmpty(DirectoryPath) || candidates == null) return 0;

        lock (GetGate(DirectoryPath))
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void CollectLayer(OnionOverlayLayer l)
            {
                if (!string.IsNullOrWhiteSpace(l?.File))
                    referenced.Add(Path.GetFullPath(Path.Combine(DirectoryPath, l.File.Replace('/', Path.DirectorySeparatorChar))));
            }

            foreach (var l in Meta.Layers) CollectLayer(l);
            foreach (var g in Meta.Groups)
                foreach (var o in g.Options)
                    foreach (var l in o.Layers) CollectLayer(l);

            int deleted = 0;
            foreach (var cand in candidates)
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                string abs = Path.IsPathRooted(cand) ? Path.GetFullPath(cand) : Path.GetFullPath(Path.Combine(DirectoryPath, cand));
                if (!abs.StartsWith(Path.GetFullPath(DirectoryPath), StringComparison.OrdinalIgnoreCase)) continue;

                if (!referenced.Contains(abs) && File.Exists(abs))
                {
                    try
                    {
                        File.Delete(abs);
                        deleted++;
                    }
                    catch { }
                }
            }
            return deleted;
        }
    }

    public static OnionModData? TryLoad(string directoryPath, out string error)
    {
        error = null;
        try
        {
            string metaPath = Path.Combine(directoryPath, MetaFileName);
            if (!File.Exists(metaPath))
            {
                error = "not an Onion mod (missing meta.json)";
                return null;
            }

            var meta = JsonConvert.DeserializeObject<OnionOverlayMeta>(File.ReadAllText(metaPath));
            if (meta == null || string.IsNullOrWhiteSpace(meta.Identifier))
            {
                error = "meta.json is damaged or missing Identifier";
                return null;
            }

            var mod = new OnionModData
            {
                DirectoryPath = directoryPath,
                Meta = meta
            };

            string settingsPath = Path.Combine(directoryPath, SettingsFileName);
            if (File.Exists(settingsPath))
            {
                var settings = JsonConvert.DeserializeObject<OnionModDdtSettings>(File.ReadAllText(settingsPath));
                if (settings != null) mod.Settings = settings;
            }

            foreach (var group in mod.Meta.Groups)
            {
                if (!mod.Settings.GroupSelections.ContainsKey(group.Name))
                    mod.Settings.GroupSelections[group.Name] = group.DefaultSettings;
            }

            return mod;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static OnionModData Load(string directoryPath)
    {
        return TryLoad(directoryPath, out _) ?? new OnionModData { DirectoryPath = directoryPath };
    }

    public string ResolveLayerPath(OnionOverlayLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer?.File)) return null;
        return Path.Combine(DirectoryPath, layer.File.Replace('/', Path.DirectorySeparatorChar));
    }
}
