using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DragAndDropTexturing
{
    public class ActiveContextualLayer
    {
        public ContextualLayer LayerDef;
        public Stopwatch Timer = new Stopwatch();
        public int CurrentStackCount = 0;
        public int KillsSinceLastStack = 0;
        public int SoundsSinceLastStack = 0;
        public List<string> CachedTexturePaths = new List<string>();
    }

    public class ContextualLayerManager : IDisposable
    {
        private Plugin _plugin;
        private EmoteReaderHooks _emoteReader;
        private ActionReaderHooks _actionReader;
        private AudioReaderHooks _audioReader;
        private System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadActions = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        private System.Threading.CancellationTokenSource _rebuildDebounce = null;
        private readonly object _debounceLock = new object();
        
        // Track which layers are currently active
        private List<ActiveContextualLayer> _activeLayers = new List<ActiveContextualLayer>();
        private HashSet<ulong> _knownDeadEnemies = new HashSet<ulong>();
        private HashSet<ulong> _seenAliveEnemies = new HashSet<ulong>();
        private HashSet<string> _contextuallyTouchedParts = new HashSet<string>();
        
        public List<ActiveContextualLayer> GetActiveLayers() => _activeLayers;

        public List<ContextualLayer> ContextualLayers { get; private set; } = new List<ContextualLayer>();
        public string RootDirectory { get; private set; }

        public ContextualLayerManager(Plugin plugin, EmoteReaderHooks emoteReader, ActionReaderHooks actionReader, AudioReaderHooks audioReader)
        {
            _plugin = plugin;
            _emoteReader = emoteReader;
            _actionReader = actionReader;
            _audioReader = audioReader;
            
            RootDirectory = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "ContextualLayers");
            if (!System.IO.Directory.Exists(RootDirectory))
            {
                System.IO.Directory.CreateDirectory(RootDirectory);
            }
            LoadLayers();

            if (_emoteReader != null)
            {
                _emoteReader.OnEmote += OnEmote;
            }
            if (_actionReader != null)
            {
                _actionReader.OnActionUsed += OnActionUsed;
            }
            if (_audioReader != null)
            {
                _audioReader.OnSoundPlayed += OnSoundPlayed;
            }
            
            Plugin.Framework.Update += Framework_Update;
            Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
            _plugin.Chat.ChatMessage += OnChatMessage;
        }

        public void LoadLayers()
        {
            ContextualLayers.Clear();
            _activeLayers.Clear();
            if (System.IO.Directory.Exists(RootDirectory))
            {
                var dirs = System.IO.Directory.GetDirectories(RootDirectory);
                foreach (var d in dirs)
                {
                    var layer = ContextualLayer.Load(d);
                    ContextualLayers.Add(layer);

                    // Restore cross-session stack counts
                    if (_plugin.Configuration.PersistedContextualStacks.TryGetValue(layer.DirectoryPath, out int stackCount))
                    {
                        if (stackCount > 0 && layer.Trigger != TriggerType.HP_Threshold && layer.Trigger != TriggerType.Combat_State)
                        {
                            var active = new ActiveContextualLayer { LayerDef = layer, CurrentStackCount = stackCount };
                            
                            if (layer.ProceduralDecalMode && !string.IsNullOrEmpty(_plugin.Configuration.PersistedProceduralCanvasPath) && System.IO.File.Exists(_plugin.Configuration.PersistedProceduralCanvasPath))
                            {
                                active.CachedTexturePaths = new List<string> { _plugin.Configuration.PersistedProceduralCanvasPath };
                            }
                            else if (System.IO.Directory.Exists(layer.DirectoryPath))
                            {
                                var files = System.IO.Directory.GetFiles(layer.DirectoryPath, "*.png")
                                    .Where(f => !f.Contains("_temp", StringComparison.OrdinalIgnoreCase) && 
                                                !f.Contains("_from_bibo_to_gen3", StringComparison.OrdinalIgnoreCase) && 
                                                !f.Contains("_from_gen3_to_bibo", StringComparison.OrdinalIgnoreCase))
                                    .ToList();
                                files.Sort();
                                active.CachedTexturePaths = files;
                            }
                            
                            active.Timer.Start();
                            _activeLayers.Add(active);
                        }
                    }
                }
            }
            EnsureHeadlessWindowIsRunningIfNeeded();
        }

        private void EnsureHeadlessWindowIsRunningIfNeeded()
        {
            if (ContextualLayers.Any(l => l.ProceduralDecalMode && l.Enabled))
            {
                var window = GetOrCreateHeadlessPaintWindow();
                if (!string.IsNullOrEmpty(_plugin.Configuration.PersistedProceduralCanvasPath) && System.IO.File.Exists(_plugin.Configuration.PersistedProceduralCanvasPath))
                {
                    window.EditSourcePath = _plugin.Configuration.PersistedProceduralCanvasPath;
                }
            }
        }

        public void SavePersistedStates()
        {
            _plugin.Configuration.PersistedContextualStacks.Clear();
            foreach (var active in _activeLayers)
            {
                if (active.LayerDef.Trigger != TriggerType.HP_Threshold && active.LayerDef.Trigger != TriggerType.Combat_State)
                {
                    _plugin.Configuration.PersistedContextualStacks[active.LayerDef.DirectoryPath] = active.CurrentStackCount;
                }
            }
            _plugin.Configuration.Save();
        }

        public ContextualLayer CreateNewLayer()
        {
            string newDir = System.IO.Path.Combine(RootDirectory, Guid.NewGuid().ToString());
            System.IO.Directory.CreateDirectory(newDir);
            
            // Create a helpful placeholder file so users know what to do when they open the folder
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(newDir, "place_layering_images_here.txt"), 
                "Drop your .png texture files into this directory!\n\n" +
                "For stacking triggers (like HP Thresholds or Actions Used), the plugin will sort the files alphabetically and apply them in sequence.\n" +
                "For non-stacking triggers, it will apply all .png files found in this folder simultaneously."
            );

            var layer = new ContextualLayer { DirectoryPath = newDir, Name = "New Context Layer" };
            layer.Save();
            ContextualLayers.Add(layer);
            return layer;
        }

        public void DeleteLayer(ContextualLayer layer)
        {
            ContextualLayers.Remove(layer);
            _activeLayers.RemoveAll(x => x.LayerDef == layer);
            if (System.IO.Directory.Exists(layer.DirectoryPath))
            {
                try { System.IO.Directory.Delete(layer.DirectoryPath, true); } catch { }
            }
            TriggerHotswapRebuild();
        }

        public void ExportLayer(ContextualLayer layer)
        {
            try
            {
                string exportFolder = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Exports");
                if (!System.IO.Directory.Exists(exportFolder)) System.IO.Directory.CreateDirectory(exportFolder);
                
                string safeName = string.Join("_", layer.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
                string zipPath = System.IO.Path.Combine(exportFolder, $"{safeName}.clmp");
                
                if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
                
                System.IO.Compression.ZipFile.CreateFromDirectory(layer.DirectoryPath, zipPath);
                
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = exportFolder,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error($"Failed to export Contextual Layer: {ex.Message}");
            }
        }

        public void ImportLayersFromSavedOverlaysFolder()
        {
            string importFolder = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "SavedOverlays");
            if (!System.IO.Directory.Exists(importFolder)) System.IO.Directory.CreateDirectory(importFolder);
            
            var files = System.IO.Directory.GetFiles(importFolder, "*.clmp");
            foreach (var file in files)
            {
                ImportLayerFromFile(file, true);
            }
        }

        public void ImportLayerFromFile(string filePath, bool deleteOriginal = false)
        {
            try
            {
                string newDir = System.IO.Path.Combine(RootDirectory, Guid.NewGuid().ToString());
                System.IO.Directory.CreateDirectory(newDir);
                
                System.IO.Compression.ZipFile.ExtractToDirectory(filePath, newDir);
                
                if (deleteOriginal)
                {
                    System.IO.File.Delete(filePath);
                }
                
                var layer = ContextualLayer.Load(newDir);
                layer.DirectoryPath = newDir;
                layer.Save();
                ContextualLayers.Add(layer);
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error($"Failed to import Contextual Layer {filePath}: {ex.Message}");
            }
        }

        private void OnTerritoryChanged(uint obj)
        {
            bool layersChanged = false;
            for (int i = _activeLayers.Count - 1; i >= 0; i--)
            {
                var active = _activeLayers[i];
                if (active.LayerDef.ClearTrigger == ClearCondition.Zone_Change)
                {
                    _activeLayers.RemoveAt(i);
                    layersChanged = true;
                    _plugin.PluginLog.Information($"Contextual Layer '{active.LayerDef.Name}' cleared due to zone change.");
                }
            }
            if (layersChanged) TriggerHotswapRebuild();
            SavePersistedStates();
        }

        private void OnSoundPlayed(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _plugin.PluginLog.Information($"[Contextual Layers] Sound detected: {path}");
            foreach (var layer in ContextualLayers.Where(l => l.Enabled))
            {
                if (layer.Trigger == TriggerType.Audio_Path_Load && !string.IsNullOrEmpty(layer.AudioTriggerPath))
                {
                    if (path.Contains(layer.AudioTriggerPath, StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateLayer(layer);
                    }
                }
            }
        }

        private void OnChatMessage(Dalamud.Game.Chat.IHandleableChatMessage msg)
        {
            var msgText = msg.Message?.TextValue;
            if (string.IsNullOrEmpty(msgText)) return;

            foreach (var layer in ContextualLayers.Where(l => l.Enabled))
            {
                if (layer.Trigger == TriggerType.Chat_Message && !string.IsNullOrEmpty(layer.ChatRegex))
                {
                    // If filter is enabled, only accept Custom Emotes or Standard Emotes
                    if (layer.ChatFilterCustomEmotesOnly)
                    {
                        var t = msg.GetType().GetProperty("Type")?.GetValue(msg) 
                             ?? msg.GetType().GetProperty("LogKind")?.GetValue(msg)
                             ?? msg.GetType().GetProperty("ChatType")?.GetValue(msg);
                             
                        if (t != null && t is Enum e)
                        {
                            string typeName = e.ToString();
                            if (!typeName.Contains("Emote", StringComparison.OrdinalIgnoreCase)) continue;
                        }
                    }

                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(msgText, layer.ChatRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            ActivateLayer(layer);
                        }
                    }
                    catch { /* Invalid Regex */ }
                }
            }
        }

        private void OnEmote(IGameObject instigator, ushort emoteId)
        {
            if (instigator == null) return;
            var player = _plugin.SafeGameObjectManager.LocalPlayer as GameObjectHelper.ThreadSafeDalamudObjectTable.ThreadSafeCharacter;
            if (player == null || instigator.GameObjectId != player.GameObjectId) return;

            foreach (var layer in ContextualLayers.Where(l => l.Enabled))
            {
                if (layer.Trigger == TriggerType.Emote && layer.EmoteId == emoteId)
                {
                    ActivateLayer(layer);
                }
            }
        }

        private void OnActionUsed(uint actionId)
        {
            foreach (var layer in ContextualLayers.Where(l => l.Enabled))
            {
                if (layer.Trigger == TriggerType.Action_Used)
                {
                    // To support specific actions in the future, we could add an ActionId property. 
                    // For now, any spell casts advance the stack/activate.
                    ActivateLayer(layer);
                }
            }
        }

        private void Framework_Update(Dalamud.Plugin.Services.IFramework framework)
        {
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); } catch (Exception ex) { _plugin.PluginLog.Error(ex, "[ContextualLayerManager] Main thread action failed."); }
            }

            var player = _plugin.SafeGameObjectManager.LocalPlayer as GameObjectHelper.ThreadSafeDalamudObjectTable.ThreadSafeCharacter;
            if (player == null) return;

            // 1. Check HP Thresholds and Combat State
            float hpPercentage = 100f;
            if (player.MaxHp > 0)
            {
                hpPercentage = ((float)player.CurrentHp / (float)player.MaxHp) * 100f;
            }
            
            bool inCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
            bool weaponDrawn = false;
            bool isSwimming = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Swimming] || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving];
            bool isMounted = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted] || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.RidingPillion];
            try
            {
                weaponDrawn = player.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.WeaponOut);
            }
            catch { return; } // Native character struct invalidated — skip this frame

            if (isSwimming)
            {
                bool clearedProcedural = false;
                for (int i = _activeLayers.Count - 1; i >= 0; i--)
                {
                    if (_activeLayers[i].LayerDef.ProceduralDecalMode)
                    {
                        _activeLayers.RemoveAt(i);
                        clearedProcedural = true;
                    }
                }
                
                if (_headlessPaintWindow != null && _headlessPaintWindow.IsOpen)
                {
                    _headlessPaintWindow.ClearCanvas();
                }

                if (clearedProcedural)
                {
                    _plugin.Configuration.PersistedProceduralCanvasPath = null;
                    _plugin.Configuration.Save();
                    TriggerHotswapRebuild();
                }
            }

            long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long eorzeaMs = (long)(unixMs * (3600.0 / 175.0));
            DateTime eorzeaTime = DateTimeOffset.FromUnixTimeMilliseconds(eorzeaMs).UtcDateTime;
            int eorzeaHour = eorzeaTime.Hour;

            // Kill Tracking Logic (Continuous)
            HashSet<ulong> currentObjects = new HashSet<ulong>();
            foreach (var obj in _plugin.SafeGameObjectManager)
            {
                currentObjects.Add(obj.GameObjectId);

                if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                {
                    bool isDead = obj.IsDead || (obj is GameObjectHelper.ThreadSafeDalamudObjectTable.ThreadSafeCharacter chara && chara.CurrentHp <= 0);
                    
                    if (!isDead)
                    {
                        _seenAliveEnemies.Add(obj.GameObjectId);
                    }
                    else if (_seenAliveEnemies.Contains(obj.GameObjectId) && !_knownDeadEnemies.Contains(obj.GameObjectId))
                    {
                        _knownDeadEnemies.Add(obj.GameObjectId);
                        
                        // Only count the kill if the enemy died within 30 yalms of the player
                        if (System.Numerics.Vector3.Distance(player.Position, obj.Position) < 30f)
                        {
                            _plugin.PluginLog.Information($"Contextual Layer: Kill registered for {obj.Name} ({obj.GameObjectId})");
                            ProcessKill();
                        }
                    }
                }
            }

            // Cleanup trackers for objects that have completely despawned
            _seenAliveEnemies.RemoveWhere(id => !currentObjects.Contains(id));
            _knownDeadEnemies.RemoveWhere(id => !currentObjects.Contains(id));

            bool layersChanged = false;

            foreach (var layer in ContextualLayers.Where(l => l.Enabled))
            {
                bool isConditionMet = false;
                int desiredStackForHp = 0;

                if (layer.Trigger == TriggerType.HP_Threshold)
                {
                    if (hpPercentage <= layer.HPThresholdPercentage)
                    {
                        isConditionMet = true;
                        var activeTemp = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
                        int numIntervals = activeTemp != null ? activeTemp.CachedTexturePaths.Count : 1;
                        if (numIntervals == 0) numIntervals = 1;
                        
                        float intervalSize = layer.HPThresholdPercentage / (float)numIntervals;
                        float hpBelowThreshold = layer.HPThresholdPercentage - hpPercentage;
                        desiredStackForHp = 1 + (int)(hpBelowThreshold / intervalSize);
                        desiredStackForHp = Math.Clamp(desiredStackForHp, 1, numIntervals);
                    }
                }
                else if (layer.Trigger == TriggerType.Combat_State)
                {
                    if (inCombat) isConditionMet = true;
                }
                else if (layer.Trigger == TriggerType.Weapon_Drawn)
                {
                    if (weaponDrawn) isConditionMet = true;
                }
                else if (layer.Trigger == TriggerType.Swimming_State)
                {
                    if (isSwimming) isConditionMet = true;
                }
                else if (layer.Trigger == TriggerType.Mounted_State)
                {
                    if (isMounted) isConditionMet = true;
                }
                else if (layer.Trigger == TriggerType.In_Game_Time)
                {
                    int start = layer.TargetTimeStartHour;
                    int end = layer.TargetTimeEndHour;
                    if (start <= end)
                    {
                        if (eorzeaHour >= start && eorzeaHour < end) isConditionMet = true;
                    }
                    else
                    {
                        if (eorzeaHour >= start || eorzeaHour < end) isConditionMet = true;
                    }
                }
                else if (layer.Trigger == TriggerType.Territory_ID)
                {
                    if (Plugin.ClientState.TerritoryType == layer.TargetTerritoryId) isConditionMet = true;
                }
                else if (layer.Trigger == TriggerType.Weather_ID)
                {
                    unsafe
                    {
                        var env = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager.Instance();
                        if (env != null && env->ActiveWeather == layer.TargetWeatherId) isConditionMet = true;
                    }
                }
                else if (layer.Trigger == TriggerType.Enemy_Nearby && !string.IsNullOrEmpty(layer.TargetEnemyName))
                {
                    foreach (var obj in _plugin.SafeGameObjectManager)
                    {
                        if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc && 
                            obj.Name.TextValue.Contains(layer.TargetEnemyName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (System.Numerics.Vector3.Distance(player.Position, obj.Position) < 30f)
                            {
                                isConditionMet = true;
                                break;
                            }
                        }
                    }
                }

                if (isConditionMet)
                {
                    var existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
                    if (existing == null)
                    {
                        _plugin.PluginLog.Information($"[Contextual Layers] Condition met for '{layer.Name}' (Trigger: {layer.Trigger}). Activating...");
                        ActivateLayer(layer);
                        existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
                        if (existing != null && layer.Trigger == TriggerType.HP_Threshold)
                        {
                             int numIntervals = existing.CachedTexturePaths.Count;
                             if (numIntervals == 0) numIntervals = 1;
                             float intervalSize = layer.HPThresholdPercentage / (float)numIntervals;
                             float hpBelowThreshold = layer.HPThresholdPercentage - hpPercentage;
                             desiredStackForHp = 1 + (int)(hpBelowThreshold / intervalSize);
                             desiredStackForHp = Math.Clamp(desiredStackForHp, 1, numIntervals);
                             existing.CurrentStackCount = desiredStackForHp;
                        }
                    }
                    else
                    {
                        // Keep the timer completely reset while the state condition is still met
                        existing.Timer.Restart();
                        if (layer.Trigger == TriggerType.HP_Threshold && existing.CurrentStackCount != desiredStackForHp)
                        {
                            existing.CurrentStackCount = desiredStackForHp;
                            layersChanged = true;
                            _plugin.PluginLog.Information($"[Contextual Layers] '{layer.Name}' HP Threshold adjusted stack count to {desiredStackForHp}.");
                        }
                        else if (layer.Trigger != TriggerType.HP_Threshold)
                        {
                            // Avoid spamming log every frame, but we could log state refresh here if necessary
                        }
                    }
                }
            }

            // 2. Process Layer Decay / Expirations
            for (int i = _activeLayers.Count - 1; i >= 0; i--)
            {
                var active = _activeLayers[i];

                if (!active.LayerDef.Enabled)
                {
                    _activeLayers.RemoveAt(i);
                    layersChanged = true;
                    continue;
                }

                if (active.LayerDef.ClearTrigger == ClearCondition.Swimming && isSwimming)
                {
                    _activeLayers.RemoveAt(i);
                    layersChanged = true;
                    _plugin.PluginLog.Information($"Contextual Layer '{active.LayerDef.Name}' washed off instantly.");
                    continue;
                }

                bool isStillActive = false;
                if (active.LayerDef.Trigger == TriggerType.HP_Threshold && hpPercentage <= active.LayerDef.HPThresholdPercentage) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.Combat_State && inCombat) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.Weapon_Drawn && weaponDrawn) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.Swimming_State && isSwimming) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.Mounted_State && isMounted) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.In_Game_Time)
                {
                    int start = active.LayerDef.TargetTimeStartHour;
                    int end = active.LayerDef.TargetTimeEndHour;
                    if (start <= end)
                    {
                        if (eorzeaHour >= start && eorzeaHour < end) isStillActive = true;
                    }
                    else
                    {
                        if (eorzeaHour >= start || eorzeaHour < end) isStillActive = true;
                    }
                }
                if (active.LayerDef.Trigger == TriggerType.Territory_ID && Plugin.ClientState.TerritoryType == active.LayerDef.TargetTerritoryId) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.Weather_ID)
                {
                    unsafe
                    {
                        var env = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager.Instance();
                        if (env != null && env->ActiveWeather == active.LayerDef.TargetWeatherId) isStillActive = true;
                    }
                }
                if (active.LayerDef.Trigger == TriggerType.Enemy_Nearby)
                {
                    foreach (var obj in _plugin.SafeGameObjectManager)
                    {
                        if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc && 
                            obj.Name.TextValue.Contains(active.LayerDef.TargetEnemyName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (System.Numerics.Vector3.Distance(player.Position, obj.Position) < 30f)
                            {
                                isStillActive = true;
                                break;
                            }
                        }
                    }
                }
                
                if (active.LayerDef.Trigger == TriggerType.Kill_Count || active.LayerDef.Trigger == TriggerType.Action_Used)
                {
                    if (active.LayerDef.ClearTrigger == ClearCondition.Time && active.LayerDef.DecayIntervalSeconds > 0 && active.Timer.Elapsed.TotalSeconds >= active.LayerDef.DecayIntervalSeconds)
                    {
                        active.CurrentStackCount--;
                        if (active.CurrentStackCount <= 0)
                        {
                            _activeLayers.RemoveAt(i);
                            layersChanged = true;
                            _plugin.PluginLog.Information($"Contextual Layer '{active.LayerDef.Name}' completely decayed.");
                        }
                        else
                        {
                            active.Timer.Restart();
                            layersChanged = true;
                            _plugin.PluginLog.Information($"Contextual Layer '{active.LayerDef.Name}' decayed one stack. Stacks remaining: {active.CurrentStackCount}");
                            TriggerHotswapRebuild();
                        }
                    }
                    continue;
                }

                if (active.LayerDef.ClearTrigger == ClearCondition.Time && !isStillActive && active.Timer.Elapsed.TotalSeconds >= active.LayerDef.DurationSeconds)
                {
                    // Expired!
                    _activeLayers.RemoveAt(i);
                    layersChanged = true;
                    _plugin.PluginLog.Information($"Contextual Layer '{active.LayerDef.Name}' expired.");
                }
            }

            if (layersChanged)
            {
                TriggerHotswapRebuild();
            }
        }

        private void UpdateProceduralDecal(ActiveContextualLayer active)
        {
            if (!active.LayerDef.ProceduralDecalMode) return;

            var files = System.IO.Directory.GetFiles(active.LayerDef.DirectoryPath, "*.png")
                .Where(f => !f.Contains("_temp", StringComparison.OrdinalIgnoreCase) && 
                            !f.Contains("_from_bibo_to_gen3", StringComparison.OrdinalIgnoreCase) && 
                            !f.Contains("_from_gen3_to_bibo", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (active.CurrentStackCount > 20) active.CurrentStackCount = 20;
            var stampCount = 1; // Since the paint layer is persistent, we only add 1 new stamp per kill

            // Get or create the hidden TexturePaintingWindow for procedural decals
            var paintWindow = GetOrCreateHeadlessPaintWindow();
            if (paintWindow == null)
            {
                _plugin.PluginLog.Warning("[ContextualLayerManager] Failed to get headless paint window for procedural decals.");
                return;
            }

            string uvType = "";
            if (files.Count > 0)
            {
                string firstFile = System.IO.Path.GetFileNameWithoutExtension(files[0]).ToLower();
                if (firstFile.Contains("bibo") || firstFile.Contains("b+")) uvType = "bibo";
                else if (firstFile.Contains("gen3")) uvType = "gen3";
                else if (firstFile.Contains("tbse")) uvType = "tbse";
                else if (firstFile.Contains("gen2")) uvType = "gen2";
                else if (firstFile.Contains("otopop")) uvType = "otopop";
            }

            // Queue stamps — they'll be processed during the next Draw() on the ImGui/D3D11 thread
            paintWindow.QueueProceduralStamps(files, stampCount, active.LayerDef.TargetBodyPart, uvType, (tempFile) =>
            {
                if (tempFile != null)
                {
                    _mainThreadActions.Enqueue(() => {
                        if (active.CachedTexturePaths.Count == 1 && active.CachedTexturePaths[0].Contains("temp_decal_"))
                        {
                            try { System.IO.File.Delete(active.CachedTexturePaths[0]); } catch { }
                        }
                        active.CachedTexturePaths = new List<string> { tempFile };
                        _plugin.Configuration.PersistedProceduralCanvasPath = tempFile;
                        _plugin.Configuration.Save();
                        TriggerHotswapRebuild();
                    });
                }
            });
        }

        private Windows.TexturePaintingWindow _headlessPaintWindow = null;

        private Windows.TexturePaintingWindow GetOrCreateHeadlessPaintWindow()
        {
            if (_headlessPaintWindow != null && _headlessPaintWindow.IsOpen) return _headlessPaintWindow;

            var window = new Windows.TexturePaintingWindow(_plugin);
            window.WindowName = $"Texture Painter (Headless Debug)###HeadlessPainter_{Guid.NewGuid()}";
            window.IsHeadlessMode = true;
            window.IsOpen = true;
            _plugin.TexturePaintingWindows.Add(window);
            _plugin.WindowSystem.AddWindow(window);
            _headlessPaintWindow = window;
            return window;
        }

        private void ActivateLayer(ContextualLayer layer)
        {
            var existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
            if (existing != null)
            {
                _plugin.PluginLog.Information($"[Contextual Layers] '{layer.Name}' is already active. Processing stack increment or timer refresh...");
                existing.Timer.Restart();
                if (layer.Trigger == TriggerType.Emote || layer.Trigger == TriggerType.Chat_Message)
                {
                    existing.CurrentStackCount++;
                    if (existing.CurrentStackCount > existing.CachedTexturePaths.Count && !layer.ProceduralDecalMode)
                    {
                        existing.CurrentStackCount = existing.CachedTexturePaths.Count;
                    }
                    if (layer.ProceduralDecalMode) 
                    {
                        UpdateProceduralDecal(existing);
                    }
                    else
                    {
                        TriggerHotswapRebuild();
                    }
                }
                else if (layer.Trigger == TriggerType.Audio_Path_Load)
                {
                    existing.SoundsSinceLastStack++;
                    if (existing.SoundsSinceLastStack >= layer.RequiredSoundsPerStack)
                    {
                        existing.SoundsSinceLastStack = 0;
                        if (existing.CurrentStackCount < existing.CachedTexturePaths.Count || layer.ProceduralDecalMode)
                        {
                            existing.CurrentStackCount++;
                            existing.Timer.Restart();
                            if (layer.ProceduralDecalMode) 
                            {
                                UpdateProceduralDecal(existing);
                            }
                            else
                            {
                                TriggerHotswapRebuild();
                            }
                        }
                        else
                        {
                            existing.Timer.Restart();
                        }
                    }
                }
            }
            else
            {
                _plugin.PluginLog.Information($"[Contextual Layers] Activating Contextual Layer '{layer.Name}' for the first time!");
                var active = new ActiveContextualLayer { LayerDef = layer };
                
                if (System.IO.Directory.Exists(layer.DirectoryPath))
                {
                    var files = System.IO.Directory.GetFiles(layer.DirectoryPath, "*.png")
                        .Where(f => !f.Contains("_temp", StringComparison.OrdinalIgnoreCase) && 
                                    !f.Contains("_from_bibo_to_gen3", StringComparison.OrdinalIgnoreCase) && 
                                    !f.Contains("_from_gen3_to_bibo", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    files.Sort();
                    active.CachedTexturePaths = files;
                }
                
                active.CurrentStackCount = 1;
                active.Timer.Start();
                _activeLayers.Add(active);
                
                if (layer.ProceduralDecalMode) 
                {
                    UpdateProceduralDecal(active);
                }
                else
                {
                    TriggerHotswapRebuild();
                }
            }
        }

        private void TriggerHotswapRebuild()
        {
            // Debounce: cancel any pending rebuild and restart the timer.
            // This collapses rapid kills into a single ScheduleRegeneration call.
            lock (_debounceLock)
            {
                _rebuildDebounce?.Cancel();
                _rebuildDebounce = new System.Threading.CancellationTokenSource();
                var token = _rebuildDebounce.Token;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500, token);
                        if (!token.IsCancellationRequested)
                        {
                            _mainThreadActions.Enqueue(() => ExecuteHotswapRebuild());
                        }
                    }
                    catch (TaskCanceledException) { }
                });
            }
        }

        private void ExecuteHotswapRebuild()
        {
            _plugin.PluginLog.Information("Contextual Layer state changed. Hotswap Rebuild triggered.");
            
            if (_plugin.DragAndDropTextures != null && _plugin.SafeGameObjectManager.LocalPlayer != null)
            {
                var charName = _plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                // Only rebuild body parts targeted by active contextual layers
                // plus any parts that WERE previously targeted (so they get cleaned up on removal)
                var partsToUpdate = new HashSet<string>();
                foreach (var active in _activeLayers)
                {
                    string part = "_" + active.LayerDef.TargetBodyPart.ToLower();
                    partsToUpdate.Add(part);
                    _contextuallyTouchedParts.Add(part);
                }
                // Include parts that were previously touched but no longer have active layers
                // (needed to rebuild them clean after a layer is removed)
                foreach (var previousPart in _contextuallyTouchedParts)
                {
                    partsToUpdate.Add(previousPart);
                }
                // Clean up: remove parts that no longer have active layers
                _contextuallyTouchedParts.RemoveWhere(p => !_activeLayers.Any(a => "_" + a.LayerDef.TargetBodyPart.ToLower() == p));

                foreach (var part in partsToUpdate)
                {
                    string categoryKey = charName + part;
                    if (!_plugin.DragAndDropTextures.TextureHistory.ContainsKey(categoryKey))
                    {
                        _plugin.DragAndDropTextures.TextureHistory[categoryKey] = new List<string>();
                    }
                }
                
                _plugin.DragAndDropTextures.ScheduleRegeneration(charName, partsToUpdate.ToArray(), skipDelays: true);
            }
        }

        private void ProcessKill()
        {
            var player = _plugin.SafeGameObjectManager.LocalPlayer;
            if (player == null) return;

            foreach (var layer in ContextualLayers.Where(l => l.Enabled))
            {
                if (layer.Trigger == TriggerType.Kill_Count || layer.Trigger == TriggerType.Action_Used)
                {
                    var existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
                    if (existing == null)
                    {
                        ActivateLayer(layer);
                        existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
                    }
                    if (existing != null)
                    {
                        existing.KillsSinceLastStack++;
                        _plugin.PluginLog.Information($"[Contextual Layers] '{layer.Name}' tracking progress: {existing.KillsSinceLastStack}/{layer.RequiredKillsPerStack}");
                        if (existing.KillsSinceLastStack >= layer.RequiredKillsPerStack)
                        {
                            existing.KillsSinceLastStack = 0;
                            if (existing.CurrentStackCount < existing.CachedTexturePaths.Count || layer.ProceduralDecalMode)
                            {
                                existing.CurrentStackCount++;
                                _plugin.PluginLog.Information($"[Contextual Layers] '{layer.Name}' threshold reached! Stack increased to {existing.CurrentStackCount}. Rebuilding...");
                                existing.Timer.Restart();
                                if (layer.ProceduralDecalMode) UpdateProceduralDecal(existing);
                                TriggerHotswapRebuild();
                            }
                            else
                            {
                                // Restart timer even if maxed out so it doesn't decay
                                existing.Timer.Restart();
                            }
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            SavePersistedStates();
            if (_emoteReader != null)
            {
                _emoteReader.OnEmote -= OnEmote;
            }
            if (_audioReader != null)
            {
                _audioReader.OnSoundPlayed -= OnSoundPlayed;
            }
            Plugin.Framework.Update -= Framework_Update;
            _plugin.Chat.ChatMessage -= OnChatMessage;
        }
    }
}
