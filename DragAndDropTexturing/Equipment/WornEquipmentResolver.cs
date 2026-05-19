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
    public string DisplayName { get; init; } = "";
    public ulong ItemId { get; init; }
    public string InternalBasePath { get; init; } = "";
    public string InternalNormalPath { get; init; } = "";
    public string InternalMaskPath { get; init; } = "";
    public string InternalMaterialPath { get; init; } = "";
    public string ResolvedBaseDiskPath { get; init; } = "";
}

public static class WornEquipmentResolver
{
    private static readonly HashSet<ulong> SkipItemIds = new()
    {
        0, 9292, 9293, 9294, 9295,
        10032, 10033, 10034, 10035, 10036, 13775,
    };

    private static ulong GetItemId(dynamic slotObject)
    {
        if (slotObject == null) return 0;
        ulong itemId = (ulong)slotObject.ItemId;
        if (itemId == 0 && slotObject.AdditionalData != null && slotObject.AdditionalData.ContainsKey("CustomItemId"))
        {
            itemId = (ulong)slotObject.AdditionalData["CustomItemId"];
        }
        else if (itemId == 0 && slotObject.AdditionalData != null && slotObject.AdditionalData.ContainsKey("Item"))
        {
            itemId = (ulong)slotObject.AdditionalData["Item"];
        }
        return itemId;
    }

    private static readonly (string SlotKey, string[] ModelSuffixes, Func<PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.Equipment, ulong> ItemId)[] Slots =
    {
        ("head", new[] { "met", "hed", "head", "hel" }, e => GetItemId(e.Head)),
        ("body", new[] { "top", "body" }, e => GetItemId(e.Body)),
        ("hands", new[] { "glv", "hand", "hnd", "hands" }, e => GetItemId(e.Hands)),
        ("legs", new[] { "dwn", "leg", "legs", "bot" }, e => GetItemId(e.Legs)),
        ("feet", new[] { "sho", "feet", "foot" }, e => GetItemId(e.Feet)),
        ("ears", new[] { "ear", "ears", "acc", "" }, e => GetItemId(e.Ears)),
        ("neck", new[] { "nek", "neck", "acc", "" }, e => GetItemId(e.Neck)),
        ("wrists", new[] { "wrs", "vit", "wrist", "acc", "" }, e => GetItemId(e.Wrists)),
        ("ring_r", new[] { "rir", "rigr", "ring", "acc", "" }, e => GetItemId(e.RFinger)),
        ("ring_l", new[] { "ril", "rigl", "ring", "acc", "" }, e => GetItemId(e.LFinger)),
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
        if (customization?.Equipment == null)
        {
            DragAndDropTexturing.Plugin.Log.Error("[Drag And Drop Debug] ResolveWornGear: customization or Equipment is null.");
            return results;
        }

        // Dump the raw Equipment object to log to see what it actually contains
        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Equipment state: {Newtonsoft.Json.JsonConvert.SerializeObject(customization.Equipment)}");

        Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
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

        DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] Resolving gear for {character.Name.TextValue}. RaceCodes: {string.Join(", ", raceCodes)}");

        foreach (var slot in Slots)
        {
            try 
            {
                ulong itemId = slot.ItemId(customization.Equipment);
                
                if (SkipItemIds.Contains(itemId))
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Skipped itemId {itemId} (in SkipItemIds).");
                    continue;
                }

                var row = itemSheet.GetRow((uint)itemId);
                if (row.RowId == 0 || row.ModelMain == 0)
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Skipped itemId {itemId} (RowId={row.RowId}, ModelMain={row.ModelMain}).");
                    continue;
                }

                DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Found valid itemId {itemId}. ModelMain={row.ModelMain}");

                if (!TryResolvePiece(collection, raceCodes, row, slot.SlotKey, slot.ModelSuffixes, itemId, out var piece))
                {
                    DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: TryResolvePiece failed for itemId {itemId}.");
                    continue;
                }

                DragAndDropTexturing.Plugin.Log.Information($"[Drag And Drop Debug] {slot.SlotKey}: Successfully resolved piece: {piece.DisplayName}");
                results.Add(piece);
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

    private static bool TryResolvePiece(Guid collection, string[] raceCodes, Item row, string slotKey,
        string[] modelSuffixes, ulong itemId, out WornEquipmentPiece piece)
    {
        piece = null;
        ushort setId = (ushort)row.ModelMain;
        byte variantId = (byte)(row.ModelMain >> 16);
        // Ensure variant is at least 1, as 0 doesn't usually map to a valid folder.
        if (variantId == 0) variantId = 1;

        bool isAccessory = slotKey == "ears" || slotKey == "neck" || slotKey == "wrists" || slotKey == "ring_r" || slotKey == "ring_l";
        string equipSetId = isAccessory ? $"a{setId:D4}" : $"e{setId:D4}";
        string texVariant = variantId.ToString("D2");
        string mtrlVariant = variantId.ToString("D4");

        string internalBase = "";
        string internalNormal = "";
        string internalMask = "";
        string internalMtrl = "";

        foreach (string raceCode in raceCodes)
        {
            // Prefer material file texture references (most accurate for modded gear).
            foreach (string suffix in modelSuffixes)
            {
                var mtrlCandidates = EquipmentPathBuilder.BuildHumanMtrlCandidates(raceCode, equipSetId, suffix, mtrlVariant).ToList();
                mtrlCandidates.AddRange(EquipmentPathBuilder.BuildHumanMtrlCandidates(raceCode, equipSetId, suffix, "0001"));

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
                    if (!TryResolveGamePath(collection, mtrlPath, out string mtrlDisk) || string.IsNullOrEmpty(mtrlDisk))
                        continue;

                    if (!TryReadMtrlTexturePaths(mtrlDisk, out internalBase, out internalNormal, out internalMask))
                        continue;

                    internalMtrl = mtrlPath.Replace("\\", "/");
                    break;
                }

                if (!string.IsNullOrEmpty(internalBase)) break;
            }

            // Fallback: probe common texture path patterns.
            if (string.IsNullOrEmpty(internalBase))
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
                        internalBase = candidate.Replace("\\", "/");
                        internalNormal = EquipmentPathBuilder.GuessNormalPath(internalBase);
                        internalMask = EquipmentPathBuilder.GuessMaskPath(internalBase);
                        break;
                    }

                    if (!string.IsNullOrEmpty(internalBase)) break;
                }
            }

            if (!string.IsNullOrEmpty(internalBase)) break;
        }

        if (string.IsNullOrEmpty(internalBase))
            return false;

        if (string.IsNullOrEmpty(internalNormal))
            internalNormal = EquipmentPathBuilder.GuessNormalPath(internalBase);
        if (string.IsNullOrEmpty(internalMask))
            internalMask = EquipmentPathBuilder.GuessMaskPath(internalBase);

        string resolvedDisk = "";
        TryResolveGamePath(collection, internalBase, out resolvedDisk);

        string itemName = row.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(itemName))
            itemName = $"Item {itemId}";

        piece = new WornEquipmentPiece
        {
            SlotKey = slotKey,
            DisplayName = $"{Capitalize(slotKey)} — {itemName}",
            ItemId = itemId,
            InternalBasePath = internalBase,
            InternalNormalPath = internalNormal,
            InternalMaskPath = internalMask,
            InternalMaterialPath = internalMtrl,
            ResolvedBaseDiskPath = resolvedDisk ?? "",
        };
        return true;
    }

    private static bool TryResolveGamePath(Guid collection, string gamePath, out string resolvedDisk)
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

        // If not modded, check if it exists in the native game files
        try 
        {
            string ffxivPath = gamePath.Replace("\\", "/");
            if (DragAndDropTexturing.Plugin.DataManager.FileExists(ffxivPath))
            {
                resolvedDisk = ffxivPath;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool TryReadMtrlTexturePaths(string mtrlDiskPath, out string basePath, out string normalPath, out string maskPath)
    {
        basePath = normalPath = maskPath = "";
        try
        {
            var data = File.ReadAllBytes(mtrlDiskPath);
            var mtrl = new MtrlFile(data);
            var paths = new List<string>();
            foreach (var tex in mtrl.Textures)
            {
                if (!string.IsNullOrWhiteSpace(tex.Path))
                    paths.Add(tex.Path.Replace("\\", "/").ToLowerInvariant());
            }

            if (paths.Count == 0) return false;

            basePath = paths.Find(p => p.Contains("_d.tex") || p.Contains("_base.tex") || p.Contains("_dif.tex")) ?? paths[0];
            normalPath = paths.Find(p => p.Contains("_n.tex") || p.Contains("_norm.tex")) ?? EquipmentPathBuilder.GuessNormalPath(basePath);
            maskPath = paths.Find(p => p.Contains("_m.tex") || p.Contains("_mask.tex") || p.Contains("_s.tex")) ?? EquipmentPathBuilder.GuessMaskPath(basePath);
            return !string.IsNullOrEmpty(basePath);
        }
        catch
        {
            return false;
        }
    }

    public static string ExportResolvedTextureToPng(string diskOrGamePath, Guid collection, string outputDir, Plugin plugin)
    {
        if (string.IsNullOrEmpty(diskOrGamePath)) return null;
        Directory.CreateDirectory(outputDir);

        string source = diskOrGamePath;
        if (!Path.IsPathRooted(source))
            TryResolveGamePath(collection, source, out source);

        if (string.IsNullOrEmpty(source))
            return null;

        if (source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return source;

        try
        {
            string outName = Path.GetFileNameWithoutExtension(source) + "_worn.png";
            string outPath = Path.Combine(outputDir, outName);
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
