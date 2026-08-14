using System;
using System.Collections.Generic;
using System.Linq;

namespace DragAndDropTexturing.Onion;

public sealed class OnionOverlayLayer
{
    public string File { get; set; } = "";
    public string Layout { get; set; } = "gen3";
    public string Map { get; set; } = "base";
    public string Mode { get; set; } = "Normal";
    public int Order { get; set; }
    public float Opacity { get; set; } = 1f;
    public List<string> Races { get; set; } = new();
    public string GeneratedFrom { get; set; }
    public string SourceHash { get; set; }

    public string MapOverrideType => Map.ToLowerInvariant() switch
    {
        "norm" => "Normal",
        "base" => "Base",
        "mask" => "Mask",
        _ => ""
    };

    public int DdtBlendMode => Mode.ToLowerInvariant() switch
    {
        "multiply" => 1,
        _ => 0
    };

    public string LayoutUvTag => Layout.ToLowerInvariant() switch
    {
        "bibo" => "bibo",
        "gen3" => "gen3",
        "vanilla" or "gen2" => "gen2",
        "tbse" => "tbse",
        _ => "gen3"
    };
}

public sealed class OnionOverlayOption
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; }
    public List<OnionOverlayLayer> Layers { get; set; } = new();
}

public sealed class OnionOverlayGroup
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "Single";
    public int Priority { get; set; }
    public int DefaultSettings { get; set; }
    public List<OnionOverlayOption> Options { get; set; } = new();

    public bool IsMulti => string.Equals(Type, "Multi", StringComparison.OrdinalIgnoreCase);
}

public sealed class OnionOverlayMeta
{
    public int FormatVersion { get; set; } = 2;
    public string Identifier { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Website { get; set; } = "";
    public bool DisableEditing { get; set; }
    public bool Locked { get; set; }
    public List<OnionOverlayLayer> Layers { get; set; } = new();
    public List<OnionOverlayGroup> Groups { get; set; } = new();

    public int TotalLayerCount => Layers.Count + Groups.Sum(g => g.Options.Sum(o => o.Layers.Count));

    public static int NormalizeSelection(OnionOverlayGroup group, int sel)
    {
        if (group.Options.Count == 0) return 0;
        if (group.IsMulti)
        {
            if (group.Options.Count >= 32) return sel;
            return sel & ((1 << group.Options.Count) - 1);
        }
        if ((uint)sel < (uint)group.Options.Count) return sel;
        if ((uint)group.DefaultSettings < (uint)group.Options.Count) return group.DefaultSettings;
        return 0;
    }

    public static string OptionScope(string groupName, string optionName)
    {
        return EscapeScopePart(groupName) + "/" + EscapeScopePart(optionName);
    }

    private static string EscapeScopePart(string s) => s.Replace("\\", "\\\\").Replace("/", "\\/");

    public List<(OnionOverlayLayer Layer, string Scope)> EffectiveLayers(IReadOnlyDictionary<string, int> selections)
    {
        var result = new List<(OnionOverlayLayer, string)>(
            Layers.OrderBy(l => l.Order).Select(l => (l, "")));

        var grouped = new List<(OnionOverlayLayer Layer, string Scope, int GroupPrio, int GroupIdx, int OptPrio, int OptIdx)>();
        for (int gi = 0; gi < Groups.Count; gi++)
        {
            var group = Groups[gi];
            if (group.Options.Count == 0) continue;

            int sel = NormalizeSelection(group,
                selections != null && selections.TryGetValue(group.Name, out var value) ? value : group.DefaultSettings);

            if (group.IsMulti)
            {
                for (int i = 0; i < group.Options.Count && i < 32; i++)
                {
                    if ((sel & (1 << i)) == 0) continue;
                    var opt = group.Options[i];
                    string scope = OptionScope(group.Name, opt.Name);
                    grouped.AddRange(opt.Layers.Select(l => (l, scope, group.Priority, gi, opt.Priority, i)));
                }
            }
            else
            {
                var opt = group.Options[sel];
                string scope = OptionScope(group.Name, opt.Name);
                grouped.AddRange(opt.Layers.Select(l => (l, scope, group.Priority, gi, 0, sel)));
            }
        }

        result.AddRange(grouped
            .OrderBy(t => t.GroupPrio).ThenBy(t => t.GroupIdx).ThenBy(t => t.OptPrio).ThenBy(t => t.OptIdx).ThenBy(t => t.Layer.Order)
            .Select(t => (t.Layer, t.Scope)));
        return result;
    }
}
