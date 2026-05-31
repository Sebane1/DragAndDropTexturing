using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;

namespace DragAndDropTexturing.Overlays
{
    public class AdvancedOverlayMod
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public List<AdvancedOptionGroup> OptionGroups { get; set; }
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
        public List<string> MaterialGamePath { get; set; }
        public string Diffuse { get; set; }
        public string Normal { get; set; }
        public string Index { get; set; }
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
        public float Opacity { get; set; }
    }

    public class ResolvedAdvancedOverlay
    {
        public string ModName { get; set; }
        public string TargetBodyPart { get; set; } // "body", "face", "eyes", "eyebrows"
        public string UVType { get; set; } // "bibo", "gen3", "tbse", etc.
        public string DiffusePath { get; set; }
        public string NormalPath { get; set; }
        public string MaskPath { get; set; }
    }

    public static class AdvancedOverlayParser
    {
        public static List<ResolvedAdvancedOverlay> ActiveOverlays { get; set; } = new List<ResolvedAdvancedOverlay>();

        public static AdvancedOverlayMod Parse(string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath)) return null;
                string json = File.ReadAllText(jsonPath);
                return JsonConvert.DeserializeObject<AdvancedOverlayMod>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
