using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DragAndDropTexturing.Overlays
{
    public class AdvancedOverlayMod
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }

        /// <summary>Unconditional overlays when the mod has no Penumbra option groups.</summary>
        public List<AdvancedOverlay> Overlays { get; set; }

        public List<AdvancedOptionGroup> OptionGroups { get; set; }
        public List<AdvancedColorTableRow> ColorTableRows { get; set; }
    }

    public class AdvancedOptionGroup
    {
        public string PenumbraGroupName { get; set; }
        public List<AdvancedOption> Options { get; set; }
    }

    public class AdvancedOption
    {
        public string Name { get; set; }
        public List<AdvancedOverlay> Overlays { get; set; }
        public List<AdvancedColorTableRow> ColorTableRows { get; set; }
    }

    public class AdvancedOverlay
    {
        [JsonProperty("MaterialGamePath")]
        [JsonConverter(typeof(MaterialGamePathConverter))]
        public List<string> MaterialGamePath { get; set; }

        public string Diffuse { get; set; }
        public string Normal { get; set; }

        /// <summary>Specular/multi map (Proteus Mask field).</summary>
        public string Mask { get; set; }

        /// <summary>Color-table region map (Proteus Index field).</summary>
        public string Index { get; set; }

        public bool GenerateDiffuse { get; set; } = true;

        /// <summary>Explicit UV body type authored in the sidecar (bibo, gen3, gen2, tbse).</summary>
        public string SourceBodyType { get; set; }

        /// <summary>Skin = painted into skin (default). Gear = second-skin shader path (skipped by DDT).</summary>
        public string Layer { get; set; }

        public string Shader { get; set; }

        /// <summary>0–1 skin-tone masking strength for diffuse overlays. Null = full masking.</summary>
        public float? SkinToneMask { get; set; }
    }

    public class AdvancedColorTableRow
    {
        public int Row { get; set; }
        public AdvancedColorSubRow SubRowA { get; set; }
        public AdvancedColorSubRow SubRowB { get; set; }
    }

    public class AdvancedColorSubRow
    {
        public string Diffuse { get; set; }
        public float Emissive { get; set; }

        /// <summary>Opacity adjustment −100…100. Zero = no change.</summary>
        public int Opacity { get; set; }
    }

    public class ResolvedAdvancedOverlay
    {
        public string ModName { get; set; }
        public string TargetBodyPart { get; set; } // "body", "face", "hair", "tail", "eyes", "eyebrows"
        public string UVType { get; set; } // "bibo", "gen3", "tbse", etc.
        public string DiffusePath { get; set; }
        public string NormalPath { get; set; }
        public string MaskPath { get; set; }
        public string IndexPath { get; set; }
        public bool GenerateDiffuse { get; set; } = true;
        public List<AdvancedColorTableRow> ColorTableRows { get; set; }
        public List<string> CoverageMaskPaths { get; set; } = new List<string>();
        /// <summary>True when overlay requires Proteus gear/shader path (skipped by DDT bake).</summary>
        public bool RequiresShaderPath { get; set; }
    }

    /// <summary>Deserialises MaterialGamePath as either a JSON string or array of strings.</summary>
    public class MaterialGamePathConverter : JsonConverter<List<string>>
    {
        public override List<string> ReadJson(JsonReader reader, Type objectType, List<string> existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            if (reader.TokenType == JsonToken.String)
                return new List<string> { reader.Value?.ToString() ?? "" };

            if (reader.TokenType == JsonToken.StartArray)
            {
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                {
                    if (reader.TokenType == JsonToken.String)
                        list.Add(reader.Value?.ToString() ?? "");
                }
                return list;
            }

            throw new JsonSerializationException($"Expected string or array for MaterialGamePath, got {reader.TokenType}.");
        }

        public override void WriteJson(JsonWriter writer, List<string> value, JsonSerializer serializer)
        {
            if (value == null || value.Count == 0)
            {
                writer.WriteNull();
                return;
            }

            if (value.Count == 1)
                writer.WriteValue(value[0]);
            else
            {
                writer.WriteStartArray();
                foreach (var s in value)
                    writer.WriteValue(s);
                writer.WriteEndArray();
            }
        }
    }

    public static class AdvancedOverlayParser
    {
        public const string ProteusSubdir = "Proteus";
        public const string MetadataFileName = "metadata.json";

        public static Dictionary<string, List<ResolvedAdvancedOverlay>> ActiveOverlays { get; set; } =
            new Dictionary<string, List<ResolvedAdvancedOverlay>>();

        /// <summary>
        /// Locates a Proteus/overlay sidecar. Checks Proteus/metadata.json first, then legacy locations.
        /// </summary>
        public static string FindMetadataJsonPath(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
                return null;

            string proteusPath = Path.Combine(modRoot, ProteusSubdir, MetadataFileName);
            if (File.Exists(proteusPath))
                return proteusPath;

            string rootPath = Path.Combine(modRoot, MetadataFileName);
            if (File.Exists(rootPath))
                return rootPath;

            try
            {
                foreach (var subDir in Directory.GetDirectories(modRoot))
                {
                    string nestedPath = Path.Combine(subDir, MetadataFileName);
                    if (File.Exists(nestedPath))
                        return nestedPath;
                }
            }
            catch { }

            return null;
        }

        public static bool HasOverlaySidecar(string modRoot) => FindMetadataJsonPath(modRoot) != null;

        public static AdvancedOverlayMod Parse(string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath))
                    return null;

                string json = File.ReadAllText(jsonPath);
                return JsonConvert.DeserializeObject<AdvancedOverlayMod>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void ResolveTexturePaths(AdvancedOverlay overlay, string sidecarRoot,
            out string diffusePath, out string normalPath, out string maskPath, out string indexPath)
        {
            diffusePath = ResolveRelativePath(sidecarRoot, overlay?.Diffuse);
            normalPath = ResolveRelativePath(sidecarRoot, overlay?.Normal);
            maskPath = ResolveRelativePath(sidecarRoot, overlay?.Mask);
            indexPath = ResolveRelativePath(sidecarRoot, overlay?.Index);
        }

        public static string ResolveRelativePath(string sidecarRoot, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(sidecarRoot))
                return null;

            string fullPath = Path.Combine(sidecarRoot, relativePath.Replace("/", "\\"));
            return File.Exists(fullPath) ? fullPath : null;
        }

        public static bool HasRenderableTextures(string diffusePath, string normalPath, string maskPath)
        {
            return !string.IsNullOrEmpty(diffusePath)
                || !string.IsNullOrEmpty(normalPath)
                || !string.IsNullOrEmpty(maskPath);
        }

        public static void InferTargetFromMaterialPaths(IEnumerable<string> materialPaths, string sourceBodyType,
            out string targetPart, out string uvType)
        {
            targetPart = "body";
            uvType = "";

            if (!string.IsNullOrEmpty(sourceBodyType))
            {
                uvType = sourceBodyType.Trim().ToLowerInvariant();
            }

            if (materialPaths == null)
                return;

            foreach (var path in materialPaths)
            {
                if (string.IsNullOrEmpty(path))
                    continue;

                string lower = path.ToLowerInvariant();
                if (lower.Contains("/eye/") && !lower.Contains("eyebrow"))
                    targetPart = "eyes";
                else if (lower.Contains("eyebrow") || lower.Contains("/ebrow"))
                    targetPart = "eyebrows";
                else if (lower.Contains("/face/"))
                    targetPart = "face";
                else if (lower.Contains("/hair/"))
                    targetPart = "hair";
                else if (lower.Contains("/tail/"))
                    targetPart = "tail";

                if (string.IsNullOrEmpty(uvType))
                {
                    if (lower.Contains("_bibo"))
                        uvType = "bibo";
                    else if (lower.Contains("_gen3") || lower.Contains("_eve") || lower.Contains("tfgen3"))
                        uvType = "gen3";
                    else if (lower.Contains("_tbse"))
                        uvType = "tbse";
                    else if (lower.Contains("_gen2"))
                        uvType = "gen2";
                    else if (lower.Contains("otopop"))
                        uvType = "otopop";
                    else if (lower.Contains("relala"))
                        uvType = "relala";
                }
            }
        }

        public static bool IsShaderOnlyOverlay(AdvancedOverlay overlay)
        {
            if (overlay == null)
                return false;

            if (!string.IsNullOrEmpty(overlay.Layer)
                && overlay.Layer.Equals("Gear", System.StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(overlay.Shader)
                && overlay.Shader.Contains("scroll", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public const string OverlayDescriptorDefaultGearShader = "character.shpk";

        public static ResolvedAdvancedOverlay CreateResolved(string modName, AdvancedOverlay overlay,
            string sidecarRoot, IList<AdvancedColorTableRow> colorTableRows, IList<string> coverageMaskPaths)
        {
            if (IsShaderOnlyOverlay(overlay))
            {
                ResolveTexturePaths(overlay, sidecarRoot, out _, out _, out _, out _);
                InferTargetFromMaterialPaths(overlay.MaterialGamePath, overlay.SourceBodyType,
                    out var targetPart, out var uvType);
                return new ResolvedAdvancedOverlay
                {
                    ModName = modName,
                    TargetBodyPart = targetPart,
                    UVType = uvType,
                    RequiresShaderPath = true,
                };
            }

            ResolveTexturePaths(overlay, sidecarRoot, out var diffPath, out var normPath, out var maskPath, out var indexPath);

            if (!HasRenderableTextures(diffPath, normPath, maskPath)
                && !(overlay.GenerateDiffuse && !string.IsNullOrEmpty(normPath)))
                return null;

            InferTargetFromMaterialPaths(overlay.MaterialGamePath, overlay.SourceBodyType,
                out var part, out var uv);

            return new ResolvedAdvancedOverlay
            {
                ModName = modName,
                TargetBodyPart = part,
                UVType = uv,
                DiffusePath = diffPath,
                NormalPath = normPath,
                MaskPath = maskPath,
                IndexPath = indexPath,
                GenerateDiffuse = overlay.GenerateDiffuse,
                ColorTableRows = colorTableRows != null ? new List<AdvancedColorTableRow>(colorTableRows) : null,
                CoverageMaskPaths = coverageMaskPaths != null
                    ? new List<string>(coverageMaskPaths)
                    : new List<string>(),
            };
        }

        public static bool IsOptionSelected(string optionName, int optionIndex, List<string> activeOptions)
        {
            if (activeOptions == null || activeOptions.Count == 0)
                return false;

            string trimmedName = optionName?.Trim() ?? "";
            foreach (var active in activeOptions)
            {
                string trimmedActive = active?.Trim() ?? "";
                if (trimmedActive.Equals(trimmedName, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (int.TryParse(trimmedActive, out int idx) && idx == optionIndex)
                    return true;
            }

            return false;
        }
    }
}
