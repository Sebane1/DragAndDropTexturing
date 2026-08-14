using DragAndDropTexturing.Onion;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DragAndDropTexturing;

public sealed class OnionLayerModManager : IDisposable
{
    private readonly Plugin _plugin;
    private readonly object _debounceLock = new();
    private CancellationTokenSource _rebuildDebounce;

    public List<OnionModData> OnionLayerMods { get; private set; } = new();
    public string RootDirectory { get; }

    public OnionLayerModManager(Plugin plugin)
    {
        _plugin = plugin;
        RootDirectory = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "OnionLayerMods");
        Directory.CreateDirectory(RootDirectory);
        LoadMods();
    }

    public void LoadMods()
    {
        OnionLayerMods.Clear();
        if (!Directory.Exists(RootDirectory)) return;

        MigrateLooseZips();

        foreach (var dir in Directory.GetDirectories(RootDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var mod = OnionModData.TryLoad(dir, out var error);
            if (mod != null)
            {
                OnionLayerMods.Add(mod);
            }
            else
            {
                _plugin.PluginLog.Warning($"[Onion] Skipped '{Path.GetFileName(dir)}': {error}");
            }
        }

        RepairSharedIdentifiers();
    }

    private void MigrateLooseZips()
    {
        try
        {
            foreach (var ompPath in Directory.EnumerateFiles(RootDirectory, "*.omp", SearchOption.TopDirectoryOnly).ToList())
            {
                var mod = OnionPackageImporter.ImportZip(ompPath, RootDirectory, out var error);
                if (mod != null)
                {
                    try { File.Delete(ompPath); } catch { }
                    _plugin.PluginLog.Information($"[Onion] Auto-migrated loose archive '{Path.GetFileName(ompPath)}' into mod '{mod.Name}'.");
                }
                else
                {
                    _plugin.PluginLog.Warning($"[Onion] Failed to auto-migrate loose archive '{Path.GetFileName(ompPath)}': {error}");
                }
            }
        }
        catch (Exception ex)
        {
            _plugin.PluginLog.Error($"[Onion] Error during loose .omp migration: {ex.Message}");
        }
    }

    private void RepairSharedIdentifiers()
    {
        var dupGroups = OnionLayerMods.GroupBy(m => m.Meta.Identifier, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        var names = new HashSet<string>(OnionLayerMods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var group in dupGroups)
        {
            foreach (var duplicateMod in group.OrderBy(m => m.DirectoryPath, StringComparer.OrdinalIgnoreCase).Skip(1))
            {
                string oldName = duplicateMod.Name;
                duplicateMod.Meta.Identifier = Guid.NewGuid().ToString();
                duplicateMod.Meta.Name = OnionPackageImporter.NextFreeName(names, oldName);
                names.Add(duplicateMod.Meta.Name);
                try
                {
                    duplicateMod.SaveMeta();
                    _plugin.PluginLog.Information($"[Onion] Repaired duplicate mod identifier for '{oldName}'. Re-assigned as '{duplicateMod.Name}'.");
                }
                catch (Exception ex)
                {
                    _plugin.PluginLog.Warning($"[Onion] Could not save identifier repair for '{oldName}': {ex.Message}");
                }
            }
        }
    }

    public OnionModData? ImportFromFile(string filePath, bool deleteOriginal = false)
    {
        string error;
        OnionModData mod = null;

        if (Directory.Exists(filePath))
        {
            mod = OnionPackageImporter.ImportFolder(filePath, RootDirectory, out error);
        }
        else if (OnionPackageImporter.LooksLikeOnionZip(filePath))
        {
            mod = OnionPackageImporter.ImportZip(filePath, RootDirectory, out error);
        }
        else
        {
            error = "file is not a valid Onion package (expected meta.json)";
        }

        if (mod == null)
        {
            _plugin.PluginLog.Error($"[Onion] Import failed for '{filePath}': {error}");
            _plugin.Chat.PrintError($"[DDT] Onion import failed: {error}");
            return null;
        }

        if (deleteOriginal)
        {
            try
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            catch { }
        }

        OnionLayerMods.Add(mod);
        _plugin.PluginLog.Information($"[Onion] Imported '{mod.Name}' ({mod.Meta.TotalLayerCount} layers).");
        TriggerHotswapRebuild();
        return mod;
    }

    public void DeleteMod(OnionModData mod)
    {
        OnionLayerMods.Remove(mod);
        if (Directory.Exists(mod.DirectoryPath))
        {
            try { Directory.Delete(mod.DirectoryPath, true); } catch { }
        }
        TriggerHotswapRebuild();
    }

    public void ExportMod(OnionModData mod)
    {
        try
        {
            string exportFolder = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Exports");
            Directory.CreateDirectory(exportFolder);

            string safeName = OnionPackageImporter.Sanitize(mod.Name);
            string zipPath = Path.Combine(exportFolder, $"{safeName}.omp");
            if (File.Exists(zipPath)) File.Delete(zipPath);

            OnionPackageExporter.ExportZip(mod, zipPath);

            Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exportFolder,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            _plugin.PluginLog.Error($"[Onion] Export failed: {ex.Message}");
        }
    }

    public IEnumerable<OnionModData> GetEnabledModsForBodyPart(string bodyPart)
    {
        if (string.IsNullOrWhiteSpace(bodyPart)) yield break;
        if (!string.Equals(bodyPart, "body", StringComparison.OrdinalIgnoreCase)) yield break;

        foreach (var mod in OnionLayerMods.Where(m => m.Enabled))
        {
            yield return mod;
        }
    }

    public void TriggerHotswapRebuild()
    {
        lock (_debounceLock)
        {
            _rebuildDebounce?.Cancel();
            _rebuildDebounce = new CancellationTokenSource();
            var token = _rebuildDebounce.Token;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                    if (!token.IsCancellationRequested)
                        ExecuteHotswapRebuild();
                }
                catch (TaskCanceledException) { }
            });
        }
    }

    private void ExecuteHotswapRebuild()
    {
        if (_plugin.DragAndDropTextures == null || _plugin.SafeGameObjectManager.LocalPlayer == null)
            return;

        var charName = _plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
        _plugin.DragAndDropTextures.ScheduleRegeneration(charName, new[] { "_body" }, skipDelays: true, hideProgressUI: false);
    }

    public void Dispose() { }
}
