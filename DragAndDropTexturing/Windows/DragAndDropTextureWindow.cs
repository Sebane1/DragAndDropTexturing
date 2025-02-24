using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ImGuiNET;
using System;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using FFXIVLooseTextureCompiler;
using FFXIVLooseTextureCompiler.PathOrganization;
using System.Collections.Generic;
using System.Threading.Tasks;
using FFXIVLooseTextureCompiler.Racial;
using PenumbraAndGlamourerHelpers;
using System.Threading;
using Ktisis.Structs;
using Ktisis.Structs.Actor;
using Bone = Ktisis.Structs.Bones.Bone;
using FFXIVLooseTextureCompiler.ImageProcessing;
using ICharacter = Dalamud.Game.ClientState.Objects.Types.ICharacter;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using static FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation;
using DragAndDropTexturing;
using LooseTextureCompilerCore.ProjectCreation;
using PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer;
namespace RoleplayingVoice
{
    internal class DragAndDropTextureWindow : Window, IDisposable
    {
        IDalamudTextureWrap textureWrap;
        private IDalamudPluginInterface _pluginInterface;
        private readonly IDragDropManager _dragDropManager;
        private readonly MemoryStream _blank;
        Plugin plugin;
        private ImGuiWindowFlags _defaultFlags;
        private ImGuiWindowFlags _dragAndDropFlags;
        private TextureProcessor _textureProcessor;
        private string _exportStatus;
        private bool _lockDuplicateGeneration;
        private object _currentMod;
        private CharacterCustomization _currentCustomization;
        private string[] _choiceTypes;
        private string[] _bodyNames;
        private string[] _bodyNamesSimplified;
        private string[] _genders;
        private string[] _faceTypes;
        private string[] _faceParts;
        private string[] _faceScales;
        private ITextureProvider _textureProvider;
        private BodyDragPart bodyDragPart;
        private bool _alreadyLoadingFrame;
        private byte[] _nextFrameToLoad;
        private IDalamudTextureWrap _frameToLoad;
        private byte[] _lastLoadedFrame;
        private Bone _closestBone;
        private Vector2 _cursorPosition;

        List<string> _alreadyAddedBoneList = new List<string>();
        List<Tuple<string, float>> boneSorting = new List<Tuple<string, float>>();
        private string modName;

        public Plugin Plugin { get => plugin; set => plugin = value; }

        public DragAndDropTextureWindow(IDalamudPluginInterface pluginInterface, IDragDropManager dragDropManager, ITextureProvider textureProvider) :
            base("DragAndDropTexture", ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoTitleBar, true)
        {
            _defaultFlags = ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoInputs
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoTitleBar;
            _dragAndDropFlags = ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoBringToFrontOnFocus
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoTitleBar;
            IsOpen = true;
            _pluginInterface = pluginInterface;
            Position = new Vector2(0, 0);
            AllowClickthrough = true;
            _dragDropManager = dragDropManager;
            _blank = new MemoryStream();
            Bitmap none = new Bitmap(1, 1);
            Graphics graphics = Graphics.FromImage(none);
            graphics.Clear(Color.Transparent);
            none.Save(_blank, ImageFormat.Png);
            _blank.Position = 0;
            // This will be used for underlay textures.
            // The user will need to download a mod pack with the following path until there is a better way to acquire underlay assets.
            string underlayTexturePath = "";
            // This should reference the xNormal install no matter where its been installed.
            // If this path is not found xNormal reliant functions will be disabled until xNormal is installed.
            _xNormalPath = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\xNormal\3.19.3\xNormal (x64).lnk";
            _textureProcessor = new TextureProcessor(underlayTexturePath);
            _textureProcessor.OnStartedProcessing += TextureProcessor_OnStartedProcessing;
            _textureProcessor.OnLaunchedXnormal += TextureProcessor_OnLaunchedXnormal;

            _textureProvider = textureProvider;
        }

        private void TextureProcessor_OnLaunchedXnormal(object? sender, EventArgs e)
        {
            _exportStatus = "Waiting For XNormal To Generate Assets For Mod";
            plugin.Chat.Print("[Drag And Drop Texturing] " + _exportStatus);
        }

        private void TextureProcessor_OnStartedProcessing(object? sender, EventArgs e)
        {
            _exportStatus = "Compiling Penumbra Assets For Mod";
            plugin.Chat.Print("[Drag And Drop Texturing] " + _exportStatus);
        }

        public override void Draw()
        {
            var size = ImGui.GetIO().DisplaySize;
            Size = new Vector2(size.X, size.Y);
            SizeCondition = ImGuiCond.None;
            var cursorPosition = ImGui.GetIO().MousePos;
            if (IsOpen)
            {
                if (!_lockDuplicateGeneration)
                {
                    Guid mainPlayerCollection = Guid.Empty;
                    Guid selectedPlayerCollection = Guid.Empty;
                    KeyValuePair<string, ICharacter> selectedPlayer = new KeyValuePair<string, ICharacter>("", null);
                    bool holdingModifier = ImGui.GetIO().KeyShift;
                    _dragDropManager.CreateImGuiSource("TextureDragDrop", m => m.Extensions.Any(e => ValidTextureExtensions.Contains(e.ToLowerInvariant())), m =>
                    {
                        try
                        {
                            mainPlayerCollection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(Plugin.ClientState.LocalPlayer.ObjectIndex).Item3.Id;
                            List<KeyValuePair<string, ICharacter>> _objects = new List<KeyValuePair<string, ICharacter>>();
                            _objects.Add(new KeyValuePair<string, ICharacter>(Plugin.ClientState.LocalPlayer.Name.TextValue, Plugin.ClientState.LocalPlayer as ICharacter));
                            bool oneMinionOnly = false;
                            foreach (var item in Plugin.GetNearestObjects())
                            {
                                ICharacter character = item as ICharacter;
                                if (character != null)
                                {
                                    string name = character.Name.TextValue;
                                    if (!string.IsNullOrEmpty(character.Name.TextValue))
                                    {
                                        _objects.Add(new KeyValuePair<string, ICharacter>(name, character));
                                    }
                                }
                            }
                            float aboveNoseYPosFinal = 0;
                            float aboveNeckYPosFinal = 0;
                            float aboveEyesYPosFinal = 0;
                            foreach (var item in _objects)
                            {
                                unsafe
                                {
                                    float closestDistance = float.MaxValue;
                                    Bone closestBone = null;
                                    float aboveEyesYPos = 0;
                                    float aboveNoseYPos = 0;
                                    float aboveNeckYPos = 0;
                                    float xPos = 0;
                                    float minWidth = float.MaxValue;
                                    float maxWidth = 0;
                                    float maxDistance = 0;
                                    Actor* characterActor = (Actor*)item.Value.Address;
                                    var model = characterActor->Model;
                                    for (int i = 0; i < model->Skeleton->PartialSkeletonCount; i++)
                                    {
                                        var partialSkeleton = model->Skeleton->PartialSkeletons[i];
                                        var pos = partialSkeleton.GetHavokPose(0);
                                        if (pos != null)
                                        {
                                            var skeleton = pos->Skeleton;
                                            for (var i2 = 1; i2 < skeleton->Bones.Length; i2++)
                                            {
                                                var bone = model->Skeleton->GetBone(i, i2);
                                                var worldPos = bone.GetWorldPos(characterActor, model);
                                                Vector2 screenPosition = new Vector2();
                                                Plugin.GameGui.WorldToScreen(worldPos, out screenPosition);
                                                float distance = Vector2.Distance(screenPosition, cursorPosition);
                                                _cursorPosition = cursorPosition;
                                                if (distance < closestDistance)
                                                {
                                                    closestDistance = distance;
                                                    closestBone = bone;
                                                }
                                                if (bone.UniqueId.Contains("1_41"))
                                                {
                                                    aboveEyesYPos = screenPosition.Y;
                                                    xPos = screenPosition.X;
                                                }
                                                if (bone.UniqueId.Contains("0_46") || bone.UniqueId.Contains("1_40"))
                                                {
                                                    aboveNoseYPos = screenPosition.Y;
                                                    xPos = screenPosition.X;
                                                }
                                                if (bone.UniqueId.Contains("0_33"))
                                                {
                                                    aboveNeckYPos = screenPosition.Y;
                                                }
                                                if (screenPosition.X > maxWidth)
                                                {
                                                    maxWidth = screenPosition.X;
                                                }
                                                if (screenPosition.X < minWidth)
                                                {
                                                    minWidth = screenPosition.X;
                                                }
                                            }
                                        }
                                    }
                                    maxDistance = Vector2.Distance(new Vector2(minWidth, 0), new Vector2(maxWidth, 0)) / 2f;
                                    if (Vector2.Distance(new(cursorPosition.X, 0), new(xPos, 0)) < maxDistance)
                                    {
                                        selectedPlayer = item;
                                        aboveEyesYPosFinal = aboveEyesYPos;
                                        aboveNoseYPosFinal = aboveNoseYPos;
                                        aboveNeckYPosFinal = aboveNeckYPos;
                                        _closestBone = closestBone;
                                    }
                                }
                            }
                            try
                            {
                                if (cursorPosition.Y < aboveEyesYPosFinal)
                                {
                                    bodyDragPart = BodyDragPart.EyebrowsAndLashes;
                                }
                                else
                                {
                                    if (cursorPosition.Y < aboveNeckYPosFinal)
                                    {

                                        if (cursorPosition.Y < aboveNoseYPosFinal)
                                        {
                                            bodyDragPart = BodyDragPart.Eyes;
                                        }
                                        else
                                        {
                                            bodyDragPart = BodyDragPart.Face;
                                        }

                                    }
                                    else
                                    {
                                        bodyDragPart = BodyDragPart.Body;
                                    }
                                }
                                if (selectedPlayer.Value != null)
                                {
                                    selectedPlayerCollection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(selectedPlayer.Value.ObjectIndex).Item3.Id;
                                }
                                string debugInfo = (_closestBone != null ? "Closest Bone" + _closestBone.HkaBone.Name.String : "") + " " + (cursorPosition != null ? cursorPosition.X + " " + cursorPosition.Y : "");
                                if (selectedPlayer.Value != null)
                                {
                                    if (selectedPlayerCollection != mainPlayerCollection ||
                                        selectedPlayer.Value == Plugin.ClientState.LocalPlayer)
                                    {
                                        ImGui.SetWindowFontScale(1.5f);
                                        ImGui.TextUnformatted($"Dragging texture onto {selectedPlayer.Key.Split(' ')[0]}'s {bodyDragPart.ToString()}:\n\t{string.Join("\n\t", m.Files.Select(Path.GetFileName))} " + debugInfo);
                                    }
                                    else
                                    {
                                        ImGui.SetWindowFontScale(1.5f);
                                        ImGui.TextUnformatted(selectedPlayer.Key.Split(' ')[0] + " has the same collection as your main character.\r\nPlease give them a unique collection in Penumbra, or drag onto your main character. " + debugInfo);
                                    }
                                    ImGui.SetWindowFontScale(1f);
                                }
                                else
                                {
                                    ImGui.TextUnformatted($"Dragging onto no character." + debugInfo);
                                }
                            }
                            catch
                            {
                                ImGui.TextUnformatted($"Dragging texture on unknown:\n\t{string.Join("\n\t", m.Files.Select(Path.GetFileName))}");
                            }
                            AllowClickthrough = false;
                        }
                        catch
                        {
                            ImGui.TextUnformatted($"Penumbra is not installed.");
                        }
                        return true;
                    });

                    if (!AllowClickthrough)
                    {
                        Flags = _dragAndDropFlags;
                        if (!_alreadyLoadingFrame)
                        {
                            Task.Run(async () =>
                            {
                                _alreadyLoadingFrame = true;
                                _nextFrameToLoad = _blank.ToArray();
                                if (_lastLoadedFrame != _nextFrameToLoad)
                                {
                                    _frameToLoad = await _textureProvider.CreateFromImageAsync(_nextFrameToLoad);
                                    _lastLoadedFrame = _nextFrameToLoad;
                                }
                                _alreadyLoadingFrame = false;
                            });
                        }
                        try
                        {
                            textureWrap = _frameToLoad.CreateWrapSharingLowLevelResource();
                            ImGui.Image(textureWrap.ImGuiHandle, new Vector2(ImGui.GetMainViewport().Size.X, ImGui.GetMainViewport().Size.Y));
                        }
                        catch
                        {

                        }
                    }
                    else
                    {
                        Flags = _defaultFlags;
                    }

                    if (_dragDropManager.CreateImGuiTarget("TextureDragDrop", out var files, out _))
                    {
                        string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                        _textureProcessor.BasePath = modPath + @"\LooseTextureCompilerDLC";
                        List<TextureSet> textureSets = new List<TextureSet>();
                        if (selectedPlayer.Value != null && selectedPlayerCollection != mainPlayerCollection ||
                            selectedPlayer.Value == Plugin.ClientState.LocalPlayer)
                        {
                            modName = selectedPlayer.Key.Split(' ')[0] + " Texture Mod";
                            _currentCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(selectedPlayer.Value);
                            Task.Run(() =>
                            {
                                foreach (var file in files)
                                {
                                    if (ValidTextureExtensions.Contains(Path.GetExtension(file)))
                                    {
                                        string filePath = file;
                                        string fileName = Path.GetFileNameWithoutExtension(filePath).ToLower();
                                        if (fileName.Contains("mata") || fileName.Contains("amat")
                                            || fileName.Contains("materiala") || fileName.Contains("gen2"))
                                        {
                                            var item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 0,
                                            RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                            _currentCustomization.Customize.TailShape.Value - 1, false);
                                            modName += " " + ProjectHelper.SortUVTexture(item, file).ToString();
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Body");
                                            item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                        }
                                        else if (fileName.Contains("bibo") || fileName.Contains("b+"))
                                        {
                                            var item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 1,
                                            RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                            _currentCustomization.Customize.TailShape.Value - 1, false);
                                            modName += " " + ProjectHelper.SortUVTexture(item, file);
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Body");
                                            item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                        }
                                        else if (fileName.Contains("gen3"))
                                        {
                                            var item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 2,
                                            RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                            _currentCustomization.Customize.TailShape.Value - 1, false);
                                            modName += " " + ProjectHelper.SortUVTexture(item, file);
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Body");
                                            item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                        }
                                        else if (fileName.Contains("tbse"))
                                        {
                                            var item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 3,
                                            RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                            _currentCustomization.Customize.TailShape.Value - 1, false);
                                            modName += " " + ProjectHelper.SortUVTexture(item, file);
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Body");
                                            item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                        }
                                        else if (fileName.Contains("eyebrow") || fileName.Contains("lash"))
                                        {
                                            var item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 1, 0,
                                             _currentCustomization.Customize.Gender.Value,
                                             RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                             _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                            item.Normal = file;
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Eyebrows");
                                        }
                                        else if (fileName.Contains("eye"))
                                        {
                                            var item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 2, 0,
                                            _currentCustomization.Customize.Gender.Value,
                                            RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                            _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                            item.Base = file;
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Eyes");
                                        }
                                        else if (fileName.Contains("face") || fileName.Contains("makeup"))
                                        {
                                            var item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 0, 0,
                                            _currentCustomization.Customize.Gender.Value,
                                            RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                            _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                            ProjectHelper.SortUVTexture(item, file);
                                            textureSets.Add(item);
                                            modName = modName.Replace("Mod", "Face");
                                        }
                                        else
                                        {
                                            TextureSet item = null;
                                            switch (bodyDragPart)
                                            {
                                                case BodyDragPart.Body:
                                                    if (_currentCustomization.Customize.Race.Value - 1 == 2)
                                                    {
                                                        item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 5,
                                                        RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                        _currentCustomization.Customize.TailShape.Value - 1, false);
                                                        ProjectHelper.SortUVTexture(item, file);
                                                        textureSets.Add(item);
                                                        modName = modName.Replace("Mod", "Body");
                                                        item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                                    }
                                                    else if (_currentCustomization.Customize.Gender.Value == 0)
                                                    {
                                                        item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 3,
                                                        RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                        _currentCustomization.Customize.TailShape.Value - 1, false);
                                                        ProjectHelper.SortUVTexture(item, file);
                                                        textureSets.Add(item);
                                                        modName = modName.Replace("Mod", "Body");
                                                        item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                                    }
                                                    else
                                                    {
                                                        switch (ImageManipulation.FemaleBodyUVClassifier(file))
                                                        {
                                                            case BodyUVType.Bibo:
                                                                item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 1,
                                                                RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                                _currentCustomization.Customize.TailShape.Value - 1, false);
                                                                ProjectHelper.SortUVTexture(item, file);
                                                                textureSets.Add(item);
                                                                modName = modName.Replace("Mod", "Body");
                                                                item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                                                break;
                                                            case BodyUVType.Gen3:
                                                                item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 2,
                                                                RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                                _currentCustomization.Customize.TailShape.Value - 1, false);
                                                                ProjectHelper.SortUVTexture(item, file);
                                                                textureSets.Add(item);
                                                                modName = modName.Replace("Mod", "Body");
                                                                item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                                                break;
                                                            case BodyUVType.Gen2:
                                                                item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 0,
                                                                RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                                _currentCustomization.Customize.TailShape.Value - 1, false);
                                                                ProjectHelper.SortUVTexture(item, file);
                                                                textureSets.Add(item);
                                                                modName = modName.Replace("Mod", "Body");
                                                                item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                                                break;
                                                        }
                                                    }
                                                    break;
                                                case BodyDragPart.Face:
                                                    item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 0, 0,
                                                    _currentCustomization.Customize.Gender.Value,
                                                    RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                    _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                                    ProjectHelper.SortUVTexture(item, file);
                                                    textureSets.Add(item);
                                                    modName = modName.Replace("Mod", "Face");
                                                    break;
                                                case BodyDragPart.Eyes:
                                                    item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 2, 0,
                                                    _currentCustomization.Customize.Gender.Value,
                                                    RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                    _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                                    item.Normal = file;
                                                    textureSets.Add(item);
                                                    modName = modName.Replace("Mod", "Eyes");
                                                    break;
                                                case BodyDragPart.EyebrowsAndLashes:
                                                    item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 1, 0,
                                                    _currentCustomization.Customize.Gender.Value,
                                                    RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1),
                                                    _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                                    item.Normal = file;
                                                    textureSets.Add(item);
                                                    modName = modName.Replace("Mod", "Eyebrows");
                                                    break;
                                            }
                                        }
                                    }
                                }
                                string fullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), modName);
                                if (textureSets.Count > 0)
                                {
                                    Task.Run(() => Export(true, textureSets, fullModPath, modName, selectedPlayer));
                                }
                                else
                                {
                                    plugin.Chat.PrintError("[Drag And Drop Texturing] Unable to identify texture type!");
                                }
                            });
                        }
                    }
                    AllowClickthrough = true;
                }
                else
                {
                    AllowClickthrough = true;
                    Flags = _defaultFlags;
                }
            }
        }
        public async Task<bool> Export(bool finalize, List<TextureSet> exportTextureSets, string path,
            string name, KeyValuePair<string, ICharacter> character)
        {
            if (!_lockDuplicateGeneration)
            {
                plugin.Chat.Print("[Drag And Drop Texturing] Processing textures, please wait.");
                string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                _textureProcessor.BasePath = modPath + @"\LooseTextureCompilerDLC";
                _exportStatus = "Initializing";
                _lockDuplicateGeneration = true;
                ProjectHelper.ExportProject(path, name, exportTextureSets, _textureProcessor, _xNormalPath);
                Thread.Sleep(100);
                PenumbraAndGlamourerIpcWrapper.Instance.AddMod.Invoke(name);
                PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(path, name);
                Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.Value.ObjectIndex).Item3.Id;
                PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, path, true, name);
                PenumbraAndGlamourerIpcWrapper.Instance.TrySetModPriority.Invoke(collection, path, 100, name);
                var settings = PenumbraAndGlamourerIpcWrapper.Instance.GetCurrentModSettings.Invoke(collection, path, name, true);
                foreach (var group in settings.Item2.Value.Item3)
                {
                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetModSetting.Invoke(collection, path, group.Key, "Enable", name);
                }
                Thread.Sleep(300);
                PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.Value.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                Thread.Sleep(300);
                PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.Value.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                _lockDuplicateGeneration = false;
                plugin.Chat.Print("[Drag And Drop Texturing] Import complete! Created mod is toggleable in penumbra.");
            }
            return true;
        }

        public void Dispose()
        {
        }

        private static readonly string[] ValidTextureExtensions = new[]
        {
          ".png",
          ".dds",
          ".bmp",
          ".tex",
        };
        private readonly string _xNormalPath;
    }
}

namespace PenumbraAndGlamourerHelpers
{
    public enum BodyDragPart
    {
        Unknown,
        Eyes,
        Face,
        Body,
        EyebrowsAndLashes
    }
}
