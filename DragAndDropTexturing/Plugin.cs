using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DragAndDropTexturing.Windows;
using RoleplayingVoice;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using DragAndDropTexturing.ThreadSafeDalamudObjectTable;

namespace DragAndDropTexturing;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string CommandName = "/ddt";
    private PenumbraAndGlamourerIpcWrapper _penumbraAndGlamourerIpcWrapper;
    private IChatGui _chat;
    private int _playerCount;
    private ThreadSafeGameObjectManager _safeGameObjectManager;
    private IPluginLog _pluginLog;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("DragAndDropTexturing");
    private MainWindow MainWindow { get; init; }
    internal DragAndDropTextureWindow? DragAndDropTextures { get; private set; }
    public IChatGui Chat { get => _chat; set => _chat = value; }
    public ThreadSafeGameObjectManager SafeGameObjectManager { get => _safeGameObjectManager; set => _safeGameObjectManager = value; }
    public IPluginLog PluginLog { get => _pluginLog; set => _pluginLog = value; }

    public Plugin(IClientState clientState, IChatGui chatGui, IObjectTable objectTable, IFramework framework, IPluginLog pluginLog)
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


        // Adds another button that is doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [SamplePlugin] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
        if (DragAndDropTextures is not null)
        {
            WindowSystem.AddWindow(DragAndDropTextures);
            DragAndDropTextures.Plugin = this;
            DragAndDropTextures.IsOpen = true;
        }
        _safeGameObjectManager = new ThreadSafeGameObjectManager(clientState, objectTable, framework, pluginLog);
        _pluginLog = pluginLog;
    }
    public Dalamud.Game.ClientState.Objects.Types.IGameObject[] GetNearestObjects()
    {
        _playerCount = 0;
        List<Dalamud.Game.ClientState.Objects.Types.IGameObject> gameObjects = new List<Dalamud.Game.ClientState.Objects.Types.IGameObject>();
        foreach (var item in ThreadSafeGameObjectManager.SafeGameObjectTable.Values)
        {
            if (Vector3.Distance(SafeGameObjectManager.LocalPlayer.Position, item.Position) < 3f
                && item.GameObjectId != SafeGameObjectManager.LocalPlayer.GameObjectId)
            {
                if (item.IsValid())
                {
                    gameObjects.Add((item as Dalamud.Game.ClientState.Objects.Types.IGameObject));
                }
            }
            if (item.ObjectKind == ObjectKind.Player)
            {
                _playerCount++;
            }
        }
        return gameObjects.ToArray();
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

        DragAndDropTextures?.Dispose();
        MainWindow?.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // in response to the slash command, just toggle the display status of our main ui
        ToggleMainUI();
    }

    private void DrawUI() => WindowSystem.Draw();

    public void ToggleMainUI() => MainWindow.Toggle();
}
