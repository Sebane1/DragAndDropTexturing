using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DragAndDropTexturing.VideoPlayback;
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
    [PluginService] public static Dalamud.Plugin.Services.IObjectTable ObjectTable { get; private set; } = null!;
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
    private AnimatedLayerManager _animatedLayerManager;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("DragAndDropTexturing");
    public MainWindow MainWindow { get; init; }
    public PsdImportWindow PsdImportWindow { get; init; }
    public MdlPreviewWindow MdlPreviewWindow { get; init; }
    public List<TexturePaintingWindow> TexturePaintingWindows { get; init; } = new();

    public void OpenPaintWindow(string editPath = null, string categoryKey = null)
    {
        if (editPath != null)
        {
            foreach (var existingWindow in TexturePaintingWindows)
            {
                if (string.Equals(existingWindow.EditSourcePath, editPath, StringComparison.OrdinalIgnoreCase))
                {
                    existingWindow.IsOpen = true;
                    existingWindow.BringToFront();
                    return;
                }
            }
        }

        var window = new TexturePaintingWindow(this);
        window.ContextCategoryKey = categoryKey;
        if (editPath != null)
        {
            window.WindowName = $"Texture Painter - {Path.GetFileName(editPath)}###PaintWindow_{Guid.NewGuid()}";
            window.OpenForEditing(editPath, categoryKey);
        }
        else
        {
            window.WindowName = $"Texture Painter###PaintWindow_{Guid.NewGuid()}";
            window.IsOpen = true;
        }
        TexturePaintingWindows.Add(window);
        WindowSystem.AddWindow(window);
    }
    internal DragAndDropTextureWindow? DragAndDropTextures { get; private set; }
    public IChatGui Chat { get => _chat; set => _chat = value; }
    public ThreadSafeGameObjectManager SafeGameObjectManager { get => _safeGameObjectManager; set => _safeGameObjectManager = value; }
    public IPluginLog PluginLog { get => _pluginLog; set => _pluginLog = value; }
    public ContextualLayerManager ContextualLayerManager => _contextualLayerManager;
    public AnimatedLayerManager AnimatedLayerManager => _animatedLayerManager;

    public Plugin(IClientState clientState, IChatGui chatGui, IObjectTable objectTable, IFramework framework, IPluginLog pluginLog, IGameInteropProvider gameInteropProvider)
    {
        _pluginLog = pluginLog;
        _penumbraAndGlamourerIpcWrapper = new PenumbraAndGlamourerIpcWrapper(PluginInterface);
        _chat = chatGui;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        RunMigrations();
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
        MdlPreviewWindow = new MdlPreviewWindow();
        WindowSystem.AddWindow(MdlPreviewWindow);
        // Painting windows are now spawned dynamically via OpenPaintWindow()
        _safeGameObjectManager = new ThreadSafeGameObjectManager(clientState, objectTable, framework, pluginLog);
        BackupTexturePaths.OverrideMode = Configuration.UsePriorityBodyMod;

        if (Configuration.LanguageOverride >= 0)
        {
            DragAndDropTexturing.LanguageHelpers.Translator.UiLanguage = (DragAndDropTexturing.LanguageHelpers.LanguageEnum)Configuration.LanguageOverride;
        }
        else
        {
            DragAndDropTexturing.LanguageHelpers.Translator.UiLanguage = clientState.ClientLanguage switch
            {
                Dalamud.Game.ClientLanguage.Japanese => DragAndDropTexturing.LanguageHelpers.LanguageEnum.Japanese,
                Dalamud.Game.ClientLanguage.French => DragAndDropTexturing.LanguageHelpers.LanguageEnum.French,
                Dalamud.Game.ClientLanguage.German => DragAndDropTexturing.LanguageHelpers.LanguageEnum.German,
                _ => DragAndDropTexturing.LanguageHelpers.LanguageEnum.English,
            };
        }
        DragAndDropTexturing.LanguageHelpers.Translator.LoadCache(Path.Combine(PluginInterface.ConfigDirectory.FullName, "translation_cache.json"));

        try
        {
            _emoteReaderHooks = new EmoteReaderHooks(gameInteropProvider, clientState, _safeGameObjectManager);
            _actionReaderHooks = new ActionReaderHooks(gameInteropProvider);
            _audioReaderHooks = new AudioReaderHooks(gameInteropProvider, SigScanner);
            _contextualLayerManager = new ContextualLayerManager(this, _emoteReaderHooks, _actionReaderHooks, _audioReaderHooks);
            _animatedLayerManager = new AnimatedLayerManager(this);
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "Failed to initialize ContextualLayerManager, EmoteReaderHooks, or ActionReaderHooks");
        }

        try
        {
            string vfsPath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "vfs.dat");
            //FFXIVLooseTextureCompiler.ImageProcessing.TexIO.LoadVFS(vfsPath);
        }
        catch (Exception ex)
        {
            pluginLog.Error(ex, "Failed to load VFS.");
        }
    }

    private void RunMigrations()
    {
        try
        {
            string configDir = PluginInterface.ConfigDirectory.FullName;
            string oldExports = Path.Combine(configDir, "ContextualLayers", "Exports");
            string oldSavedOverlays = Path.Combine(configDir, "ContextualLayers", "SavedOverlays");
            
            string newExports = Path.Combine(configDir, "Exports");
            string newSavedOverlays = Path.Combine(configDir, "SavedOverlays");

            if (Directory.Exists(oldExports))
            {
                if (!Directory.Exists(newExports)) Directory.CreateDirectory(newExports);
                foreach (var file in Directory.GetFiles(oldExports))
                {
                    string dest = Path.Combine(newExports, Path.GetFileName(file));
                    if (!File.Exists(dest)) File.Move(file, dest);
                }
                try { Directory.Delete(oldExports, false); } catch { }
            }

            if (Directory.Exists(oldSavedOverlays))
            {
                if (!Directory.Exists(newSavedOverlays)) Directory.CreateDirectory(newSavedOverlays);
                foreach (var file in Directory.GetFiles(oldSavedOverlays))
                {
                    string dest = Path.Combine(newSavedOverlays, Path.GetFileName(file));
                    if (!File.Exists(dest)) File.Move(file, dest);
                }
                try { Directory.Delete(oldSavedOverlays, false); } catch { }
            }

            // Migrate paths in Configuration
            bool configUpdated = false;
            string target1A = "ContextualLayers\\SavedOverlays";
            string target1B = "ContextualLayers/SavedOverlays";
            string rep1 = "SavedOverlays";
            string target2A = "ContextualLayers\\Exports";
            string target2B = "ContextualLayers/Exports";
            string rep2 = "Exports";

            if (Configuration != null)
            {
                if (Configuration.TextureHistory != null)
                {
                    foreach (var list in Configuration.TextureHistory.Values)
                    {
                        if (list == null) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (!string.IsNullOrEmpty(list[i]))
                            {
                                if (list[i].Contains(target1A, StringComparison.OrdinalIgnoreCase) || 
                                    list[i].Contains(target1B, StringComparison.OrdinalIgnoreCase) || 
                                    list[i].Contains(target2A, StringComparison.OrdinalIgnoreCase) || 
                                    list[i].Contains(target2B, StringComparison.OrdinalIgnoreCase))
                                {
                                    list[i] = list[i].Replace(target1A, rep1, StringComparison.OrdinalIgnoreCase)
                                                     .Replace(target1B, rep1, StringComparison.OrdinalIgnoreCase)
                                                     .Replace(target2A, rep2, StringComparison.OrdinalIgnoreCase)
                                                     .Replace(target2B, rep2, StringComparison.OrdinalIgnoreCase);
                                    configUpdated = true;
                                }
                            }
                        }
                    }
                }

                if (Configuration.RecentLayers != null)
                {
                    for (int i = 0; i < Configuration.RecentLayers.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(Configuration.RecentLayers[i]))
                        {
                            if (Configuration.RecentLayers[i].Contains(target1A, StringComparison.OrdinalIgnoreCase) || 
                                Configuration.RecentLayers[i].Contains(target1B, StringComparison.OrdinalIgnoreCase) || 
                                Configuration.RecentLayers[i].Contains(target2A, StringComparison.OrdinalIgnoreCase) || 
                                Configuration.RecentLayers[i].Contains(target2B, StringComparison.OrdinalIgnoreCase))
                            {
                                Configuration.RecentLayers[i] = Configuration.RecentLayers[i].Replace(target1A, rep1, StringComparison.OrdinalIgnoreCase)
                                                                                             .Replace(target1B, rep1, StringComparison.OrdinalIgnoreCase)
                                                                                             .Replace(target2A, rep2, StringComparison.OrdinalIgnoreCase)
                                                                                             .Replace(target2B, rep2, StringComparison.OrdinalIgnoreCase);
                                configUpdated = true;
                            }
                        }
                    }
                }

                if (configUpdated)
                {
                    Configuration.Save();
                }
            }
        }
        catch (Exception ex)
        {
            _pluginLog?.Error(ex, "Failed to run directory migrations.");
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

        _animatedLayerManager?.Shutdown();
        _contextualLayerManager?.Dispose();
        _emoteReaderHooks?.Dispose();
        _actionReaderHooks?.Dispose();
        _audioReaderHooks?.Dispose();
        DragAndDropTextures?.Dispose();
        MainWindow?.Dispose();
        PsdImportWindow?.Dispose();
        foreach (var window in TexturePaintingWindows)
        {
            window.Dispose();
        }
        TexturePaintingWindows.Clear();
        CommandManager.RemoveHandler(CommandName);

        try
        {
            string vfsPath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "vfs.dat");
            //FFXIVLooseTextureCompiler.ImageProcessing.TexIO.SaveVFS(vfsPath);
        }
        catch (Exception ex)
        {
            _pluginLog.Error(ex, "Failed to save VFS.");
        }
    }

    private Dictionary<string, bool> _bodyAvailabilityCache = new Dictionary<string, bool>();
    private DateTime _lastBodyAvailabilityCheck = DateTime.MinValue;

    public bool IsBodyAvailable(string targetKeyword)
    {
        try
        {
            if ((DateTime.Now - _lastBodyAvailabilityCheck).TotalSeconds > 10)
            {
                _bodyAvailabilityCache.Clear();
                _lastBodyAvailabilityCheck = DateTime.Now;
            }

            if (_bodyAvailabilityCache.TryGetValue(targetKeyword, out bool available))
            {
                return available;
            }

            string trueRaceCode = targetKeyword == "tbse" ? "c0101" : "c0201";
            string relativeTop = $"chara/equipment/e0279/model/{trueRaceCode}e0279_top.mdl";
            string foundPath = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.FindMeshDiskPathInModDirectory(targetKeyword, relativeTop);
            
            bool isAvailable = !string.IsNullOrEmpty(foundPath);
            _bodyAvailabilityCache[targetKeyword] = isAvailable;
            return isAvailable;
        }
        catch { }
        return false;
    }

    private void OnCommand(string command, string args)
    {
        // in response to the slash command, just toggle the display status of our main ui
        ToggleMainUI();
    }

    private void DrawUI()
    {
        WindowSystem.Draw();

        for (int i = TexturePaintingWindows.Count - 1; i >= 0; i--)
        {
            var window = TexturePaintingWindows[i];
            if (!window.IsOpen)
            {
                WindowSystem.RemoveWindow(window);
                window.Dispose();
                TexturePaintingWindows.RemoveAt(i);
            }
        }
    }

    public void ToggleMainUI()
    {
        DragAndDropTextures?.RefreshActiveOverrides();
        MainWindow.Toggle();
    }
}
