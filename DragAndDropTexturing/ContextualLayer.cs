using System;

namespace DragAndDropTexturing;

public enum TriggerType 
{ 
    Emote, 
    HP_Threshold, 
    Combat_State,
    Kill_Count,
    Action_Used,
    Weapon_Drawn,
    Audio_Path_Load,
    Chat_Message,
    Enemy_Nearby,
    Territory_ID,
    Weather_ID,
    In_Game_Time,
    Swimming_State,
    Mounted_State
}

public enum ClearCondition
{
    Time,
    Swimming,
    Zone_Change
}

[Serializable]
public class ContextualLayer
{
    public string Name { get; set; } = "New Context Layer";
    public bool Enabled { get; set; } = true;
    [Newtonsoft.Json.JsonIgnore]
    public string DirectoryPath { get; set; } = "";
    
    // The condition that triggers this layer
    public TriggerType Trigger { get; set; } = TriggerType.Emote;
    
    // How the layer clears or decays
    public ClearCondition ClearTrigger { get; set; } = ClearCondition.Time;
    
    // Specific trigger values based on the type
    public ushort EmoteId { get; set; } = 0;
    public int HPThresholdPercentage { get; set; } = 50; 
    public int RequiredKillsPerStack { get; set; } = 1;
    public string AudioTriggerPath { get; set; } = "";
    public int RequiredSoundsPerStack { get; set; } = 1;
    public string ChatRegex { get; set; } = "";
    public bool ChatFilterCustomEmotesOnly { get; set; } = true;
    public string TargetEnemyName { get; set; } = "";
    public uint TargetTerritoryId { get; set; } = 0;
    public uint TargetWeatherId { get; set; } = 0;
    public int TargetTimeStartHour { get; set; } = 0;
    public int TargetTimeEndHour { get; set; } = 0;
    
    // How long the effect lasts before turning off
    public int DurationSeconds { get; set; } = 10;
    public int DecayIntervalSeconds { get; set; } = 10;
    
    // Which body part this applies to (body, face, eyes, eyebrows) so we know which texture to hotswap
    public string TargetBodyPart { get; set; } = "body"; 
    
    // If true, files in the directory are treated as decals to be randomly stamped rather than full overlays
    public bool ProceduralDecalMode { get; set; } = false;

    public void Save()
    {
        if (string.IsNullOrEmpty(DirectoryPath)) return;
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
        System.IO.File.WriteAllText(System.IO.Path.Combine(DirectoryPath, "rules.json"), json);
    }

    public static ContextualLayer Load(string directoryPath)
    {
        string file = System.IO.Path.Combine(directoryPath, "rules.json");
        if (System.IO.File.Exists(file))
        {
            var json = System.IO.File.ReadAllText(file);
            var layer = Newtonsoft.Json.JsonConvert.DeserializeObject<ContextualLayer>(json);
            if (layer != null)
            {
                layer.DirectoryPath = directoryPath;
                return layer;
            }
        }
        return new ContextualLayer { DirectoryPath = directoryPath };
    }
}
