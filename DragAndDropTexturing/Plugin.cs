using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DragAndDropTexturing.Windows;
using RoleplayingVoice;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using GameObjectHelper.ThreadSafeDalamudObjectTable;
using FFXIVLooseTextureCompiler.Export;

namespace DragAndDropTexturing;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static Dalamud.Interface.DragDrop.IDragDropManager DragDropManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/ddt";
    private PenumbraAndGlamourerIpcWrapper _penumbraAndGlamourerIpcWrapper;
    private IChatGui _chat;
    private int _playerCount;
    private ThreadSafeGameObjectManager _safeGameObjectManager;
    private IPluginLog _pluginLog;
    private EmoteReaderHooks _emoteReaderHooks;
    private ActionReaderHooks _actionReaderHooks;
    private AudioReaderHooks _audioReaderHooks;
    private ContextualLayerManager _contextualLayerManager;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("DragAndDropTexturing");
    private MainWindow MainWindow { get; init; }
    public PsdImportWindow PsdImportWindow { get; init; }
    internal DragAndDropTextureWindow? DragAndDropTextures { get; private set; }
    public IChatGui Chat { get => _chat; set => _chat = value; }
    public ThreadSafeGameObjectManager SafeGameObjectManager { get => _safeGameObjectManager; set => _safeGameObjectManager = value; }
    public IPluginLog PluginLog { get => _pluginLog; set => _pluginLog = value; }
    public ContextualLayerManager ContextualLayerManager => _contextualLayerManager;

    public Plugin(IClientState clientState, IChatGui chatGui, IObjectTable objectTable, IFramework framework, IPluginLog pluginLog, IGameInteropProvider gameInteropProvider)
    {
        _penumbraAndGlamourerIpcWrapper = new PenumbraAndGlamourerIpcWrapper(PluginInterface);
        _chat = chatGui;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        DragAndDropTextures = PluginInterface.Create<DragAndDropTextureWindow>();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
        if (DragAndDropTextures is not null)
        {
            WindowSystem.AddWindow(DragAndDropTextures);
            DragAndDropTextures.Plugin = this;
            DragAndDropTextures.IsOpen = true;
        }
        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);
        PsdImportWindow = new PsdImportWindow(this);
        WindowSystem.AddWindow(PsdImportWindow);
        _safeGameObjectManager = new ThreadSafeGameObjectManager(clientState, objectTable, framework, pluginLog);
        _pluginLog = pluginLog;
        BackupTexturePaths.OverrideMode = Configuration.UsePriorityBodyMod;

        try
        {
            _emoteReaderHooks = new EmoteReaderHooks(gameInteropProvider, clientState, _safeGameObjectManager);
            _actionReaderHooks = new ActionReaderHooks(gameInteropProvider);
            _audioReaderHooks = new AudioReaderHooks(gameInteropProvider, SigScanner);
            _contextualLayerManager = new ContextualLayerManager(this, _emoteReaderHooks, _actionReaderHooks, _audioReaderHooks);
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "Failed to initialize ContextualLayerManager, EmoteReaderHooks, or ActionReaderHooks");
        }
    }
    public Dalamud.Game.ClientState.Objects.Types.IGameObject[] GetNearestObjects()
    {
        _playerCount = 0;
        List<Dalamud.Game.ClientState.Objects.Types.IGameObject> gameObjects = new List<Dalamud.Game.ClientState.Objects.Types.IGameObject>();
        foreach (var item in _safeGameObjectManager)
        {
            if (Vector3.Distance(SafeGameObjectManager.LocalPlayer.Position, item.Position) < 3f
                && item.GameObjectId != SafeGameObjectManager.LocalPlayer.GameObjectId)
            {
                    gameObjects.Add((item as Dalamud.Game.ClientState.Objects.Types.IGameObject));
            }
            if (item.ObjectKind == ObjectKind.Pc)
            {
                _playerCount++;
            }
        }
        return gameObjects.ToArray();
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

        _contextualLayerManager?.Dispose();
        _emoteReaderHooks?.Dispose();
        _actionReaderHooks?.Dispose();
        _audioReaderHooks?.Dispose();
        DragAndDropTextures?.Dispose();
        MainWindow?.Dispose();
        PsdImportWindow?.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // in response to the slash command, just toggle the display status of our main ui
        ToggleMainUI();
    }

    private void DrawUI() => WindowSystem.Draw();

    public void ToggleMainUI()
    {
        DragAndDropTextures?.RefreshActiveOverrides();
        MainWindow.Toggle();
    }
}
