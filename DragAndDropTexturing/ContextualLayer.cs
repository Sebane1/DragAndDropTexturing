using System;

namespace DragAndDropTexturing;

public enum TriggerType 
{ 
    Emote, 
    HP_Threshold, 
    Combat_State,
    Kill_Count
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
    public string TexturePath { get; set; } = "";
    public string TextureDirectoryPath { get; set; } = "";
    
    // The condition that triggers this layer
    public TriggerType Trigger { get; set; } = TriggerType.Emote;
    
    // How the layer clears or decays
    public ClearCondition ClearTrigger { get; set; } = ClearCondition.Time;
    
    // Specific trigger values based on the type
    public ushort EmoteId { get; set; } = 0;
    public int HPThresholdPercentage { get; set; } = 50; 
    public int RequiredKillsPerStack { get; set; } = 1;
    
    // How long the effect lasts before turning off
    public int DurationSeconds { get; set; } = 10;
    public int DecayIntervalSeconds { get; set; } = 10;
    
    // Which body part this applies to (body, face, eyes, eyebrows) so we know which texture to hotswap
    public string TargetBodyPart { get; set; } = "body"; 
}
