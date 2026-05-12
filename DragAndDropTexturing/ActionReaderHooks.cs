using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace DragAndDropTexturing {
    public unsafe class ActionReaderHooks : IDisposable {
        public Action<uint> OnActionUsed;

        public delegate bool UseActionDelegate(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint extraParam, uint mode, uint comboRouteID, bool* outOptOut);
        private readonly Hook<UseActionDelegate> hookUseAction;

        public bool IsValid = false;

        public ActionReaderHooks(IGameInteropProvider interopProvider) {
            try {
                hookUseAction = interopProvider.HookFromAddress<UseActionDelegate>((nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour);
                hookUseAction.Enable();
                IsValid = true;
            } catch (Exception ex) {
            }
        }

        public void Dispose() {
            hookUseAction?.Dispose();
            IsValid = false;
        }

        private bool UseActionDetour(ActionManager* actionManager, ActionType actionType, uint actionID, ulong targetID, uint extraParam, uint mode, uint comboRouteID, bool* outOptOut) {
            try {
                if (actionType == ActionType.Action) {
                    OnActionUsed?.Invoke(actionID);
                }
            } catch { }
            return hookUseAction.Original(actionManager, actionType, actionID, targetID, extraParam, mode, comboRouteID, outOptOut);
        }
    }
}
