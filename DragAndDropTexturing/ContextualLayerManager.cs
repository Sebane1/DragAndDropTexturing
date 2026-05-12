using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DragAndDropTexturing
{
    public class ActiveContextualLayer
    {
        public ContextualLayer LayerDef;
        public Stopwatch Timer = new Stopwatch();
        public int CurrentStackCount = 0;
        public int KillsSinceLastStack = 0;
        public List<string> CachedTexturePaths = new List<string>();
    }

    public class ContextualLayerManager : IDisposable
    {
        private Plugin _plugin;
        private EmoteReaderHooks _emoteReader;
        
        // Track which layers are currently active
        private List<ActiveContextualLayer> _activeLayers = new List<ActiveContextualLayer>();
        private HashSet<ulong> _knownDeadEnemies = new HashSet<ulong>();
        private HashSet<ulong> _seenAliveEnemies = new HashSet<ulong>();
        
        public List<ActiveContextualLayer> GetActiveLayers() => _activeLayers;

        public ContextualLayerManager(Plugin plugin, EmoteReaderHooks emoteReader)
        {
            _plugin = plugin;
            _emoteReader = emoteReader;
            
            if (_emoteReader != null)
            {
                _emoteReader.OnEmote += OnEmote;
            }
            
            Plugin.Framework.Update += Framework_Update;
            Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
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
        }

        private void OnEmote(IGameObject instigator, ushort emoteId)
        {
            if (instigator == null) return;
            
            var player = _plugin.SafeGameObjectManager.LocalPlayer;
            if (player == null) return;
            if (instigator.Address != player.Address) return;

            foreach (var layer in _plugin.Configuration.ContextualLayers)
            {
                if (layer.Trigger == TriggerType.Emote && layer.EmoteId == emoteId)
                {
                    ActivateLayer(layer);
                }
            }
        }

        private void Framework_Update(Dalamud.Plugin.Services.IFramework framework)
        {
            var player = _plugin.SafeGameObjectManager.LocalPlayer as GameObjectHelper.ThreadSafeDalamudObjectTable.ThreadSafeCharacter;
            if (player == null) return;

            // 1. Check HP Thresholds and Combat State
            float hpPercentage = 100f;
            if (player.MaxHp > 0)
            {
                hpPercentage = ((float)player.CurrentHp / (float)player.MaxHp) * 100f;
            }
            
            bool inCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];

            // Kill Tracking Logic (Continuous)
            HashSet<ulong> currentObjects = new HashSet<ulong>();
            foreach (var obj in _plugin.SafeGameObjectManager)
            {
                currentObjects.Add(obj.GameObjectId);

                if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc && obj.SubKind == (byte)5)
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

            foreach (var layer in _plugin.Configuration.ContextualLayers)
            {
                bool isConditionMet = false;

                if (layer.Trigger == TriggerType.HP_Threshold)
                {
                    if (hpPercentage <= layer.HPThresholdPercentage)
                    {
                        isConditionMet = true;
                    }
                }
                else if (layer.Trigger == TriggerType.Combat_State)
                {
                    if (inCombat)
                    {
                        isConditionMet = true;
                    }
                }

                if (isConditionMet)
                {
                    var existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
                    if (existing == null)
                    {
                        ActivateLayer(layer);
                    }
                    else
                    {
                        // Keep the timer completely reset while the state condition is still met
                        existing.Timer.Restart();
                    }
                }
            }

            // 2. Process Layer Decay / Expirations
            bool isSwimming = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Swimming] || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving];
            bool layersChanged = false;
            for (int i = _activeLayers.Count - 1; i >= 0; i--)
            {
                var active = _activeLayers[i];

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
                
                if (active.LayerDef.Trigger == TriggerType.Kill_Count)
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

        private void ActivateLayer(ContextualLayer layer)
        {
            var existing = _activeLayers.FirstOrDefault(x => x.LayerDef == layer);
            if (existing != null)
            {
                existing.Timer.Restart();
            }
            else
            {
                _plugin.PluginLog.Information($"Activating Contextual Layer '{layer.Name}'!");
                var active = new ActiveContextualLayer { LayerDef = layer };
                
                if (layer.Trigger == TriggerType.Kill_Count && !string.IsNullOrEmpty(layer.TextureDirectoryPath) && System.IO.Directory.Exists(layer.TextureDirectoryPath))
                {
                    var files = System.IO.Directory.GetFiles(layer.TextureDirectoryPath, "*.png").ToList();
                    files.Sort();
                    active.CachedTexturePaths = files;
                }
                else if (!string.IsNullOrEmpty(layer.TexturePath))
                {
                    active.CachedTexturePaths.Add(layer.TexturePath);
                }
                
                active.Timer.Start();
                _activeLayers.Add(active);
                
                TriggerHotswapRebuild();
            }
        }

        private void TriggerHotswapRebuild()
        {
            _plugin.PluginLog.Information("Contextual Layer state changed. Hotswap Rebuild triggered.");
            
            if (_plugin.DragAndDropTextures != null && _plugin.SafeGameObjectManager.LocalPlayer != null)
            {
                var charName = _plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                // Rebuild for each active body part category we've touched
                List<string> partsToUpdate = new List<string> { "body", "face", "eyes", "eyebrows" };
                foreach (var part in partsToUpdate)
                {
                    string categoryKey = charName + "_" + part;
                    if (!_plugin.DragAndDropTextures.TextureHistory.ContainsKey(categoryKey))
                    {
                        _plugin.DragAndDropTextures.TextureHistory[categoryKey] = new List<string>();
                    }
                    _plugin.DragAndDropTextures.RebuildCategory(categoryKey);
                }
            }
        }

        private void ProcessKill()
        {
            var player = _plugin.SafeGameObjectManager.LocalPlayer;
            if (player == null) return;

            foreach (var layer in _plugin.Configuration.ContextualLayers)
            {
                if (layer.Trigger == TriggerType.Kill_Count)
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
                        if (existing.KillsSinceLastStack >= layer.RequiredKillsPerStack)
                        {
                            existing.KillsSinceLastStack = 0;
                            if (existing.CurrentStackCount < existing.CachedTexturePaths.Count)
                            {
                                existing.CurrentStackCount++;
                                existing.Timer.Restart();
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
            if (_emoteReader != null)
            {
                _emoteReader.OnEmote -= OnEmote;
            }
            Plugin.Framework.Update -= Framework_Update;
        }
    }
}
