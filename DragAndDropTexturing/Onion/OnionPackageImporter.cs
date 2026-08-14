using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace DragAndDropTexturing.Onion;

public static class OnionPackageImporter
{
    public static OnionModData? ImportZip(string zipPath, string libraryDirectory, out string error)
    {
        error = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(OnionModData.MetaFileName) == null)
            {
                error = "no meta.json in package";
                return null;
            }

            string baseName = Sanitize(Path.GetFileNameWithoutExtension(zipPath));
            Directory.CreateDirectory(libraryDirectory);
            string target = UniqueDirectory(libraryDirectory, baseName);
            ZipFile.ExtractToDirectory(zipPath, target);

            var mod = OnionModData.TryLoad(target, out error);
            if (mod == null)
            {
                try { Directory.Delete(target, true); } catch { }
                return null;
            }

            EnsureUniqueIdentifier(mod, libraryDirectory, target);
            mod.Save();
            return mod;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static OnionModData? ImportFolder(string sourceFolder, string libraryDirectory, out string error)
    {
        error = null;
        try
        {
            string onionSub = Path.Combine(sourceFolder, "Onion");
            if (File.Exists(Path.Combine(onionSub, OnionModData.MetaFileName)))
                sourceFolder = onionSub;

            var probe = OnionModData.TryLoad(sourceFolder, out error);
            if (probe == null) return null;

            Directory.CreateDirectory(libraryDirectory);
            string target = UniqueDirectory(libraryDirectory, Sanitize(probe.Name));
            CopyDirectory(sourceFolder, target);

            var mod = OnionModData.TryLoad(target, out error);
            if (mod == null)
            {
                try { Directory.Delete(target, true); } catch { }
                return null;
            }

            EnsureUniqueIdentifier(mod, libraryDirectory, target);
            mod.Save();
            return mod;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static bool LooksLikeOnionZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.GetEntry(OnionModData.MetaFileName) != null;
        }
        catch { return false; }
    }

    public static bool LooksLikeOnionFolder(string folderPath)
    {
        if (File.Exists(Path.Combine(folderPath, OnionModData.MetaFileName))) return true;
        return File.Exists(Path.Combine(folderPath, "Onion", OnionModData.MetaFileName));
    }

    private static void EnsureUniqueIdentifier(OnionModData mod, string libraryDirectory, string targetDir)
    {
        var siblings = Directory.EnumerateDirectories(libraryDirectory)
            .Where(d => !string.Equals(Path.GetFullPath(d), Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            .Select(d => OnionModData.TryLoad(d, out _))
            .Where(m => m != null)
            .ToList();

        if (siblings.Any(m => m.Meta.Identifier == mod.Meta.Identifier))
        {
            mod.Meta.Identifier = Guid.NewGuid().ToString();
            mod.Meta.Name = NextFreeName(siblings.Select(m => m.Name), mod.Meta.Name);
            mod.SaveMeta();
        }
    }

    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string text = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return text.Length > 0 ? text : "Mod";
    }

    private static string UniqueDirectory(string root, string name)
    {
        string candidate = Path.Combine(root, name);
        int n = 2;
        while (Directory.Exists(candidate))
            candidate = Path.Combine(root, $"{name} ({n++})");
        return candidate;
    }

    public static string NextFreeName(IEnumerable<string> taken, string wanted)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(wanted)) return wanted;
        string stem = Regex.Replace(wanted, @"\s*\(\d+\)$", "");
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n})";
            if (!set.Contains(candidate)) return candidate;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            string dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
