using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVLooseTextureCompiler.ImageProcessing;
using FFXIVLooseTextureCompiler.Racial;
using Lumina.Excel.Sheets;
using Penumbra.GameData.Files;
using PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer;

namespace DragAndDropTexturing.Equipment;

public sealed class WornEquipmentPiece
{
    public string SlotKey { get; init; } = "";
    private string _displayName = "";
    public string DisplayName
    {
        get => string.IsNullOrEmpty(ModName) ? _displayName : $"{_displayName} [{ModName}]";
        init => _displayName = value;
    }
    public ulong ItemId { get; init; }
    public string EquipSetId { get; init; } = "";
    public string InternalBasePath { get; init; } = "";
    public string InternalNormalPath { get; init; } = "";
    public string InternalMaskPath { get; init; } = "";
    public string InternalMaterialPath { get; init; } = "";
    public string ResolvedBaseDiskPath { get; init; } = "";

    public string ModName
    {
        get
        {
            if (string.IsNullOrEmpty(ResolvedBaseDiskPath)) return "";
            try
            {
                string penumbraDir = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                if (string.IsNullOrEmpty(penumbraDir)) return "";
                if (ResolvedBaseDiskPath.StartsWith(penumbraDir, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = ResolvedBaseDiskPath.Substring(penumbraDir.Length).TrimStart('\\', '/');
                    int slashIndex = relative.IndexOfAny(new[] { '\\', '/' });
                    if (slashIndex > 0)
                        return relative.Substring(0, slashIndex);
                    return relative;
                }
            }
            catch { }
            return "";
        }
    }

    public string MaterialName
    {
        get
        {
            if (string.IsNullOrEmpty(InternalMaterialPath)) return "";
            string filename = Path.GetFileNameWithoutExtension(InternalMaterialPath).Replace("mt_", "");
            var match = System.Text.RegularExpressions.Regex.Match(filename, @"^[a-z]\d+e\d+_[a-z]+_(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : filename;
        }
    }
}

public static class WornEquipmentResolver
{
    private static readonly HashSet<ulong> SkipItemIds = new()
    {
        0, 9292, 9293, 9294, 9295,
        10032, 10033, 10034, 10035, 10036, 13775,
    };

    private static ulong GetItemId(dynamic item)
    {
        if (item == null) return 0;
        try
        {
            if (item.AdditionalData != null && item.AdditionalData.ContainsKey("CustomItemId"))
                return (ulong)item.AdditionalData["CustomItemId"];
            if (item.AdditionalData != null && item.AdditionalData.ContainsKey("Item"))
                return (ulong)item.AdditionalData["Item"];
            
            var type = item.GetType();
            var customIdProp = type.GetProperty("CustomItemId");
            if (customIdProp != null)
                return Convert.ToUInt64(customIdProp.GetValue(item));

            var idProp = type.GetProperty("ItemId");
            if (idProp != null)
                return Convert.ToUInt64(idProp.GetValue(item));
        }
        catch { }
        return 0;
    }

    private static uint GetModelMain(dynamic item)
    {
        if (item == null) return 0;
        try
        {
            if (item.AdditionalData != null && item.AdditionalData.ContainsKey("ModelMain"))
                return (uint)item.AdditionalData["ModelMain"];

            var prop = item.GetType().GetProperty("ModelMain");
            if (prop != null)
                return Convert.ToUInt32(prop.GetValue(item));
        }
        catch { }
        return 0;
    }

    private static uint GetModelVariant(dynamic item)
    {
        if (item == null) return 0;
        try
        {
            if (item.AdditionalData != null && item.AdditionalData.ContainsKey("ModelVariant"))
                return (uint)item.AdditionalData["ModelVariant"];

            var prop = item.GetType().GetProperty("ModelVariant");
            if (prop != null)
                return Convert.ToUInt32(prop.GetValue(item));
        }
        catch { }
        return 0;
    }

    private static readonly (string SlotKey, string[] ModelSuffixes, Func<PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.Equipment, dynamic> GetItem)[] Slots =
    {
        ("head", new[] { "met", "hed", "head", "hel" }, e => e.Head),
        ("body", new[] { "top", "body" }, e => e.Body),
        ("hands", new[] { "glv", "hand", "hnd", "hands" }, e => e.Hands),
        ("legs", new[] { "dwn", "leg", "legs", "bot" }, e => e.Legs),
        ("feet", new[] { "sho", "feet", "foot" }, e => e.Feet),
    };

    public static List<WornEquipmentPiece> ResolveWornGear(ICharacter character, Plugin plugin)
    {
        var results = new List<WornEquipmentPiece>();
        if (character == null || plugin == null)
        {
            DragAndDropTexturing.Plugin.Log.Error("[Drag And Drop Debug] ResolveWornGear: character or plugin is null.");
            return results;
        }

        var customization = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
        Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;

        return ResolveWornGear(character.Name.TextValue, character.Customize.ToArray(), customization, collection, plugin);
    }

    public static List<WornEquipmentPiece> ResolveWornGear(
        string characterName,
        byte[] customizeBytes,
        PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization customization,
        Guid collection,
        Plugin plugin)
    {
        var results = new List<WornEquipmentPiece>();
        if (customization?.Equipment == null || plugin == null)
        {
            DragAndDropTexturing.Plugin.Log.Error("[Drag And Drop Debug] ResolveWornGear: customization, Equipment, or plugin is null.");
            return results;
        }

        // Dump the raw Equipment object to log to see what it actually contains
        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Equipment state: {Newtonsoft.Json.JsonConvert.SerializeObject(customization.Equipment)}");

        string[] raceCodes = GetFfxivRaceCodes(
            customization.Customize.Race.Value,
            customization.Customize.Clan.Value,
            customization.Customize.Gender.Value);

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            DragAndDropTexturing.Plugin.Log.Error("[Drag And Drop Debug] ResolveWornGear: Item ExcelSheet is null.");
            return results;
        }

        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Resolving gear for {characterName}. RaceCodes: {string.Join(", ", raceCodes)}");

        foreach (var slot in Slots)
        {
            try 
            {
                var equipItem = slot.GetItem(customization.Equipment);
                if (equipItem == null) continue;

                ulong itemId = GetItemId(equipItem);

                if (SkipItemIds.Contains(itemId))
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Skipped itemId {itemId} (in SkipItemIds).");
                    continue;
                }

                var row = itemSheet.GetRow((uint)itemId);
                
                uint equipModelMain = GetModelMain(equipItem);
                uint equipVariant = GetModelVariant(equipItem);

                uint finalModelMain = equipModelMain > 0 ? equipModelMain : (row.RowId != 0 ? (uint)row.ModelMain : 0);
                uint finalVariant = equipVariant > 0 ? equipVariant : (row.RowId != 0 ? (uint)(row.ModelMain >> 16) : 0);

                if (finalModelMain == 0)
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Skipped itemId {itemId} (ModelMain=0).");
                    continue;
                }

                DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Found valid itemId {itemId}. ModelMain={finalModelMain}");

                string itemName = row.RowId != 0 ? row.Name.ToString() : $"Item {itemId}";

                var pieces = TryResolvePieces(collection, raceCodes, (ushort)finalModelMain, (byte)finalVariant, slot.SlotKey, slot.ModelSuffixes, itemId, itemName);
                if (pieces.Count == 0)
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: TryResolvePieces failed for itemId {itemId}.");
                    continue;
                }

                foreach (var piece in pieces)
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Successfully resolved piece: {piece.DisplayName}");
                    results.Add(piece);
                }
            }
            catch (Exception ex)
            {
                DragAndDropTexturing.Plugin.Log.Error(ex, $"[Drag And Drop Debug] Exception while processing slot {slot.SlotKey}: {ex.Message}");
            }
        }

        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] ResolveWornGear finished. Found {results.Count} pieces.");
        return results;
    }

    private static string[] GetFfxivRaceCodes(int race, int clan, int gender)
    {
        string code = "0101";
        if (race == 1) code = (clan == 1 || clan == 2) ? "0101" : "0301";
        else if (race == 2) code = "0501";
        else if (race == 3) code = "1101";
        else if (race == 4) code = "0701";
        else if (race == 5) code = "0901";
        else if (race == 6) code = "1301";
        else if (race == 7) code = "1501";
        else if (race == 8) code = "1701";
        
        if (gender == 1) // Female
        {
            int codeInt = int.Parse(code);
            codeInt += 100;
            code = codeInt.ToString("D4");
        }
        
        string primaryCode = "c" + code;
        List<string> codes = new List<string> { primaryCode };
        
        if (gender == 1 && primaryCode != "c0201")
            codes.Add("c0201");
            
        if (!codes.Contains("c0101"))
            codes.Add("c0101");
            
        return codes.ToArray();
    }

    private static List<WornEquipmentPiece> TryResolvePieces(Guid collection, string[] raceCodes, ushort setId, byte variantId, string slotKey,
        string[] modelSuffixes, ulong itemId, string itemName)
    {
        var pieces = new List<WornEquipmentPiece>();
        // Ensure variant is at least 1, as 0 doesn't usually map to a valid folder.
        if (variantId == 0) variantId = 1;

        bool isAccessory = slotKey == "ears" || slotKey == "neck" || slotKey == "wrists" || slotKey == "ring_r" || slotKey == "ring_l";
        string equipSetId = isAccessory ? $"a{setId:D4}" : $"e{setId:D4}";
        string texVariant = variantId.ToString("D2");
        string mtrlVariant = variantId.ToString("D4");

        var resolvedMaterialFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raceCode in raceCodes)
        {
            foreach (string suffix in modelSuffixes)
            {
                var mtrlCandidates = EquipmentPathBuilder.BuildHumanMtrlCandidates(raceCode, equipSetId, suffix, mtrlVariant).ToList();
                mtrlCandidates.AddRange(EquipmentPathBuilder.BuildHumanMtrlCandidates(raceCode, equipSetId, suffix, "0001"));

                // Probe Penumbra for a modded .mdl file, falling back to vanilla .mdl from game data
                var mdlCandidates = EquipmentPathBuilder.BuildEquipmentModelCandidates(raceCode, equipSetId, suffix).ToList();
                foreach (string mdlCandidate in mdlCandidates)
                {
                    byte[] mdlBytes = null;
                    if (TryResolveGamePath(collection, mdlCandidate, out string mdlDisk) && !string.IsNullOrEmpty(mdlDisk))
                    {
                        try { mdlBytes = File.ReadAllBytes(mdlDisk); } catch { }
                    }
                    else
                    {
                        try
                        {
                            var ffxivMdlFile = DragAndDropTexturing.Plugin.DataManager.GetFile(mdlCandidate);
                            if (ffxivMdlFile != null) mdlBytes = ffxivMdlFile.Data;
                        }
                        catch { }
                    }

                    if (mdlBytes != null)
                    {
                        try
                        {
                            string mdlString = System.Text.Encoding.ASCII.GetString(mdlBytes);
                            var matches = System.Text.RegularExpressions.Regex.Matches(mdlString, @"[\w/\-]+\.mtrl");
                            foreach (System.Text.RegularExpressions.Match match in matches)
                            {
                                string filename = match.Value.TrimStart('/');
                                var fullPaths = new List<string>
                                {
                                    $"chara/human/{raceCode}/obj/equipment/{equipSetId}/material/v{mtrlVariant}/{filename}",
                                    $"chara/human/{raceCode}/obj/equipment/{equipSetId}/material/{filename}",
                                    $"chara/equipment/{equipSetId}/material/v{mtrlVariant}/{filename}",
                                    $"chara/equipment/{equipSetId}/material/{filename}"
                                };

                                // Also try the fallback variant 0001 just in case
                                fullPaths.Add($"chara/human/{raceCode}/obj/equipment/{equipSetId}/material/v0001/{filename}");
                                fullPaths.Add($"chara/equipment/{equipSetId}/material/v0001/{filename}");

                                foreach (var p in fullPaths)
                                {
                                    if (!mtrlCandidates.Contains(p))
                                    {
                                        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Constructed custom .mtrl path from .mdl: {p}");
                                        mtrlCandidates.Insert(0, p);
                                    }
                                }
                            }
                        }
                        catch { }
                        break; // Found the model candidate, no need to check others
                    }
                }

                if (isAccessory)
                {
                    string racePrefix = $"c{raceCode.Substring(1)}";
                    for (int i = 0; i < mtrlCandidates.Count; i++)
                    {
                        mtrlCandidates[i] = mtrlCandidates[i]
                            .Replace($"chara/human/{raceCode}/obj/equipment/", "chara/accessory/")
                            .Replace("chara/equipment/", "chara/accessory/")
                            .Replace($"{racePrefix}a", "a")
                            .Replace("__", "_");
                    }
                }

                foreach (string mtrlPath in mtrlCandidates)
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Checking mtrl candidate: {mtrlPath}");
                    string mtrlDisk = "";
                    TryResolveGamePath(collection, mtrlPath, out mtrlDisk);
                    string mtrlPathToRead = !string.IsNullOrEmpty(mtrlDisk) ? mtrlDisk : mtrlPath;

                    // Extract material name to avoid loading duplicates (e.g. legs vs legback)
                    string cleanMatName = Path.GetFileNameWithoutExtension(mtrlPath).Replace("mt_", "");
                    if (resolvedMaterialFiles.Contains(cleanMatName))
                        continue;

                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Resolved mtrl path: {mtrlPathToRead}");
                    if (!TryReadMtrlTexturePaths(mtrlPathToRead, out string internalBase, out string internalNormal, out string internalMask))
                    {
                        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Failed to read textures from mtrl: {mtrlPathToRead}");
                        continue;
                    }

                    string internalMtrl = mtrlPath.Replace("\\", "/");
                    resolvedMaterialFiles.Add(cleanMatName);

                    string resolvedDisk = "";
                    TryResolveGamePath(collection, internalBase, out resolvedDisk);

                    string displayMatName = cleanMatName;
                    var matMatch = System.Text.RegularExpressions.Regex.Match(cleanMatName, @"^[a-z]\d+e\d+_[a-z]+_(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (matMatch.Success) displayMatName = matMatch.Groups[1].Value;

                    var piece = new WornEquipmentPiece
                    {
                        SlotKey = slotKey,
                        DisplayName = $"{Capitalize(slotKey)} — {itemName} ({Capitalize(displayMatName)})",
                        ItemId = itemId,
                        EquipSetId = equipSetId,
                        InternalBasePath = internalBase,
                        InternalNormalPath = internalNormal,
                        InternalMaskPath = internalMask,
                        InternalMaterialPath = internalMtrl,
                        ResolvedBaseDiskPath = resolvedDisk ?? "",
                    };

                    pieces.Add(piece);
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Successfully resolved sub-material Piece: {piece.DisplayName}");
                }
            }
        }

        // If mtrl path resolution failed completely, try standard texture probe for slot (will only resolve a single fallback piece)
        if (pieces.Count == 0)
        {
            foreach (string raceCode in raceCodes)
            {
                foreach (string suffix in modelSuffixes)
                {
                    var texCandidates = EquipmentPathBuilder.BuildHumanTextureCandidates(raceCode, equipSetId, suffix, texVariant).ToList();
                    texCandidates.AddRange(EquipmentPathBuilder.BuildHumanTextureCandidates(raceCode, equipSetId, suffix, "01"));

                    if (isAccessory)
                    {
                        string racePrefix = $"c{raceCode.Substring(1)}";
                        for (int i = 0; i < texCandidates.Count; i++)
                        {
                            texCandidates[i] = texCandidates[i]
                                .Replace($"chara/human/{raceCode}/obj/equipment/", "chara/accessory/")
                                .Replace("chara/equipment/", "chara/accessory/")
                                .Replace($"{racePrefix}a", "a")
                                .Replace("__", "_");
                        }
                    }

                    foreach (string candidate in texCandidates)
                    {
                        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Checking texture candidate: {candidate}");
                        if (!TryResolveGamePath(collection, candidate, out _))
                            continue;

                        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] -> Valid candidate found: {candidate}");
                        string internalBase = candidate.Replace("\\", "/");
                        string internalNormal = EquipmentPathBuilder.GuessNormalPath(internalBase);
                        string internalMask = EquipmentPathBuilder.GuessMaskPath(internalBase);

                        string resolvedDisk = "";
                        TryResolveGamePath(collection, internalBase, out resolvedDisk);

                        var piece = new WornEquipmentPiece
                        {
                            SlotKey = slotKey,
                            DisplayName = $"{Capitalize(slotKey)} — {itemName}",
                            ItemId = itemId,
                            EquipSetId = equipSetId,
                            InternalBasePath = internalBase,
                            InternalNormalPath = internalNormal,
                            InternalMaskPath = internalMask,
                            InternalMaterialPath = "",
                            ResolvedBaseDiskPath = resolvedDisk ?? "",
                        };
                        pieces.Add(piece);
                        break;
                    }
                    if (pieces.Count > 0) break;
                }
                if (pieces.Count > 0) break;
            }
        }

        return pieces;
    }

    public static bool TryResolveGamePath(Guid collection, string gamePath, out string resolvedDisk)
    {
        resolvedDisk = "";
        if (string.IsNullOrEmpty(gamePath)) return false;
        try
        {
            // Check if Penumbra has a modded file
            PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collection, gamePath.Replace("\\", "/"), out resolvedDisk);
            if (!string.IsNullOrEmpty(resolvedDisk) && File.Exists(resolvedDisk))
                return true;
        }
        catch { }

        return false;
    }

    public static bool TryReadMtrlTexturePaths(string mtrlDiskPath, out string basePath, out string normalPath, out string maskPath)
    {
        basePath = normalPath = maskPath = "";
        try
        {
            byte[] data;
            if (Path.IsPathRooted(mtrlDiskPath) && File.Exists(mtrlDiskPath))
            {
                data = File.ReadAllBytes(mtrlDiskPath);
            }
            else
            {
                var gameFile = DragAndDropTexturing.Plugin.DataManager.GetFile(mtrlDiskPath);
                if (gameFile == null) return false;
                data = gameFile.Data;
            }

            var mtrl = new MtrlFile(data);
            var paths = new List<string>();
            foreach (var tex in mtrl.Textures)
            {
                if (!string.IsNullOrWhiteSpace(tex.Path))
                    paths.Add(tex.Path.Replace("\\", "/").ToLowerInvariant());
            }

            if (mtrl.ShaderPackage.Samplers != null)
            {
                foreach (var sampler in mtrl.ShaderPackage.Samplers)
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] SamplerId: 0x{sampler.SamplerId:X8}, TextureIndex: {sampler.TextureIndex}");
                }
            }

            foreach (var path in paths)
            {
                DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Found Texture Path in Mtrl: {path}");
            }

            if (paths.Count == 0) return false;

            // Match diffuse/base textures
            basePath = paths.Find(p => p.Contains("_d.tex") || p.Contains("_base") || p.Contains("_dif.tex"));

            // Match normal textures (both _norm and bare /norm.tex)
            normalPath = paths.Find(p => p.Contains("_n.tex") || p.Contains("_norm") || p.EndsWith("/norm.tex") || p.EndsWith("\\norm.tex"));

            // Match mask/multi textures (both _mask and bare /mask.tex)
            maskPath = paths.Find(p => p.Contains("_m.tex") || p.Contains("_mask") || p.Contains("_s.tex") || p.EndsWith("/mask.tex") || p.EndsWith("\\mask.tex"));

            // If no diffuse was found, prefer mask over normal as it carries the color data
            if (string.IsNullOrEmpty(basePath))
            {
                basePath = maskPath ?? normalPath ?? (paths.Count > 0 ? paths[0] : "");
            }

            // Fallbacks for normal/mask if they weren't matched
            if (string.IsNullOrEmpty(normalPath))
                normalPath = paths.Count > 1 ? paths[1] : EquipmentPathBuilder.GuessNormalPath(basePath);
            if (string.IsNullOrEmpty(maskPath))
                maskPath = paths.Count > 2 ? paths[2] : EquipmentPathBuilder.GuessMaskPath(basePath);

            DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Classified textures — base: {basePath}, norm: {normalPath}, mask: {maskPath}");
            return !string.IsNullOrEmpty(basePath);
        }
        catch
        {
            return false;
        }
    }

    public static string ExportResolvedTextureToPng(string diskOrGamePath, Guid collection, string outputDir, Plugin plugin, string slotKey = null, string materialName = null)
    {
        if (string.IsNullOrEmpty(diskOrGamePath)) return null;
        Directory.CreateDirectory(outputDir);

        string source = diskOrGamePath;
        if (!Path.IsPathRooted(source))
        {
            if (!TryResolveGamePath(collection, source, out string resolvedDisk) || string.IsNullOrEmpty(resolvedDisk))
            {
                // Penumbra didn't have it, keep the original internal FFXIV path so we can pull from Lumina
            }
            else
            {
                source = resolvedDisk; // Update to the modded physical path
            }
        }

        if (string.IsNullOrEmpty(source))
            return null;

        if (source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return source;

        try
        {
            // Build a unique output name — modders reuse generic filenames like mask.tex
            // in different subdirectories, so include a hash of the full path for uniqueness.
            string sourceHash = source.GetHashCode().ToString("X8");
            string slotSuffix = !string.IsNullOrEmpty(slotKey) ? $"_{slotKey}" : "";
            string matSuffix = !string.IsNullOrEmpty(materialName) ? $"_{materialName}" : "";
            string outName = Path.GetFileNameWithoutExtension(source) + "_" + sourceHash + "_worn" + slotSuffix + matSuffix + ".png";
            string outPath = Path.Combine(outputDir, outName);

            if (File.Exists(outPath))
            {
                if (File.Exists(source))
                {
                    var sourceTime = File.GetLastWriteTimeUtc(source);
                    var outTime = File.GetLastWriteTimeUtc(outPath);
                    if (sourceTime <= outTime)
                    {
                        plugin?.PluginLog.Info($"[WornGear] Cached PNG found and source is not newer: {outPath}");
                        return outPath;
                    }
                    plugin?.PluginLog.Info($"[WornGear] Source file {source} is newer than cached PNG {outPath}. Regenerating...");
                }
                else
                {
                    plugin?.PluginLog.Info($"[WornGear] Cached PNG found for vanilla game texture: {outPath}");
                    return outPath;
                }
            }

            System.Drawing.Bitmap bitmap = null;

            if (File.Exists(source))
            {
                bitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.ResolveBitmap(source);
            }
            else
            {
                var ffxivFile = DragAndDropTexturing.Plugin.DataManager.GetFile(source);
                if (ffxivFile != null && ffxivFile.Data != null)
                {
                    using (var stream = new MemoryStream(ffxivFile.Data))
                    {
                        bitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.TexToBitmap(stream, false);
                    }
                }
            }

            if (bitmap == null) return null;
            FFXIVLooseTextureCompiler.ImageProcessing.TexIO.SaveBitmapFast(bitmap, outPath);
            return outPath;
        }
        catch (Exception ex)
        {
            plugin?.PluginLog.Warning(ex, $"[WornGear] Failed to export texture: {diskOrGamePath}");
            return null;
        }
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].Replace('_', ' ');
}
