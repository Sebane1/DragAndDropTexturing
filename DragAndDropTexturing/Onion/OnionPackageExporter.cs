using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;

namespace DragAndDropTexturing.Onion;

public static class OnionPackageExporter
{
    private static CompressionLevel LevelFor(string relativePath)
    {
        if (relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return CompressionLevel.NoCompression;
        return CompressionLevel.Optimal;
    }

    public static void ExportZip(OnionModData mod, string outPath)
    {
        if (File.Exists(outPath)) File.Delete(outPath);

        using var zip = ZipFile.Open(outPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(mod.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(mod.DirectoryPath, file).Replace('\\', '/');
            if (rel.Equals(OnionModData.MetaFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Equals(OnionModData.SettingsFileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith('.')) continue;
            zip.CreateEntryFromFile(file, rel, LevelFor(rel));
        }

        var exportMeta = JsonConvert.DeserializeObject<OnionOverlayMeta>(
            JsonConvert.SerializeObject(mod.Meta));
        exportMeta.FormatVersion = 2;
        exportMeta.Locked = false;
        exportMeta.DisableEditing = false;

        using var writer = new StreamWriter(zip.CreateEntry(OnionModData.MetaFileName, CompressionLevel.Optimal).Open());
        writer.Write(JsonConvert.SerializeObject(exportMeta, Formatting.Indented));
    }
}
