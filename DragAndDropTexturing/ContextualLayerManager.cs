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
    }

    public class ContextualLayerManager : IDisposable
    {
        private Plugin _plugin;
        private EmoteReaderHooks _emoteReader;
        
        // Track which layers are currently active
        private List<ActiveContextualLayer> _activeLayers = new List<ActiveContextualLayer>();
        
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
                    if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
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
            bool layersChanged = false;
            for (int i = _activeLayers.Count - 1; i >= 0; i--)
            {
                var active = _activeLayers[i];

                bool isStillActive = false;
                if (active.LayerDef.Trigger == TriggerType.HP_Threshold && hpPercentage <= active.LayerDef.HPThresholdPercentage) isStillActive = true;
                if (active.LayerDef.Trigger == TriggerType.Combat_State && Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]) isStillActive = true;

                if (!isStillActive && active.Timer.Elapsed.TotalSeconds >= active.LayerDef.DurationSeconds)
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
