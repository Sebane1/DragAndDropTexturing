using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Bindings.ImGui;
using System;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using FFXIVLooseTextureCompiler;
using FFXIVLooseTextureCompiler.PathOrganization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Net.Http;
using FFXIVLooseTextureCompiler.Racial;
using PenumbraAndGlamourerHelpers;
using System.Threading;
using Ktisis.Structs;
using Ktisis.Structs.Actor;
using Bone = Ktisis.Structs.Bones.Bone;
using FFXIVLooseTextureCompiler.ImageProcessing;
using FFXIVLooseTextureCompiler.Export;
using ICharacter = Dalamud.Game.ClientState.Objects.Types.ICharacter;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using static FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation;
using DragAndDropTexturing;
using DragAndDropTexturing.Equipment;
using DragAndDropTexturing.LanguageHelpers;
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
        private ICharacter _currentTarget;
        private bool _lockDuplicateGeneration;
        private bool _hideProgressUI;
        public Dictionary<string, string> ActiveBodyOverrides = new Dictionary<string, string>();
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
        private TaskCompletionSource<string> _classificationTcs;
        private string _fileToClassify;
        private bool _showClassificationPopup;
        private bool _classificationIsBody;
        private int _classificationSelectedUV = 0;
        private int _classificationSelectedMap = 0;
        private IDalamudTextureWrap _classificationTexturePreview;
        private Guid _lastSelectedCollection = Guid.Empty;
        private Guid _lastMainCollection = Guid.Empty;
        private DateTime _lastIpcCheckTime = DateTime.MinValue;
        private BodyDragPart bodyDragPart;
        private bool _alreadyLoadingFrame;
        private byte[] _nextFrameToLoad;
        private IDalamudTextureWrap _frameToLoad;
        private byte[] _lastLoadedFrame;
        private Bone _closestBone;
        private Vector2 _cursorPosition;
        private Vector2 _smoothedBarPos = Vector2.Zero;
        private bool _isDownloadingDLC = false;
        private bool _isWaitingForPenumbra = false;
        private float _dlcDownloadProgress = 0f;
        public bool IsDownloadingDLC { get => _isDownloadingDLC || _isWaitingForPenumbra; }
        public float DLCDownloadProgress { get => _dlcDownloadProgress; }

        List<string> _alreadyAddedBoneList = new List<string>();
        List<Tuple<string, float>> boneSorting = new List<Tuple<string, float>>();
        private Dictionary<string, List<string>> _textureHistory = new Dictionary<string, List<string>>();
        private Dictionary<string, List<System.Numerics.Vector4>> _textureHistoryTints = new Dictionary<string, List<System.Numerics.Vector4>>();
        public Dictionary<string, List<string>> TextureHistory { get => _textureHistory; set => _textureHistory = value; }
        public Dictionary<string, List<System.Numerics.Vector4>> TextureHistoryTints { get => _textureHistoryTints; set => _textureHistoryTints = value; }
        private readonly Dictionary<string, WornEquipmentPiece> _gearCategoryMeta = new();
        public Dictionary<string, WornEquipmentPiece> GearCategoryMeta { get => _gearCategoryMeta; }
        public List<WornEquipmentPiece> CachedWornGear { get; private set; } = new();

        // Auto-regeneration tracking
        private System.Threading.Timer _regenerationDebounce;
        private HashSet<string> _pendingRegenerationCategories = new HashSet<string>();
        private readonly object _regenerationLock = new object();

        private void AddToTextureSet(TextureSet item, string file, string overrideType = "", System.Numerics.Vector4? tint = null)
        {
            if (item.TextureSetName.ToLower().Contains("face"))
            {
                item.IgnoreNormalGeneration = true;
            }
            UVMapType uvType = UVMapType.Base;
            if (overrideType == "Normal") uvType = UVMapType.Normal;
            else if (overrideType == "Base") uvType = UVMapType.Base;
            else
            {
                TextureSet temp = new TextureSet();
                uvType = ProjectHelper.SortUVTexture(temp, file);
            }

            string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
            string sourceUV = "";
            if (fileName.Contains("bibo") || fileName.Contains("b+")) sourceUV = "bibo";
            else if (fileName.Contains("gen3") || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"(^|[^a-z])eve([^a-z]|$)")) sourceUV = "gen3";
            else if (fileName.Contains("tbse")) sourceUV = "tbse";
            else if (fileName.Contains("gen2") || fileName.Contains("body") || fileName.Contains("mata")) sourceUV = "gen2";

            if (string.IsNullOrEmpty(sourceUV))
            {
                if (uvType != UVMapType.Glow && uvType != UVMapType.Mask)
                {
                    // Fall back to querying the user's active body via Penumbra Settings
                    int penumbraBase = -1;
                    var localPlayer = plugin?.SafeGameObjectManager?.LocalPlayer;
                    if (localPlayer != null && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter character)
                    {
                        var customization = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                        Guid collectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                        int gender = customization.Customize.Gender.Value;
                        penumbraBase = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, gender, out string _, plugin);
                    }

                    if (penumbraBase == 1) sourceUV = "bibo";
                    else if (penumbraBase == 2) sourceUV = "gen3";
                    else if (penumbraBase == 3) sourceUV = "tbse";
                    else
                    {
                        // Final fallback: Image heuristic
                        switch (ImageManipulation.FemaleBodyUVClassifier(file))
                        {
                            case BodyUVType.Bibo: sourceUV = "bibo"; break;
                            case BodyUVType.Gen3: sourceUV = "gen3"; break;
                            case BodyUVType.Gen2: sourceUV = "gen2"; break;
                        }
                    }
                }
            }

            if (uvType == UVMapType.Base)
            {
                if (string.IsNullOrEmpty(item.Base)) { item.Base = file; item.BaseUV = sourceUV; }
                else if (!item.BaseOverlays.Contains(file)) { item.BaseOverlays.Add(file); item.BaseOverlayUVs.Add(sourceUV); item.BaseOverlayTints.Add(tint ?? System.Numerics.Vector4.One); }
            }
            else if (uvType == UVMapType.Normal)
            {
                if (string.IsNullOrEmpty(item.Normal)) { item.Normal = file; item.NormalUV = sourceUV; }
                else if (!item.NormalOverlays.Contains(file)) { item.NormalOverlays.Add(file); item.NormalOverlayUVs.Add(sourceUV); }
            }
            else if (uvType == UVMapType.Mask)
            {
                if (string.IsNullOrEmpty(item.Mask)) { item.Mask = file; item.MaskUV = sourceUV; }
                else if (!item.MaskOverlays.Contains(file)) { item.MaskOverlays.Add(file); item.MaskOverlayUVs.Add(sourceUV); }
            }
            else if (uvType == UVMapType.Glow)
            {
                if (string.IsNullOrEmpty(item.Glow)) { item.Glow = file; item.GlowUV = sourceUV; }
                else if (!item.GlowOverlays.Contains(file)) { item.GlowOverlays.Add(file); item.GlowOverlayUVs.Add(sourceUV); }
            }
        }

        private string modName;

        private Dictionary<string, System.IO.FileSystemWatcher> _fileWatchers = new Dictionary<string, System.IO.FileSystemWatcher>();
        private object _watcherLock = new object();
        private DateTime _lastRebuildTime = DateTime.MinValue;

        private void UpdateWatchers()
        {
            lock (_watcherLock)
            {
                foreach (var watcher in _fileWatchers.Values)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                _fileWatchers.Clear();

                foreach (var kvp in _textureHistory)
                {
                    string category = kvp.Key;
                    foreach (var file in kvp.Value)
                    {
                        if (System.IO.File.Exists(file))
                        {
                            string dir = System.IO.Path.GetDirectoryName(file);
                            if (string.IsNullOrEmpty(dir)) continue;

                            string watcherKey = dir.ToLowerInvariant();
                            if (!_fileWatchers.TryGetValue(watcherKey, out var watcher))
                            {
                                watcher = new System.IO.FileSystemWatcher();
                                watcher.Path = dir;
                                watcher.NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size;
                                watcher.Changed += FileWatcher_Changed;
                                watcher.EnableRaisingEvents = true;
                                _fileWatchers[watcherKey] = watcher;
                            }
                        }
                    }
                }
            }
        }

        private void FileWatcher_Changed(object sender, System.IO.FileSystemEventArgs e)
        {
            if ((DateTime.Now - _lastRebuildTime).TotalMilliseconds < 500) return; // Debounce

            bool triggered = false;
            HashSet<string> categoriesToRebuild = new HashSet<string>();

            lock (_watcherLock)
            {
                foreach (var kvp in _textureHistory)
                {
                    foreach (var file in kvp.Value)
                    {
                        if (file.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            categoriesToRebuild.Add(kvp.Key);
                            triggered = true;
                        }
                    }
                }
            }

            if (triggered)
            {
                _lastRebuildTime = DateTime.Now;
                System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(200); // Give Photoshop a moment to finish writing
                    foreach (var cat in categoriesToRebuild)
                    {
                        RebuildCategory(cat, false);
                    }
                });
            }
        }

        public Plugin Plugin
        {
            get => plugin;
            set
            {
                plugin = value;
                if (plugin != null)
                {
                    _textureHistory = plugin.Configuration.TextureHistory;
                    _textureHistoryTints = plugin.Configuration.TextureHistoryTints;
                    if (_textureHistoryTints == null)
                    {
                        _textureHistoryTints = new Dictionary<string, List<System.Numerics.Vector4>>();
                        plugin.Configuration.TextureHistoryTints = _textureHistoryTints;
                    }
                    foreach (var key in _textureHistory.Keys)
                    {
                        if (!_textureHistoryTints.ContainsKey(key))
                        {
                            _textureHistoryTints[key] = new List<System.Numerics.Vector4>();
                            for (int i = 0; i < _textureHistory[key].Count; i++)
                            {
                                _textureHistoryTints[key].Add(System.Numerics.Vector4.One);
                            }
                        }
                    }
                    var oldKeys = _textureHistory.Keys.Where(k => k.EndsWith("_gen2") || k.EndsWith("_bibo") || k.EndsWith("_gen3") || k.EndsWith("_tbse") || k.EndsWith("_otopop") || k.EndsWith("fallback_Body")).ToList();
                    bool migrated = false;
                    foreach (var key in oldKeys)
                    {
                        string newKey = key.Substring(0, key.LastIndexOf('_')) + "_body";
                        if (key.EndsWith("fallback_Body")) newKey = key.Replace("fallback_Body", "body");

                        if (!_textureHistory.ContainsKey(newKey))
                        {
                            _textureHistory[newKey] = new List<string>();
                            _textureHistoryTints[newKey] = new List<System.Numerics.Vector4>();
                        }
                        _textureHistory[newKey].AddRange(_textureHistory[key]);
                        if (_textureHistoryTints.ContainsKey(key))
                        {
                            _textureHistoryTints[newKey].AddRange(_textureHistoryTints[key]);
                            _textureHistoryTints.Remove(key);
                        }
                        else
                        {
                            for (int i = 0; i < _textureHistory[key].Count; i++) _textureHistoryTints[newKey].Add(System.Numerics.Vector4.One);
                        }
                        _textureHistory.Remove(key);
                        migrated = true;
                    }
                    if (migrated) plugin.Configuration.Save();

                    Task.Run(async () =>
                    {
                        await CheckAndDownloadDLC();

                        // Hook Glamourer state changes for auto-regeneration
                        PenumbraAndGlamourerIpcWrapper.Instance.OnGlamourerStateChanged += OnGlamourerStateChanged;
                        PenumbraAndGlamourerIpcWrapper.Instance.OnModSettingChanged += OnModSettingChanged;

                        // Trigger initial rebuild if player is already logged in with existing texture history
                        TryInitialRebuild();
                    });
                }
            }
        }

        private async Task CheckAndDownloadDLC()
        {
            try
            {
                string modPath = string.Empty;
                _isWaitingForPenumbra = true;
                while (string.IsNullOrEmpty(modPath))
                {
                    try
                    {
                        modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                    }
                    catch
                    {
                        await Task.Delay(1000);
                    }
                }
                _isWaitingForPenumbra = false;

                string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                string fastUvPath = Path.Combine(dlcPath, "res", "fastuvtransfer");
                if (!Directory.Exists(dlcPath) || !Directory.Exists(fastUvPath) || Directory.GetFiles(dlcPath, "*.*", SearchOption.AllDirectories).Length < 5)
                {
                    _isDownloadingDLC = true;
                    plugin.PluginLog.Information("[Drag And Drop Texturing] Missing LooseTextureCompilerDLC. Auto-downloading from GitHub now... This may take a moment.");
                    using (HttpClient client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("DragAndDropTexturing/1.0");
                        string downloadUrl = "https://github.com/Sebane1/DragAndDropTexturing/releases/download/0.0.1.3/LooseTextureCompilerDLC.pmp";
                        using (HttpResponseMessage response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            long? totalBytes = response.Content.Headers.ContentLength;
                            byte[] fileBytes;

                            using (var stream = await response.Content.ReadAsStreamAsync())
                            using (var ms = new MemoryStream())
                            {
                                byte[] buffer = new byte[8192];
                                int bytesRead;
                                long totalRead = 0;
                                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await ms.WriteAsync(buffer, 0, bytesRead);
                                    totalRead += bytesRead;
                                    if (totalBytes.HasValue)
                                    {
                                        _dlcDownloadProgress = (float)totalRead / totalBytes.Value;
                                    }
                                }
                                fileBytes = ms.ToArray();
                            }

                            if (fileBytes.Length > 100 && fileBytes[0] == 0x50 && fileBytes[1] == 0x4B)
                            {
                                string tempZip = Path.Combine(Path.GetTempPath(), "LooseTextureCompilerDLC.pmp");
                                File.WriteAllBytes(tempZip, fileBytes);

                                string extractPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                                if (!Directory.Exists(extractPath))
                                {
                                    Directory.CreateDirectory(extractPath);
                                }

                                string tempExtract = Path.Combine(Path.GetTempPath(), "LooseTextureCompilerDLC_Temp");
                                if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true);
                                Directory.CreateDirectory(tempExtract);

                                ZipFile.ExtractToDirectory(tempZip, tempExtract, true);

                                string[] dirs = Directory.GetDirectories(tempExtract);
                                string[] filesExtracted = Directory.GetFiles(tempExtract);
                                if (dirs.Length == 1 && filesExtracted.Length == 0 && Path.GetFileName(dirs[0]) == "LooseTextureCompilerDLC")
                                {
                                    foreach (string dirPath in Directory.GetDirectories(dirs[0], "*", SearchOption.AllDirectories))
                                    {
                                        Directory.CreateDirectory(dirPath.Replace(dirs[0], extractPath));
                                    }
                                    foreach (string newPath in Directory.GetFiles(dirs[0], "*.*", SearchOption.AllDirectories))
                                    {
                                        File.Copy(newPath, newPath.Replace(dirs[0], extractPath), true);
                                    }
                                }
                                else
                                {
                                    foreach (string dirPath in Directory.GetDirectories(tempExtract, "*", SearchOption.AllDirectories))
                                    {
                                        Directory.CreateDirectory(dirPath.Replace(tempExtract, extractPath));
                                    }
                                    foreach (string newPath in Directory.GetFiles(tempExtract, "*.*", SearchOption.AllDirectories))
                                    {
                                        File.Copy(newPath, newPath.Replace(tempExtract, extractPath), true);
                                    }
                                }

                                Directory.Delete(tempExtract, true);
                                File.Delete(tempZip);

                                plugin.PluginLog.Information("[Drag And Drop Texturing] LooseTextureCompilerDLC downloaded and installed successfully!");
                            }
                            else
                            {
                                plugin.PluginLog.Error("[Drag And Drop Texturing] Auto-download failed. The downloaded file was not a valid archive.");
                            }
                        } // using response
                    } // using client
                } // if
            } // try
            catch (Exception ex)
            {
                plugin.PluginLog.Error("[Drag And Drop Texturing] Failed to download DLC: " + ex.Message);
            }
            finally
            {
                _isDownloadingDLC = false;
            }
        }

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
            _textureProcessor.OnProgressReport += TextureProcessor_OnProgressReport;
            _textureProcessor.OnError += (sender, msg) => plugin.PluginLog.Error($"[TextureProcessor] {msg}");

            _textureProvider = textureProvider;
        }

        private void TextureProcessor_OnProgressReport(object? sender, string e)
        {
            _exportStatus = e;
        }

        private void TextureProcessor_OnLaunchedXnormal(object? sender, EventArgs e)
        {
            _exportStatus = "Waiting For Fast UV Transfer To Generate Assets For Mod";
            plugin.PluginLog.Information("[Drag And Drop Texturing] " + _exportStatus);
        }

        private void TextureProcessor_OnStartedProcessing(object? sender, EventArgs e)
        {
            _exportStatus = "Compiling Penumbra Assets For Mod";
            plugin.PluginLog.Information("[Drag And Drop Texturing] " + _exportStatus);
        }

        private int _lastDistanceBracket = -1;

        public override void Update()
        {
            if (Plugin.Configuration.AutoDistanceExportQuality && plugin?.SafeGameObjectManager?.LocalPlayer != null)
            {
                try
                {
                    string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                    var charKeys = _textureHistory.Keys.Where(k => k.StartsWith(charName + "_") && _textureHistory[k].Count > 0).ToList();

                    if (charKeys.Count > 0)
                    {
                        unsafe
                        {
                            var cameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
                            if (cameraManager != null && cameraManager->CurrentCamera != null)
                            {
                                var camPos = cameraManager->CurrentCamera->Object.Position;
                                var playerPos = plugin.SafeGameObjectManager.LocalPlayer.Position;
                                float distance = System.Numerics.Vector3.Distance(camPos, playerPos);

                                int currentBracket = 0;
                                if (distance < 2.5f) currentBracket = 2; // High
                                else if (distance < 6.0f) currentBracket = 1; // Mid
                                else currentBracket = 0; // Low

                                if (_lastDistanceBracket != -1 && _lastDistanceBracket != currentBracket)
                                {
                                    plugin.PluginLog.Information($"[Drag And Drop Texturing] Camera crossed distance threshold ({distance}m). Auto-triggering export.");

                                    List<string> partsToRegenerate = new List<string>();
                                    foreach (var key in charKeys)
                                    {
                                        string partSuffix = key.Substring(charName.Length);
                                        if (partSuffix.Contains("face") || partSuffix.Contains("eye")) continue;
                                        partsToRegenerate.Add(partSuffix);
                                    }

                                    if (partsToRegenerate.Count > 0)
                                    {
                                        ScheduleRegeneration(charName, partsToRegenerate.ToArray(), true);
                                    }
                                }

                                _lastDistanceBracket = currentBracket;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public override void Draw()
        {
            var size = ImGui.GetIO().DisplaySize;
            Size = new Vector2(size.X, size.Y);
            SizeCondition = ImGuiCond.None;
            var cursorPosition = ImGui.GetIO().MousePos;
            if (IsOpen)
            {
                if (_showClassificationPopup)
                {
                    ImGui.OpenPopup("Identify Texture Format");
                    Vector2 center = ImGui.GetMainViewport().GetCenter();
                    ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
                    if (ImGui.BeginPopupModal("Identify Texture Format", ref _showClassificationPopup, ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        ImGui.Text("The dropped texture could not be automatically identified.");
                        ImGui.Text($"File: {Path.GetFileName(_fileToClassify)}");
                        ImGui.Separator();

                        // Always allow target reassignment
                        ImGui.Text("1. Select Target / UV Layout:");
                        ImGui.RadioButton("Bibo+", ref _classificationSelectedUV, 0); ImGui.SameLine();
                        ImGui.RadioButton("Gen3", ref _classificationSelectedUV, 1); ImGui.SameLine();
                        ImGui.RadioButton("TBSE", ref _classificationSelectedUV, 2); ImGui.SameLine();
                        ImGui.RadioButton("Gen2 / Vanilla", ref _classificationSelectedUV, 3); ImGui.SameLine();
                        ImGui.RadioButton("Face", ref _classificationSelectedUV, 4);
                        ImGui.Spacing();

                        ImGui.Text("2. Select Texture Map Type:");
                        ImGui.RadioButton("Base / Diffuse", ref _classificationSelectedMap, 0); ImGui.SameLine();
                        ImGui.RadioButton("Normal Map", ref _classificationSelectedMap, 1); ImGui.SameLine();
                        ImGui.RadioButton("Mask", ref _classificationSelectedMap, 2); ImGui.SameLine();
                        ImGui.RadioButton("Glow", ref _classificationSelectedMap, 3);

                        ImGui.Separator();
                        if (ImGui.Button("Confirm", new Vector2(120, 0)))
                        {
                            string uv = _classificationSelectedUV == 0 ? "bibo" : _classificationSelectedUV == 1 ? "gen3" : _classificationSelectedUV == 2 ? "tbse" : _classificationSelectedUV == 3 ? "gen2" : "face";
                            string map = _classificationSelectedMap == 0 ? "base" : _classificationSelectedMap == 1 ? "norm" : _classificationSelectedMap == 2 ? "mask" : "glow";
                            _classificationTcs?.TrySetResult($"{uv}|{map}");
                            _showClassificationPopup = false;
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Cancel", new Vector2(120, 0)))
                        {
                            _classificationTcs?.TrySetResult("");
                            _showClassificationPopup = false;
                        }

                        if (!_showClassificationPopup)
                        {
                            ImGui.CloseCurrentPopup();
                        }
                        ImGui.EndPopup();
                    }
                    else
                    {
                        if (!_showClassificationPopup)
                        {
                            _classificationTcs?.TrySetResult("");
                        }
                    }
                }

                if (_isWaitingForPenumbra)
                {
                    Vector2 barPos = new Vector2(size.X / 2 - 150, size.Y - 100);
                    ImGui.SetCursorPos(barPos);
                    ImGui.BeginChild("LoadingBoxPenumbra", new Vector2(300, 40), true, ImGuiWindowFlags.NoScrollbar);
                    float bounce = (float)Math.Abs(Math.Sin(ImGui.GetTime() * 2.0));
                    ImGui.ProgressBar(bounce, new Vector2(-1, 0), Translator.LocalizeUI("Waiting for Penumbra IPC..."));
                    ImGui.EndChild();
                }
                else if (_isDownloadingDLC)
                {
                    Vector2 barPos = new Vector2(size.X / 2 - 150, size.Y - 100);
                    ImGui.SetCursorPos(barPos);
                    ImGui.BeginChild("LoadingBoxDLC", new Vector2(300, 40), true, ImGuiWindowFlags.NoScrollbar);
                    if (_dlcDownloadProgress > 0f && _dlcDownloadProgress < 1f)
                    {
                        ImGui.ProgressBar(_dlcDownloadProgress, new Vector2(-1, 0), Translator.LocalizeUI("Downloading DLC:") + $" {(_dlcDownloadProgress * 100):0.0}%");
                    }
                    else
                    {
                        float bounce = (float)Math.Abs(Math.Sin(ImGui.GetTime() * 2.0));
                        ImGui.ProgressBar(bounce, new Vector2(-1, 0), Translator.LocalizeUI("Fetching DLC (Please wait)..."));
                    }
                    ImGui.EndChild();
                }
                else if (_lockDuplicateGeneration && !_hideProgressUI)
                {
                    Vector2 barPos = new Vector2(size.X / 2 - 150, size.Y - 100);
                    if (_currentTarget != null && _currentTarget.Address != nint.Zero)
                    {
                        if (DragAndDropTexturing.Plugin.GameGui.WorldToScreen(_currentTarget.Position + new Vector3(0, 1.5f, 0), out Vector2 screenPos))
                        {
                            Vector2 targetPos = new Vector2(screenPos.X - 150, screenPos.Y);
                            if (_smoothedBarPos == Vector2.Zero || Vector2.Distance(_smoothedBarPos, targetPos) > 200f)
                            {
                                _smoothedBarPos = targetPos; // Snap if too far or first frame
                            }
                            else if (Vector2.Distance(_smoothedBarPos, targetPos) > 2.0f)
                            {
                                // Lerp smoothly with a lower factor, but use a deadzone to prevent breathing jitter
                                _smoothedBarPos = Vector2.Lerp(_smoothedBarPos, targetPos, 0.1f);
                            }
                            // Round the final coordinates to prevent ImGui sub-pixel anti-aliasing/rendering jitter
                            barPos = new Vector2((float)Math.Round(_smoothedBarPos.X), (float)Math.Round(_smoothedBarPos.Y));
                        }
                    }
                    else
                    {
                        _smoothedBarPos = Vector2.Zero;
                    }
                    ImGui.SetCursorPos(barPos);
                    ImGui.BeginChild("LoadingBox", new Vector2(300, 40), true, ImGuiWindowFlags.NoScrollbar);
                    if (_textureProcessor != null && _textureProcessor.ExportMax > 0)
                    {
                        ImGui.ProgressBar(_textureProcessor.ExportCompletion / (float)_textureProcessor.ExportMax, new Vector2(-1, 0), _exportStatus);
                    }
                    else
                    {
                        ImGui.ProgressBar(0f, new Vector2(-1, 0), _exportStatus);
                    }
                    ImGui.EndChild();
                }

                if (!_lockDuplicateGeneration && !_isDownloadingDLC && !_isWaitingForPenumbra)
                {
                    Guid mainPlayerCollection = Guid.Empty;
                    Guid selectedPlayerCollection = Guid.Empty;
                    KeyValuePair<string, ICharacter> selectedPlayer = new KeyValuePair<string, ICharacter>("", null);
                    bool holdingModifier = ImGui.GetIO().KeyShift || Plugin.Configuration.AutoUniversalConvert;
                    _dragDropManager.CreateImGuiSource("TextureDragDrop", m => m.Extensions.Any(e => ValidTextureExtensions.Contains(e.ToLowerInvariant())), m =>
                    {
                        try
                        {
                            _closestBone = null;
                            List<KeyValuePair<string, ICharacter>> _objects = new List<KeyValuePair<string, ICharacter>>();
                            _objects.Add(new KeyValuePair<string, ICharacter>(plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue, plugin.SafeGameObjectManager.LocalPlayer as ICharacter));
                            bool oneMinionOnly = false;
                            foreach (var item in Plugin.GetNearestObjects())
                            {
                                Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter character = item as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
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
                                    if (model != null)
                                    {
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

                                if (ImGui.GetIO().KeyCtrl)
                                {
                                    bodyDragPart = BodyDragPart.Clothing;
                                }

                                if (selectedPlayer.Value != null)
                                {
                                    if ((DateTime.Now - _lastIpcCheckTime).TotalMilliseconds > 1000)
                                    {
                                        _lastSelectedCollection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(selectedPlayer.Value.ObjectIndex).Item3.Id;
                                        _lastMainCollection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex).Item3.Id;
                                        _lastIpcCheckTime = DateTime.Now;
                                    }
                                    selectedPlayerCollection = _lastSelectedCollection;
                                    mainPlayerCollection = _lastMainCollection;
                                }
                                string debugInfo = (_closestBone != null ? "Closest Bone " + _closestBone.HkaBone.Name.String : "") + " " + (cursorPosition != null ? cursorPosition.X + " " + cursorPosition.Y : "");
                                if (selectedPlayer.Value != null)
                                {
                                    if (selectedPlayerCollection != mainPlayerCollection ||
                                        selectedPlayer.Value == plugin.SafeGameObjectManager.LocalPlayer)
                                    {
                                        ImGui.SetWindowFontScale(1.5f);
                                        string partName = bodyDragPart.ToString();
                                        if (bodyDragPart == BodyDragPart.Clothing)
                                        {
                                            string gearSlot = GetGearSlotFromBone(_closestBone);
                                            partName = $"Clothing ({char.ToUpper(gearSlot[0]) + gearSlot.Substring(1).Replace('_', ' ')})";
                                        }
                                        ImGui.TextUnformatted(Translator.LocalizeUI("Dragging texture onto") + $" {selectedPlayer.Key.Split(' ')[0]}'s {partName}:\n\t{string.Join("\n\t", m.Files.Select(Path.GetFileName))} " + debugInfo);
                                    }
                                    else
                                    {
                                        ImGui.SetWindowFontScale(1.5f);
                                        ImGui.TextUnformatted(selectedPlayer.Key.Split(' ')[0] + " " + Translator.LocalizeUI("has the same collection as your main character.\r\nPlease give them a unique collection in Penumbra, or drag onto your main character.") + " " + debugInfo);
                                    }
                                    ImGui.SetWindowFontScale(1f);
                                }
                                else
                                {
                                    ImGui.TextUnformatted(Translator.LocalizeUI("Dragging onto no character.") + debugInfo);
                                }
                            }
                            catch
                            {
                                ImGui.TextUnformatted(Translator.LocalizeUI("Dragging texture on unknown:") + $"\n\t{string.Join("\n\t", m.Files.Select(Path.GetFileName))}");
                            }
                            AllowClickthrough = false;
                        }
                        catch (Exception e)
                        {
                            plugin.PluginLog.Warning(e, e.Message);
                            ImGui.TextUnformatted(Translator.LocalizeUI("Penumbra is not installed. Or error occured."));
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
                            ImGui.Image(textureWrap.Handle, new Vector2(ImGui.GetMainViewport().Size.X, ImGui.GetMainViewport().Size.Y));
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
                        LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory = _textureProcessor.BasePath;
                        List<TextureSet> textureSets = new List<TextureSet>();
                        plugin.PluginLog.Information("[Drag And Drop Debug] Drop event triggered, selectedPlayer: " + (selectedPlayer.Value != null));
                        if (selectedPlayer.Value != null && selectedPlayerCollection != mainPlayerCollection ||
                            selectedPlayer.Value == plugin.SafeGameObjectManager.LocalPlayer)
                        {
                            plugin.PluginLog.Information("[Drag And Drop Debug] Valid player target, getting customization...");
                            modName = selectedPlayer.Key + " Texture Mod";
                            _currentCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(selectedPlayer.Value);
                            plugin.PluginLog.Information("[Drag And Drop Debug] Customization retrieved! Starting task...");
                            Task.Run(async () =>
                            {
                                try
                                {
                                    HashSet<string> droppedCategories = new HashSet<string>();
                                    var psdFiles = files.Where(f => Path.GetExtension(f).Equals(".psd", StringComparison.OrdinalIgnoreCase)).ToList();
                                    var clmpFiles = files.Where(f => Path.GetExtension(f).Equals(".clmp", StringComparison.OrdinalIgnoreCase)).ToList();
                                    files = files.Where(f => !Path.GetExtension(f).Equals(".psd", StringComparison.OrdinalIgnoreCase)).ToList();

                                    if (psdFiles.Count > 0)
                                    {
                                        var capturedPlayer = selectedPlayer;
                                        var capturedPart = bodyDragPart;
                                        plugin.PsdImportWindow.StartImport(psdFiles[0], extractedPngs =>
                                        {
                                            return InjectFilesAndRebuild(extractedPngs, capturedPlayer, capturedPart);
                                        });
                                    }

                                    if (clmpFiles.Count > 0)
                                    {
                                        foreach (var clmpFile in clmpFiles)
                                        {
                                            plugin.ContextualLayerManager.ImportLayerFromFile(clmpFile, true);
                                        }
                                        return;
                                    }

                                    if (files.Count == 0) return;

                                    foreach (var file in files)
                                    {
                                        if (!ValidTextureExtensions.Contains(Path.GetExtension(file))) continue;
                                        string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                                        string categoryKey = selectedPlayer.Key + "_";
                                        if (bodyDragPart == BodyDragPart.Clothing)
                                        {
                                            string gearSlot = GetGearSlotFromBone(_closestBone) ?? "body";
                                            categoryKey += "gear_" + gearSlot;
                                        }
                                        else if (fileName.Contains("_gear_"))
                                        {
                                            string slot = "body";
                                            foreach (var s in new[] { "head", "body", "hands", "legs", "feet", "ears", "neck", "wrists", "ring_l", "ring_r" })
                                            {
                                                if (fileName.Contains("_gear_" + s))
                                                {
                                                    slot = s;
                                                    break;
                                                }
                                            }
                                            categoryKey += "gear_" + slot;
                                        }
                                        else if (fileName.Contains("eyebrow") || fileName.Contains("lash")) categoryKey += "eyebrows";
                                        else if (fileName.Contains("eye")) categoryKey += "eyes";
                                        else if (fileName.Contains("face") || fileName.Contains("makeup")) categoryKey += "face";
                                        else if (fileName.Contains("mata") || fileName.Contains("amat") || fileName.Contains("materiala") || fileName.Contains("gen2") ||
                                            fileName.Contains("bibo") || fileName.Contains("b+") ||
                                            fileName.Contains("gen3") || fileName.Contains("tbse")) categoryKey += "body";
                                        else
                                        {
                                            switch (bodyDragPart)
                                            {
                                                case BodyDragPart.Body: categoryKey += "body"; break;
                                                case BodyDragPart.Face: categoryKey += "face"; break;
                                                case BodyDragPart.Eyes: categoryKey += "eyes"; break;
                                                case BodyDragPart.EyebrowsAndLashes: categoryKey += "eyebrows"; break;
                                                default: categoryKey += "fallback_" + bodyDragPart.ToString(); break;
                                            }
                                        }

                                        droppedCategories.Add(categoryKey);
                                    }

                                    if (!plugin.Configuration.EnableTextureStacking)
                                    {
                                        foreach (var cat in droppedCategories)
                                        {
                                            _textureHistory[cat] = new List<string>();
                                        }
                                    }

                                    foreach (var file in files)
                                    {
                                        if (!ValidTextureExtensions.Contains(Path.GetExtension(file))) continue;
                                        string f = file;
                                        string fileName = Path.GetFileNameWithoutExtension(f).ToLower();
                                        string categoryKey = selectedPlayer.Key + "_";
                                        bool isBody = false;
                                        if (bodyDragPart == BodyDragPart.Clothing)
                                        {
                                            string gearSlot = GetGearSlotFromBone(_closestBone) ?? "body";
                                            categoryKey += "gear_" + gearSlot;
                                        }
                                        else if (fileName.Contains("_gear_"))
                                        {
                                            string slot = "body";
                                            foreach (var s in new[] { "head", "body", "hands", "legs", "feet", "ears", "neck", "wrists", "ring_l", "ring_r" })
                                            {
                                                if (fileName.Contains("_gear_" + s))
                                                {
                                                    slot = s;
                                                    break;
                                                }
                                            }
                                            categoryKey += "gear_" + slot;
                                        }
                                        else if (fileName.Contains("eyebrow") || fileName.Contains("lash")) categoryKey += "eyebrows";
                                        else if (fileName.Contains("eye")) categoryKey += "eyes";
                                        else if (fileName.Contains("face") || fileName.Contains("makeup")) categoryKey += "face";
                                        else if (fileName.Contains("mata") || fileName.Contains("amat") || fileName.Contains("materiala") || fileName.Contains("gen2") ||
                                            fileName.Contains("bibo") || fileName.Contains("b+") ||
                                            fileName.Contains("gen3") || fileName.Contains("tbse")) { categoryKey += "body"; isBody = true; }
                                        else
                                        {
                                            switch (bodyDragPart)
                                            {
                                                case BodyDragPart.Body: categoryKey += "body"; isBody = true; break;
                                                case BodyDragPart.Face: categoryKey += "face"; break;
                                                case BodyDragPart.Eyes: categoryKey += "eyes"; break;
                                                case BodyDragPart.EyebrowsAndLashes: categoryKey += "eyebrows"; break;
                                                default: categoryKey += "fallback_" + bodyDragPart.ToString(); break;
                                            }
                                        }

                                        bool isFace = categoryKey.EndsWith("face");
                                        bool needsClassification = false;

                                        // Completely unknown drag drop destination?
                                        if (!isBody && !isFace && !categoryKey.Contains("gear_"))
                                            needsClassification = true;

                                        // Body UV layout missing?
                                        if (isBody && !fileName.Contains("bibo") && !fileName.Contains("b+") && !fileName.Contains("gen3") && !fileName.Contains("tbse") && !fileName.Contains("gen2") && !fileName.Contains("mata") && !fileName.Contains("amat"))
                                            needsClassification = true;

                                        // Map type missing?
                                        if ((isBody || isFace || needsClassification) && !fileName.Contains("norm") && !fileName.EndsWith("_n") && !fileName.Contains("_n_") && !fileName.Contains("mask") && !fileName.EndsWith("_m") && !fileName.Contains("_m_") && !fileName.Contains("base") && !fileName.Contains("diffuse") && !fileName.EndsWith("_d") && !fileName.Contains("_d_") && !fileName.Contains("glow") && !fileName.EndsWith("_g") && !fileName.Contains("_g_"))
                                            needsClassification = true;

                                        if (needsClassification)
                                        {
                                            _classificationTcs = new TaskCompletionSource<string>();
                                            _fileToClassify = f;
                                            _classificationIsBody = isBody;
                                            // Automatically select Face radio if dropped on face
                                            if (isFace) _classificationSelectedUV = 4;
                                            else if (!isBody) _classificationSelectedUV = 0; // Default to Bibo+ if unknown
                                            
                                            _showClassificationPopup = true;
                                            string format = _classificationTcs.Task.Result;
                                            if (!string.IsNullOrEmpty(format))
                                            {
                                                string[] parts = format.Split('|');
                                                string prefix = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) ? parts[0] + "_" : "";
                                                string suffix = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? "_" + parts[1] : "";

                                                // Reroute categoryKey if user manually selected a specific target
                                                if (parts[0] == "face")
                                                {
                                                    categoryKey = selectedPlayer.Key + "_face";
                                                    isFace = true;
                                                    isBody = false;
                                                }
                                                else if (!string.IsNullOrEmpty(parts[0]))
                                                {
                                                    categoryKey = selectedPlayer.Key + "_body";
                                                    isBody = true;
                                                    isFace = false;
                                                }

                                                string newPath = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, prefix + Path.GetFileNameWithoutExtension(f) + suffix + Path.GetExtension(f));
                                                File.Copy(f, newPath, true);
                                                f = newPath;
                                            }
                                            else
                                            {
                                                continue; // They cancelled the popup
                                            }
                                        }

                                        if (!_textureHistory.ContainsKey(categoryKey))
                                        {
                                            _textureHistory[categoryKey] = new List<string>();
                                            _textureHistoryTints[categoryKey] = new List<System.Numerics.Vector4>();
                                        }
                                        _textureHistory[categoryKey].Add(f);
                                        _textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
                                        droppedCategories.Add(categoryKey);
                                        Plugin.Configuration.Save();
                                        UpdateWatchers();
                                    }

                                    int effectiveRace = RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1);

                                    foreach (var categoryKey in droppedCategories)
                                    {
                                        if (!_textureHistory.ContainsKey(categoryKey) || _textureHistory[categoryKey].Count == 0) continue;
                                        string lastFile = _textureHistory[categoryKey].First();
                                        TextureSet item = null;
                                        string categoryModName = "";
                                        string overrideType = "";

                                        if (categoryKey.EndsWith("_body"))
                                        {
                                            if (_currentCustomization.Customize.Race.Value - 1 == 2)
                                            {
                                                item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 5,
                                                effectiveRace,
                                                _currentCustomization.Customize.TailShape.Value - 1, false);
                                            }
                                            else if (_currentCustomization.Customize.Gender.Value == 0)
                                            {
                                                item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 3,
                                                effectiveRace,
                                                _currentCustomization.Customize.TailShape.Value - 1, false);
                                            }
                                            else
                                            {
                                                item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, DetectBaseBodyType(lastFile, selectedPlayerCollection, _currentCustomization.Customize.Gender.Value),
                                                effectiveRace,
                                                _currentCustomization.Customize.TailShape.Value - 1, false);
                                            }
                                            categoryModName = "Body";
                                            if (item != null) item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                        }
                                        else if (categoryKey.EndsWith("_eyebrows"))
                                        {
                                            item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 1, 0,
                                             _currentCustomization.Customize.Gender.Value,
                                             effectiveRace,
                                             _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                            categoryModName = "Eyebrows";
                                            overrideType = "Normal";
                                        }
                                        else if (categoryKey.EndsWith("_eyes"))
                                        {
                                            item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 2, 0,
                                            _currentCustomization.Customize.Gender.Value,
                                            effectiveRace,
                                            _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                            categoryModName = "Eyes";
                                            overrideType = "Base";
                                        }
                                        else if (categoryKey.EndsWith("_face"))
                                        {
                                            item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 0, 0,
                                            _currentCustomization.Customize.Gender.Value,
                                            effectiveRace,
                                            _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                            categoryModName = "Face";
                                        }
                                        else
                                        {
                                            switch (bodyDragPart)
                                            {
                                                case BodyDragPart.Body:
                                                    if (_currentCustomization.Customize.Race.Value - 1 == 2)
                                                    {
                                                        item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 5,
                                                        effectiveRace,
                                                        _currentCustomization.Customize.TailShape.Value - 1, false);
                                                    }
                                                    else if (_currentCustomization.Customize.Gender.Value == 0)
                                                    {
                                                        item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 3,
                                                        effectiveRace,
                                                        _currentCustomization.Customize.TailShape.Value - 1, false);
                                                    }
                                                    else
                                                    {
                                                        item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, DetectBaseBodyType(lastFile, selectedPlayerCollection, _currentCustomization.Customize.Gender.Value),
                                                        effectiveRace,
                                                        _currentCustomization.Customize.TailShape.Value - 1, false);
                                                    }
                                                    categoryModName = "Body";
                                                    if (item != null) item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                                                    break;
                                                case BodyDragPart.Face:
                                                    item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 0, 0,
                                                    _currentCustomization.Customize.Gender.Value,
                                                    effectiveRace,
                                                    _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                                    categoryModName = "Face";
                                                    break;
                                                case BodyDragPart.Eyes:
                                                    item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 2, 0,
                                                    _currentCustomization.Customize.Gender.Value,
                                                    effectiveRace,
                                                    _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                                    categoryModName = "Eyes";
                                                    overrideType = "Normal";
                                                    break;
                                                case BodyDragPart.EyebrowsAndLashes:
                                                    item = ProjectHelper.CreateFaceTextureSet(_currentCustomization.Customize.Face.Value - 1, 1, 0,
                                                    _currentCustomization.Customize.Gender.Value,
                                                    effectiveRace,
                                                    _currentCustomization.Customize.Clan.Value - 1, 0, false);
                                                    categoryModName = "Eyebrows";
                                                    overrideType = "Normal";
                                                    break;
                                            }
                                        }

                                        if (item != null)
                                        {
                                            ApplyDefaultSkinType(item);
                                            for (int _i = 0; _i < _textureHistory[categoryKey].Count; _i++)
                                            {
                                                string f = _textureHistory[categoryKey][_i];
                                                System.Numerics.Vector4? t = _textureHistoryTints.ContainsKey(categoryKey) && _i < _textureHistoryTints[categoryKey].Count ? _textureHistoryTints[categoryKey][_i] : null;
                                                AddToTextureSet(item, f, overrideType, t);
                                            }
                                            // Composite active contextual layers on top of drag-and-drop textures
                                            if (plugin.ContextualLayerManager != null && selectedPlayer.Key == plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue)
                                            {
                                                foreach (var activeLayer in plugin.ContextualLayerManager.GetActiveLayers())
                                                {
                                                    if (categoryKey.EndsWith("_" + activeLayer.LayerDef.TargetBodyPart.ToLower()))
                                                    {
                                                        for (int layerIdx = 0; layerIdx < activeLayer.CurrentStackCount; layerIdx++)
                                                        {
                                                            if (layerIdx < activeLayer.CachedTexturePaths.Count && File.Exists(activeLayer.CachedTexturePaths[layerIdx]))
                                                            {
                                                                AddToTextureSet(item, activeLayer.CachedTexturePaths[layerIdx], overrideType);
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                            plugin.PluginLog.Information($"[Glow Debug] TextureSet '{item.TextureSetName}': Base='{item.Base}', Normal='{item.Normal}', Mask='{item.Mask}', Glow='{item.Glow}', Material='{item.Material}', InternalMtrl='{item.InternalMaterialPath}'");
                                            textureSets.Add(item);
                                            
                                            string singleModName = selectedPlayer.Key + " Texture Mod";
                                            singleModName = singleModName.Replace("Mod", categoryModName);
                                            string singleFullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), singleModName);
                                            
                                            List<TextureSet> singleList = new List<TextureSet>() { item };
                                            await Export(true, singleList, singleFullModPath, singleModName, selectedPlayer);
                                        }
                                    }

                                    if (textureSets.Count == 0)
                                    {
                                        Plugin.Framework.RunOnFrameworkThread(() =>
                                        {
                                            plugin.Chat.PrintError("[Drag And Drop Texturing] Unable to identify texture type! If its a transparent texture please include descriptors in the file name (IE: filename_bibo_base.png, filename_gen3_base.png, filename_gen2_base.png, etc)");
                                        });
                                    }
                                }
                                catch (Exception e)
                                {
                                    plugin.PluginLog.Error($"[Drag And Drop Texturing] Crash during generation: {e.Message}");
                                    plugin.PluginLog.Warning(e, e.Message);
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

        public void RefreshActiveOverrides()
        {
            if (!Plugin.Configuration.UsePriorityBodyMod) return;
            Task.Run(() =>
            {
                try
                {
                    ActiveBodyOverrides.Clear();
                    if (plugin?.SafeGameObjectManager?.LocalPlayer == null) return;

                    var character = Plugin.ObjectTable.LocalPlayer as ICharacter;
                    if (character == null) return;

                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                    var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                    int useClan = customization.Customize.Clan.Value - 1;
                    int useGender = customization.Customize.Gender.Value;

                    // Ensure OriginalBaseDirectory is set so FastUVTransfer can find its transfer maps
                    string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                    if (string.IsNullOrEmpty(LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory) ||
                        LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory == System.AppDomain.CurrentDomain.BaseDirectory)
                    {
                        LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory = modPath + @"\LooseTextureCompilerDLC";
                    }

                    PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(collection, useGender, useClan, plugin);

                    if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride != null && !string.IsNullOrEmpty(FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.ModName))
                        ActiveBodyOverrides["Bibo+"] = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.ModName;
                    if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override != null && !string.IsNullOrEmpty(FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.ModName))
                        ActiveBodyOverrides["Gen3"] = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.ModName;
                    if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen2Override != null && !string.IsNullOrEmpty(FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen2Override.ModName))
                        ActiveBodyOverrides["Vanilla/Gen2"] = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen2Override.ModName;
                    if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride != null && !string.IsNullOrEmpty(FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.ModName))
                        ActiveBodyOverrides["TBSE"] = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.ModName;
                    if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OtopopOverride != null && !string.IsNullOrEmpty(FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OtopopOverride.ModName))
                        ActiveBodyOverrides["Otopop"] = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OtopopOverride.ModName;
                }
                catch { }
            });
        }

        private readonly System.Threading.SemaphoreSlim _exportSemaphore = new System.Threading.SemaphoreSlim(1, 1);

        public async Task<bool> Export(bool finalize, List<TextureSet> exportTextureSets, string path,
            string name, KeyValuePair<string, ICharacter> character, int overrideRace = -1, int overrideClan = -1, int overrideGender = -1, int overrideFace = -1, bool isContextual = false)
        {
            await _exportSemaphore.WaitAsync();
            try
            {
                System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                sw.Start();
                plugin.PluginLog.Information("[Drag And Drop Debug] Export started!");
                _lockDuplicateGeneration = true;

                plugin.PluginLog.Information("[Drag And Drop Texturing] Processing textures, please wait.");
                string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                _textureProcessor.BasePath = modPath + @"\LooseTextureCompilerDLC";
                LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory = _textureProcessor.BasePath;
                _exportStatus = "Initializing";
                _currentTarget = character.Value;
                _hideProgressUI = isContextual;
                Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.Value.ObjectIndex).Item3.Id;
                bool requiresFullRedraw = false;

                // Extract currently equipped textures from Penumbra to use as underlay
                try
                {
                    // Use overridden customization if provided (from RebuildCategory), otherwise read live
                    int useRace, useClan, useGender, useFace;
                    if (overrideRace != -1)
                    {
                        useRace = overrideRace;
                        useClan = overrideClan;
                        useGender = overrideGender;
                        useFace = overrideFace;
                    }
                    else
                    {
                        var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character.Value);
                        useRace = customization.Customize.Race.Value - 1;
                        useClan = customization.Customize.Clan.Value - 1;
                        useGender = customization.Customize.Gender.Value;
                        useFace = customization.Customize.Face.Value - 1;
                    }
                    string raceCode = PenumbraAndGlamourerHelperFunctions.ModelRaceToRaceCode(useRace, useClan, useGender);
                    string subRaceName = PenumbraAndGlamourerHelperFunctions.SubRaceToSubRaceName(useRace, useClan);
                    plugin.PluginLog.Information($"[Drag And Drop Debug] Export customization: Race={useRace}, Clan={useClan}, Face={useFace}, RaceCode={raceCode}");

                    PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(collection, useGender, useClan, plugin);
                    foreach (var i in exportTextureSets)
                    {
                        string category = "";
                        string tName = i.TextureSetName.ToLower();
                        string tPath = i.InternalBasePath.ToLower();
                        plugin.PluginLog.Information($"[Drag And Drop Debug] TextureSet '{i.TextureSetName}' InternalBase: {i.InternalBasePath}");

                        if (tName.Contains("body") || tName.Contains("bibo") || tName.Contains("gen3") || tName.Contains("tbse") || tPath.Contains("obj/body") || tPath.Contains("bibo") || tPath.Contains("otopop") || tPath.Contains("asym lala") || tPath.Contains("relala"))
                            category = "_body";
                        else if (tName.Contains("eyebrows"))
                        {
                            category = "_eyebrows";
                            requiresFullRedraw = true;
                        }
                        else if (tName.Contains("eyes") || tPath.Contains("eye"))
                        {
                            category = "_eyes";
                            requiresFullRedraw = true;
                        }
                        else if (tName.Contains("face") || tPath.Contains("obj/face"))
                        {
                            category = "_face";
                            requiresFullRedraw = true;
                        }
                        else if (tPath.Contains("obj/equipment") || tPath.Contains("chara/equipment") || tName.Contains("gear"))
                        {
                            category = "_gear";
                            requiresFullRedraw = true;
                        }

                        if (!string.IsNullOrEmpty(category))
                        {
                            // Dynamically update the face paths based on current face geometry to fix Au Ra UV mismatch
                            if (category == "_face")
                            {
                                BackupTexturePaths.AddFaceBackupPaths(useGender, useClan, useFace, i);
                            }

                            plugin.PluginLog.Information($"[Drag And Drop Debug] Extracting underlay for {category}...");
                            string baseTex, normTex, maskTex;
                            PenumbraAndGlamourerHelperFunctions.ExtractActiveTextureFromPenumbra(collection, category, raceCode, subRaceName, out _, out baseTex, out normTex, out maskTex, plugin, i);

                            // Refresh OmniOverrides now that they've been loaded
                            if (category == "_body")
                            {
                                int mainRace = RaceInfo.SubRaceToMainRace(useClan);
                                BackupTexturePaths.AddBodyBackupPaths(useGender, mainRace, i);
                            }

                            bool usedLuminaBase = false;
                            bool usedLuminaNorm = false;

                            // Lumina fallback: if no modded textures found AND OmniOverrides didn't provide one
                            if (string.IsNullOrEmpty(baseTex) && !string.IsNullOrEmpty(i.InternalBasePath) && (i.BackupTexturePaths == null || string.IsNullOrEmpty(i.BackupTexturePaths.Base)))
                            {
                                baseTex = ExtractVanillaTexViaLumina(i.InternalBasePath, i);
                                usedLuminaBase = true;
                                if (!string.IsNullOrEmpty(baseTex))
                                    plugin.PluginLog.Information($"[Drag And Drop Debug] Vanilla fallback base: {i.InternalBasePath}");
                            }
                            if (string.IsNullOrEmpty(normTex) && !string.IsNullOrEmpty(i.InternalNormalPath) && (i.BackupTexturePaths == null || string.IsNullOrEmpty(i.BackupTexturePaths.Normal)))
                            {
                                normTex = ExtractVanillaTexViaLumina(i.InternalNormalPath, i);
                                usedLuminaNorm = true;
                                if (!string.IsNullOrEmpty(normTex))
                                    plugin.PluginLog.Information($"[Drag And Drop Debug] Vanilla fallback normal: {i.InternalNormalPath}");
                            }
                            if (string.IsNullOrEmpty(maskTex) && !string.IsNullOrEmpty(i.InternalMaskPath))
                            {
                                maskTex = ExtractVanillaTexViaLumina(i.InternalMaskPath, i);
                                if (!string.IsNullOrEmpty(maskTex))
                                    plugin.PluginLog.Information($"[Drag And Drop Debug] Vanilla fallback mask: {i.InternalMaskPath}");
                            }

                            // Update BackupTexturePaths so the assembly pipeline
                            // uses the freshly-extracted textures (not stale static presets)
                            // This is required for all categories so the extracted normal map is preserved.
                            bool hasValidBase = !string.IsNullOrEmpty(baseTex);
                            bool hasValidNorm = !string.IsNullOrEmpty(normTex);

                            string finalBase = i.BackupTexturePaths != null ? i.BackupTexturePaths.Base : "";
                            string finalNorm = i.BackupTexturePaths != null ? i.BackupTexturePaths.Normal : "";

                            // We must always overwrite finalBase/finalNorm with the newly extracted textures.
                            // The static presets in BackupTexturePaths point to dead 'res\textures\...' paths 
                            // which are only valid in the standalone compiler, not the runtime plugin.
                            if (hasValidBase)
                                finalBase = baseTex;
                            if (hasValidNorm)
                                finalNorm = normTex;

                            if (!string.IsNullOrEmpty(finalBase) && !string.IsNullOrEmpty(finalNorm))
                                i.BackupTexturePaths = new BackupTexturePaths(finalBase, finalNorm);
                            else if (!string.IsNullOrEmpty(finalBase))
                                i.BackupTexturePaths = new BackupTexturePaths(finalBase);
                        }
                    }
                }
                catch (Exception ex)
                {
                    plugin.PluginLog.Error("[Drag And Drop Texturing] Error during underlay extraction: " + ex.Message);
                }

                float exportScale = Plugin.Configuration.ExportScale;
                if (character.Value != null)
                {
                    try
                    {
                        unsafe
                        {
                            var cameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
                            if (cameraManager != null && cameraManager->CurrentCamera != null)
                            {
                                var camPos = cameraManager->CurrentCamera->Object.Position;
                                var playerPos = character.Value.Position;
                                float distance = System.Numerics.Vector3.Distance(camPos, playerPos);

                                plugin.PluginLog.Information($"[Drag And Drop Texturing] Camera Distance = {distance}m. Auto Quality Enabled = {Plugin.Configuration.AutoDistanceExportQuality}");

                                if (Plugin.Configuration.AutoDistanceExportQuality)
                                {
                                    // Scale based on distance. 
                                    if (distance < 2.5f) exportScale = 1.0f; // Close up, High Quality
                                    else if (distance < 6.0f) exportScale = 0.5f; // Mid-range, Half Quality
                                    else exportScale = 0.25f; // Far away, Quarter Quality

                                    plugin.PluginLog.Information($"[Drag And Drop Texturing] Auto Distance Export Quality: Scale applied = {exportScale}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        plugin.PluginLog.Error("[Drag And Drop Texturing] Camera distance check failed: " + ex.Message);
                    }
                }
                _textureProcessor.ExportScale = exportScale;
                ProjectHelper.ExportProject(path, name, exportTextureSets, _textureProcessor, _xNormalPath, 3, Plugin.Configuration.GenerateNormals, false, true, Plugin.Configuration.ExportCompression == 1);

                long compilationTime = sw.ElapsedMilliseconds;
                plugin.PluginLog.Information($"[Drag And Drop Texturing] Texture generation complete in {compilationTime}ms, beginning Penumbra update...");

                // Penumbra IPC calls do not require the framework thread
                PenumbraAndGlamourerIpcWrapper.Instance.AddMod.Invoke(name);
                PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(path, name);
                collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.Value.ObjectIndex).Item3.Id;
                PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, path, true, name);
                PenumbraAndGlamourerIpcWrapper.Instance.TrySetModPriority.Invoke(collection, path, 100, name);
                var settings = PenumbraAndGlamourerIpcWrapper.Instance.GetCurrentModSettings.Invoke(collection, path, name, true);
                foreach (var group in settings.Item2.Value.Item3)
                {
                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetModSetting.Invoke(collection, path, group.Key, "Enable", name);
                }

                // Delay to allow Penumbra's filesystem watchers and IPC to fully process ReloadMod.
                Thread.Sleep(200);


                // Only Glamourer operations need the framework thread
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character.Value);
                    if (customization != null && customization.Equipment != null)
                    {
                        if (name.Contains("face", StringComparison.OrdinalIgnoreCase) || 
                            name.Contains("eyes", StringComparison.OrdinalIgnoreCase) || 
                            name.Contains("eyebrow", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("hair", StringComparison.OrdinalIgnoreCase))
                        {
                            PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.Value.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                        }
                        else if (name.Contains("head", StringComparison.OrdinalIgnoreCase) || name.Contains("hat", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Head, (ulong)customization.Equipment.Head.ItemId, new List<byte> { (byte)customization.Equipment.Head.Stain });
                        else if (name.Contains("hands", StringComparison.OrdinalIgnoreCase) || name.Contains("glv", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, (ulong)customization.Equipment.Hands.ItemId, new List<byte> { (byte)customization.Equipment.Hands.Stain });
                        else if (name.Contains("legs", StringComparison.OrdinalIgnoreCase) || name.Contains("dwn", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, (ulong)customization.Equipment.Legs.ItemId, new List<byte> { (byte)customization.Equipment.Legs.Stain });
                        else if (name.Contains("feet", StringComparison.OrdinalIgnoreCase) || name.Contains("sho", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, (ulong)customization.Equipment.Feet.ItemId, new List<byte> { (byte)customization.Equipment.Feet.Stain });
                        else if (name.Contains("ears", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Ears, (ulong)customization.Equipment.Ears.ItemId, new List<byte> { (byte)customization.Equipment.Ears.Stain });
                        else if (name.Contains("neck", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Neck, (ulong)customization.Equipment.Neck.ItemId, new List<byte> { (byte)customization.Equipment.Neck.Stain });
                        else if (name.Contains("wrists", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Wrists, (ulong)customization.Equipment.Wrists.ItemId, new List<byte> { (byte)customization.Equipment.Wrists.Stain });
                        else if (name.Contains("ring_r", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.RFinger, (ulong)customization.Equipment.RFinger.ItemId, new List<byte> { (byte)customization.Equipment.RFinger.Stain });
                        else if (name.Contains("ring_l", StringComparison.OrdinalIgnoreCase))
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.LFinger, (ulong)customization.Equipment.LFinger.ItemId, new List<byte> { (byte)customization.Equipment.LFinger.Stain });
                        else
                        {
                            // A body texture update (e.g. Bibo+) affects multiple equipment slots since the mesh is often split.
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Body, (ulong)customization.Equipment.Body.ItemId, new List<byte> { (byte)customization.Equipment.Body.Stain });
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, (ulong)customization.Equipment.Legs.ItemId, new List<byte> { (byte)customization.Equipment.Legs.Stain });
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, (ulong)customization.Equipment.Feet.ItemId, new List<byte> { (byte)customization.Equipment.Feet.Stain });
                            PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, (ulong)customization.Equipment.Hands.ItemId, new List<byte> { (byte)customization.Equipment.Hands.Stain });
                        }
                    }
                });

                plugin.PluginLog.Information("[Drag And Drop Texturing] Import complete! Created mod is toggleable in penumbra.");
                sw.Stop();
                plugin.PluginLog.Information($"[Drag & Drop] Generated in {compilationTime}ms. Total Export+IPC: {sw.ElapsedMilliseconds}ms.");
            }
            finally
            {
                _lockDuplicateGeneration = false;
                _exportSemaphore.Release();
            }
            return true;
        }

        public void Dispose()
        {
            if (plugin != null)
            {
                PenumbraAndGlamourerIpcWrapper.Instance.OnGlamourerStateChanged -= OnGlamourerStateChanged;
                PenumbraAndGlamourerIpcWrapper.Instance.OnModSettingChanged -= OnModSettingChanged;
            }
            _regenerationDebounce?.Dispose();
        }

        private void TryInitialRebuild()
        {
            try
            {
                // Wait for Penumbra IPC to be fully ready (mod list + collection resolution)
                bool penumbraReady = false;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    Thread.Sleep(3000);
                    try
                    {
                        if (plugin?.SafeGameObjectManager?.LocalPlayer == null) continue;
                        var character = Plugin.ObjectTable.LocalPlayer as ICharacter;
                        if (character == null) continue;

                        // Verify Penumbra can return mod data
                        var mods = PenumbraAndGlamourerIpcWrapper.Instance.GetModList.Invoke();
                        var collectionResult = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex);
                        if (mods.Count > 0 && collectionResult.Item3.Id != Guid.Empty)
                        {
                            penumbraReady = true;
                            break;
                        }
                        plugin?.PluginLog?.Information($"[Drag And Drop Debug] Penumbra not ready yet (attempt {attempt + 1}/5, mods={mods.Count})");
                    }
                    catch
                    {
                        plugin?.PluginLog?.Information($"[Drag And Drop Debug] Penumbra IPC not available yet (attempt {attempt + 1}/5)");
                    }
                }

                if (!penumbraReady) return;
                if (_textureHistory.Count == 0) return;

                string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                var charKeys = _textureHistory.Keys.Where(k => k.StartsWith(charName + "_") && _textureHistory[k].Count > 0).ToList();
                if (charKeys.Count == 0) return;

                // Snapshot the current customization
                var character2 = Plugin.ObjectTable.LocalPlayer as ICharacter;
                if (character2 == null) return;
                var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character2);
                if (Plugin.Configuration.LastKnownFace == customization.Customize.Face.Value &&
                    Plugin.Configuration.LastKnownRace == customization.Customize.Race.Value &&
                    Plugin.Configuration.LastKnownClan == customization.Customize.Clan.Value &&
                    Plugin.Configuration.LastKnownGender == customization.Customize.Gender.Value)
                {
                    plugin.PluginLog.Information("[Drag And Drop Texturing] Initial rebuild skipped: Customization exactly matches last session.");

                    // Repopulate GearCategoryMeta so the UI retains the human-readable names for currently worn gear
                    try
                    {
                        var wornPieces = DragAndDropTexturing.Equipment.WornEquipmentResolver.ResolveWornGear(character2, plugin);
                        foreach (var piece in wornPieces)
                        {
                            string categoryKey = $"{charName}_gear_{piece.SlotKey}" +
                                (string.IsNullOrEmpty(piece.MaterialName) ? "" : "_" + piece.MaterialName) +
                                (string.IsNullOrEmpty(piece.ModName) ? "" : "_[" + piece.ModName + "]");
                            _gearCategoryMeta[categoryKey] = piece;
                        }
                    }
                    catch { }

                    return;
                }

                Plugin.Configuration.LastKnownFace = customization.Customize.Face.Value;
                Plugin.Configuration.LastKnownRace = customization.Customize.Race.Value;
                Plugin.Configuration.LastKnownClan = customization.Customize.Clan.Value;
                Plugin.Configuration.LastKnownGender = customization.Customize.Gender.Value;
                Plugin.Configuration.Save();

                plugin.PluginLog.Information("[Drag And Drop Texturing] Rebuilding " + charKeys.Count + " texture categories for " + charName + "...");
                foreach (var key in charKeys)
                {
                    // Wait for any previous rebuild to finish before starting the next
                    int waitAttempts = 0;
                    while (_lockDuplicateGeneration && waitAttempts < 60)
                    {
                        Thread.Sleep(1000);
                        waitAttempts++;
                    }
                    RebuildCategory(key);
                    // Give the Task.Run inside RebuildCategory time to start and set the lock
                    Thread.Sleep(500);
                }
            }
            catch (Exception e)
            {
                plugin?.PluginLog?.Warning(e, "Failed initial texture rebuild");
            }
        }

        private void OnGlamourerStateChanged(object sender, GlamourerStateChangedEventArgs e)
        {
            try
            {
                if (_lockDuplicateGeneration) return;
                if (plugin?.SafeGameObjectManager?.LocalPlayer == null) return;
                if (e.GameObjectPtr != plugin.SafeGameObjectManager.LocalPlayer.Address) return;

                var character = Plugin.ObjectTable.LocalPlayer as ICharacter;
                if (character == null) return;

                var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                string charName = character.Name.TextValue;

                int newRace = customization.Customize.Race.Value;
                int newClan = customization.Customize.Clan.Value;
                int newGender = customization.Customize.Gender.Value;
                int newFace = customization.Customize.Face.Value;

                bool faceChanged = Plugin.Configuration.LastKnownFace != -1 && Plugin.Configuration.LastKnownFace != newFace;
                bool raceChanged = Plugin.Configuration.LastKnownRace != -1 && (Plugin.Configuration.LastKnownRace != newRace || Plugin.Configuration.LastKnownClan != newClan);
                bool genderChanged = Plugin.Configuration.LastKnownGender != -1 && Plugin.Configuration.LastKnownGender != newGender;

                bool bodyPathChanged = true;
                if (Plugin.Configuration.LastKnownRace != -1 && Plugin.Configuration.LastKnownGender != -1 && Plugin.Configuration.LastKnownClan != -1)
                {
                    int oldMappedRace = FFXIVLooseTextureCompiler.Racial.RaceInfo.SubRaceToMainRace(Plugin.Configuration.LastKnownClan - 1);
                    int newMappedRace = FFXIVLooseTextureCompiler.Racial.RaceInfo.SubRaceToMainRace(newClan - 1);

                    // Check if the underlying game path for the body actually changed between the two states
                    string oldBody = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(0, Plugin.Configuration.LastKnownGender, 0, oldMappedRace, 0, false);
                    string newBody = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(0, newGender, 0, newMappedRace, 0, false);
                    if (oldBody == newBody) bodyPathChanged = false;
                }

                // Update tracked state
                Plugin.Configuration.LastKnownFace = newFace;
                Plugin.Configuration.LastKnownRace = newRace;
                Plugin.Configuration.LastKnownClan = newClan;
                Plugin.Configuration.LastKnownGender = newGender;
                Plugin.Configuration.Save();

                List<string> partsToRegenerate = new List<string>();

                if (raceChanged || genderChanged)
                {
                    if (bodyPathChanged)
                    {
                        plugin.PluginLog.Information("[Drag And Drop Texturing] Race/Gender body texture path changed. Rebuilding body...");
                        partsToRegenerate.Add("_body");
                    }
                    else
                    {
                        plugin.PluginLog.Information("[Drag And Drop Texturing] Race changed but body texture path is identical. Skipping body rebuild.");
                    }
                    partsToRegenerate.Add("_face");
                    partsToRegenerate.Add("_eyes");
                    partsToRegenerate.Add("_eyebrows");
                }
                else if (faceChanged)
                {
                    plugin.PluginLog.Information("[Drag And Drop Texturing] Face change detected. Rebuilding face and eye textures...");
                    partsToRegenerate.Add("_face");
                    partsToRegenerate.Add("_eyes");
                    partsToRegenerate.Add("_eyebrows");
                }

                if (partsToRegenerate.Count > 0)
                {
                    ScheduleRegeneration(charName, partsToRegenerate.ToArray());
                }
            }
            catch (Exception ex)
            {
                plugin?.PluginLog?.Warning(ex, "Error in Glamourer state change handler");
            }
        }

        private void OnModSettingChanged(object sender, ModSettingChangedEventArgs e)
        {
            try
            {
                if (_lockDuplicateGeneration) return;
                if (plugin?.SafeGameObjectManager?.LocalPlayer == null) return;

                string modDir = e.ModDirectory?.ToLower() ?? "";
                plugin?.PluginLog?.Information($"[Drag And Drop Debug] ModSettingChanged: Type={e.ChangeType}, ModDir='{e.ModDirectory}', CollectionId={e.CollectionId}, Inherited={e.Inherited}");

                // Skip our own generated mods
                if (modDir.Contains("drag and drop") || modDir.Contains("do_not_edit") || modDir.Contains("texture body") || modDir.Contains("texture face") || modDir.Contains("texture eyes") || modDir.Contains("texture eyebrows") || modDir.Contains("texture mod")) return;

                // Check if this change affects the player's current collection
                Guid playerCollection = Guid.Empty;
                try
                {
                    playerCollection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex).Item3.Id;
                }
                catch { }

                // Inheritance changes affect the effective mod list even if the mod name doesn't look skin-related.
                // When a collection's inheritance is changed, the body mod may appear/disappear from the effective set.
                bool isInheritanceChange = e.ChangeType == Penumbra.Api.Enums.ModSettingChange.Inheritance ||
                                           e.ChangeType == Penumbra.Api.Enums.ModSettingChange.MultiInheritance;

                bool isSkinMod = modDir.Contains("bibo") || modDir.Contains("gen3") || modDir.Contains("tbse") ||
                                 modDir.Contains("body") || modDir.Contains("skin") || modDir.Contains("yab") ||
                                 modDir.Contains("eve ") || modDir.Contains("tight");

                if (isSkinMod || isInheritanceChange)
                {
                    string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                    if (isInheritanceChange)
                        plugin.PluginLog.Information($"[Drag And Drop Texturing] Inheritance change detected (ModDir: '{e.ModDirectory}', Collection: {e.CollectionId}). Rebuilding body textures...");
                    else
                        plugin.PluginLog.Information("[Drag And Drop Texturing] Skin mod change detected (" + e.ModDirectory + "). Rebuilding body textures...");
                    ScheduleRegeneration(charName, new[] { "_body" });
                }
            }
            catch (Exception ex)
            {
                plugin?.PluginLog?.Warning(ex, "Error in mod setting change handler");
            }
        }

        public void ScheduleRegeneration(string charName, string[] categorySuffixes, bool skipDelays = false, bool hideProgressUI = true)
        {
            lock (_regenerationLock)
            {
                foreach (var suffix in categorySuffixes)
                {
                    string key = charName + suffix;
                    if (_textureHistory.ContainsKey(key))
                    {
                        _pendingRegenerationCategories.Add(key);
                    }
                }

                _regenerationDebounce?.Dispose();
                _regenerationDebounce = new System.Threading.Timer(_ =>
                {
                    HashSet<string> categories;
                    lock (_regenerationLock)
                    {
                        categories = new HashSet<string>(_pendingRegenerationCategories);
                        _pendingRegenerationCategories.Clear();
                    }

                    // Give Penumbra time to process the character model change
                    // before we try to extract textures for the new race
                    // (skip for contextual layers — race hasn't changed)
                    if (!skipDelays) Thread.Sleep(2000);

                    foreach (var key in categories)
                    {
                        // Wait for previous rebuild to finish before starting the next
                        int waitAttempts = 0;
                        while (_lockDuplicateGeneration && waitAttempts < 60)
                        {
                            Thread.Sleep(1000);
                            waitAttempts++;
                        }
                        RebuildCategory(key, hideProgressUI);
                        if (!skipDelays) Thread.Sleep(500);
                    }
                }, null, skipDelays ? 200 : 2000, System.Threading.Timeout.Infinite);
            }
        }

        public Task InjectFilesAndRebuild(List<string> extractedFiles, KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter> selectedPlayer, BodyDragPart bodyDragPart)
        {
            _currentTarget = selectedPlayer.Value;
            return Task.Run(() =>
            {
                HashSet<string> droppedCategories = new HashSet<string>();
                for (int i = 0; i < extractedFiles.Count; i++)
                {
                    string file = extractedFiles[i];
                    string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                    string categoryKey = selectedPlayer.Key + "_";
                    bool isBody = false;

                    if (bodyDragPart == BodyDragPart.Clothing)
                    {
                        string gearSlot = GetGearSlotFromBone(_closestBone) ?? "body";
                        categoryKey += "gear_" + gearSlot;
                    }
                    else if (fileName.Contains("_gear_"))
                    {
                        string slot = "body";
                        foreach (var s in new[] { "head", "body", "hands", "legs", "feet", "ears", "neck", "wrists", "ring_l", "ring_r" })
                        {
                            if (fileName.Contains("_gear_" + s))
                            {
                                slot = s;
                                break;
                            }
                        }
                        categoryKey += "gear_" + slot;
                    }
                    else if (fileName.Contains("eyebrow") || fileName.Contains("lash")) categoryKey += "eyebrows";
                    else if (fileName.Contains("eye")) categoryKey += "eyes";
                    else if (fileName.Contains("face") || fileName.Contains("makeup")) categoryKey += "face";
                    else if (fileName.Contains("mata") || fileName.Contains("amat") || fileName.Contains("materiala") || fileName.Contains("gen2") ||
                        fileName.Contains("bibo") || fileName.Contains("b+") ||
                        fileName.Contains("gen3") || fileName.Contains("tbse")) { categoryKey += "body"; isBody = true; }
                    else
                    {
                        switch (bodyDragPart)
                        {
                            case BodyDragPart.Body: categoryKey += "body"; isBody = true; break;
                            case BodyDragPart.Face: categoryKey += "face"; break;
                            case BodyDragPart.Eyes: categoryKey += "eyes"; break;
                            case BodyDragPart.EyebrowsAndLashes: categoryKey += "eyebrows"; break;
                            default: categoryKey += "fallback_" + bodyDragPart.ToString(); break;
                        }
                    }

                    bool isFace = categoryKey.EndsWith("face");
                    bool needsClassification = false;

                    if (isBody && !fileName.Contains("bibo") && !fileName.Contains("b+") && !fileName.Contains("gen3") && !fileName.Contains("tbse") && !fileName.Contains("gen2") && !fileName.Contains("mata") && !fileName.Contains("amat"))
                        needsClassification = true;

                    if ((isBody || isFace) && !fileName.Contains("norm") && !fileName.EndsWith("_n") && !fileName.Contains("_n_") && !fileName.Contains("mask") && !fileName.EndsWith("_m") && !fileName.Contains("_m_") && !fileName.Contains("base") && !fileName.Contains("diffuse") && !fileName.EndsWith("_d") && !fileName.Contains("_d_") && !fileName.Contains("glow") && !fileName.EndsWith("_g") && !fileName.Contains("_g_"))
                        needsClassification = true;

                    if (needsClassification)
                    {
                        _classificationTcs = new TaskCompletionSource<string>();
                        _fileToClassify = file;
                        _classificationIsBody = isBody;
                        _showClassificationPopup = true;
                        string format = _classificationTcs.Task.Result;
                        if (!string.IsNullOrEmpty(format))
                        {
                            string[] parts = format.Split('|');
                            string prefix = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) ? parts[0] + "_" : "";
                            string suffix = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? "_" + parts[1] : "";

                            string newPath = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, prefix + Path.GetFileNameWithoutExtension(file) + suffix + Path.GetExtension(file));
                            System.IO.File.Copy(file, newPath, true);
                            file = newPath;
                        }
                        else
                        {
                            continue; // They cancelled the popup
                        }
                    }

                    if (!_textureHistory.ContainsKey(categoryKey))
                    {
                        _textureHistory[categoryKey] = new List<string>();
                    }
                    if (!_textureHistoryTints.ContainsKey(categoryKey))
                    {
                        _textureHistoryTints[categoryKey] = new List<System.Numerics.Vector4>();
                        for (int h = 0; h < _textureHistory[categoryKey].Count; h++)
                        {
                            _textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
                        }
                    }

                    if (!plugin.Configuration.EnableTextureStacking && !droppedCategories.Contains(categoryKey))
                    {
                        _textureHistory[categoryKey].Clear();
                        _textureHistoryTints[categoryKey].Clear();
                    }

                    if (!_textureHistory[categoryKey].Contains(file))
                    {
                        _textureHistory[categoryKey].Add(file);
                        _textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
                    }

                    if (plugin.Configuration.RecentLayers.Contains(file))
                        plugin.Configuration.RecentLayers.Remove(file);
                    plugin.Configuration.RecentLayers.Insert(0, file);
                    while (plugin.Configuration.RecentLayers.Count > 50)
                        plugin.Configuration.RecentLayers.RemoveAt(plugin.Configuration.RecentLayers.Count - 1);

                    droppedCategories.Add(categoryKey);
                }
                plugin.Configuration.Save();
                UpdateWatchers();

                foreach (var cat in droppedCategories)
                {
                    int waitAttempts = 0;
                    while (_lockDuplicateGeneration && waitAttempts < 60)
                    {
                        Thread.Sleep(1000);
                        waitAttempts++;
                    }
                    RebuildCategory(cat, false);
                    Thread.Sleep(500);
                }
            });
        }

        private static readonly string[] ValidTextureExtensions = new[]
        {
          ".png",
          ".dds",
          ".bmp",
          ".tex",
          ".psd",
          ".clmp"
        };
        public void RebuildAllCategories()
        {
            if (_textureHistory == null || _textureHistory.Count == 0) return;
            var keys = _textureHistory.Keys.ToList();

            Task.Run(() =>
            {
                foreach (var key in keys)
                {
                    int waitAttempts = 0;
                    while (_lockDuplicateGeneration && waitAttempts < 60)
                    {
                        Thread.Sleep(1000);
                        waitAttempts++;
                    }
                    RebuildCategory(key, false);
                    Thread.Sleep(500);
                }
            });
        }
        public void RebuildCategory(string categoryKey, bool hideProgressUI = true)
        {
            if (!_textureHistory.ContainsKey(categoryKey)) return;

            string charName = categoryKey.Split('_')[0];
            ICharacter character = null;
            if (plugin.SafeGameObjectManager.LocalPlayer != null && plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue == charName)
            {
                character = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
            }
            else
            {
                foreach (var item in Plugin.GetNearestObjects())
                {
                    ICharacter c = item as ICharacter;
                    if (c != null && c.Name.TextValue == charName)
                    {
                        character = c;
                        break;
                    }
                }
            }

            if (character == null)
            {
                plugin.PluginLog.Error("[Drag And Drop Texturing] Character " + charName + " not found nearby. Cannot re-export.");
                return;
            }

            var localCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
            string raceCode = PenumbraAndGlamourerHelperFunctions.ModelRaceToRaceCode(localCustomization.Customize.Race.Value - 1, localCustomization.Customize.Clan.Value - 1, localCustomization.Customize.Gender.Value);
            string subRaceName = PenumbraAndGlamourerHelperFunctions.SubRaceToSubRaceName(localCustomization.Customize.Race.Value - 1, localCustomization.Customize.Clan.Value - 1);
            bool holdingModifier = Plugin.Configuration.AutoUniversalConvert;

            Task.Run(async () =>
            {
                int waitAttempts = 0;
                while (_lockDuplicateGeneration && waitAttempts < 60)
                {
                    Thread.Sleep(1000);
                    waitAttempts++;
                }
                if (_lockDuplicateGeneration) return;

                try
                {
                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                    List<TextureSet> textureSets = new List<TextureSet>();
                    string localModName = charName + " Texture Mod";

                    TextureSet item = null;
                    string categoryModName = "";
                    string overrideType = "";

                    string lastFile = _textureHistory[categoryKey].FirstOrDefault();
                    if (string.IsNullOrEmpty(lastFile))
                    {
                        lastFile = "empty.png";
                    }

                    if (categoryKey.EndsWith("_body"))
                    {
                        if (localCustomization.Customize.Race.Value - 1 == 2)
                        {
                            item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, 5,
                            RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                            localCustomization.Customize.TailShape.Value - 1, false);
                        }
                        else if (localCustomization.Customize.Gender.Value == 0)
                        {
                            item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, 3,
                            RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                            localCustomization.Customize.TailShape.Value - 1, false);
                        }
                        else
                        {
                            item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, DetectBaseBodyType(lastFile, collection, localCustomization.Customize.Gender.Value),
                            RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                            localCustomization.Customize.TailShape.Value - 1, false);
                        }
                        categoryModName = "Body";
                        if (item != null) item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                    }
                    else if (categoryKey.EndsWith("_eyebrows"))
                    {
                        item = ProjectHelper.CreateFaceTextureSet(localCustomization.Customize.Face.Value - 1, 1, 0,
                        localCustomization.Customize.Gender.Value,
                        RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                        localCustomization.Customize.Clan.Value - 1, 0, false);
                        categoryModName = "Eyebrows";
                        overrideType = "Normal";
                    }
                    else if (categoryKey.EndsWith("_eyes"))
                    {
                        item = ProjectHelper.CreateFaceTextureSet(localCustomization.Customize.Face.Value - 1, 2, 0,
                        localCustomization.Customize.Gender.Value,
                        RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                        localCustomization.Customize.Clan.Value - 1, 0, false);
                        categoryModName = "Eyes";
                        overrideType = "Base";
                    }
                    else if (categoryKey.EndsWith("_face"))
                    {
                        item = ProjectHelper.CreateFaceTextureSet(localCustomization.Customize.Face.Value - 1, 0, 0,
                        localCustomization.Customize.Gender.Value,
                        RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                        localCustomization.Customize.Clan.Value - 1, 0, false);
                        categoryModName = "Face";
                    }
                    else if (categoryKey.Contains("fallback_Body"))
                    {
                        if (localCustomization.Customize.Race.Value - 1 == 2)
                        {
                            item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, 5,
                            RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                            localCustomization.Customize.TailShape.Value - 1, false);
                        }
                        else if (localCustomization.Customize.Gender.Value == 0)
                        {
                            item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, 3,
                            RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                            localCustomization.Customize.TailShape.Value - 1, false);
                        }
                        else
                        {
                            item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, DetectBaseBodyType(lastFile, collection, localCustomization.Customize.Gender.Value),
                            RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                            localCustomization.Customize.TailShape.Value - 1, false);
                        }
                        categoryModName = "Body";
                        if (item != null) item.OmniExportMode = File.Exists(_xNormalPath) && Path.Exists(_textureProcessor.BasePath) && holdingModifier;
                    }
                    else if (categoryKey.Contains("fallback_Face"))
                    {
                        item = ProjectHelper.CreateFaceTextureSet(localCustomization.Customize.Face.Value - 1, 0, 0,
                        localCustomization.Customize.Gender.Value,
                        RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                        localCustomization.Customize.Clan.Value - 1, 0, false);
                        categoryModName = "Face";
                    }
                    else if (categoryKey.Contains("fallback_Eyes"))
                    {
                        item = ProjectHelper.CreateFaceTextureSet(localCustomization.Customize.Face.Value - 1, 2, 0,
                        localCustomization.Customize.Gender.Value,
                        RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                        localCustomization.Customize.Clan.Value - 1, 0, false);
                        categoryModName = "Eyes";
                        overrideType = "Normal";
                    }
                    else if (categoryKey.Contains("fallback_EyebrowsAndLashes"))
                    {
                        item = ProjectHelper.CreateFaceTextureSet(localCustomization.Customize.Face.Value - 1, 1, 0,
                        localCustomization.Customize.Gender.Value,
                        RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1),
                        localCustomization.Customize.Clan.Value - 1, 0, false);
                        categoryModName = "Eyebrows";
                        overrideType = "Normal";
                    }
                    else if (categoryKey.Contains("_gear_"))
                    {
                        if (!_gearCategoryMeta.TryGetValue(categoryKey, out var gearMeta))
                        {
                            string suffixPart = categoryKey.Split("_gear_")[1];
                            string slot = suffixPart;
                            string matName = "";
                            string modName = "";

                            int bracketIdx = suffixPart.IndexOf("_[");
                            if (bracketIdx > 0)
                            {
                                modName = suffixPart.Substring(bracketIdx + 2).TrimEnd(']');
                                suffixPart = suffixPart.Substring(0, bracketIdx);
                            }

                            int underscoreIdx = suffixPart.IndexOf('_');
                            if (underscoreIdx > 0)
                            {
                                slot = suffixPart.Substring(0, underscoreIdx);
                                matName = suffixPart.Substring(underscoreIdx + 1);
                            }

                            var wornPieces = DragAndDropTexturing.Equipment.WornEquipmentResolver.ResolveWornGear(character, plugin);
                            gearMeta = wornPieces.Find(p => p.SlotKey.Equals(slot, StringComparison.OrdinalIgnoreCase) &&
                                (string.IsNullOrEmpty(matName) || p.MaterialName.Equals(matName, StringComparison.OrdinalIgnoreCase)) &&
                                (string.IsNullOrEmpty(modName) || p.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase)));
                            if (gearMeta != null)
                            {
                                _gearCategoryMeta[categoryKey] = gearMeta;
                            }
                        }

                        if (gearMeta != null)
                        {
                            item = ProjectHelper.CreateEquipmentTextureSet(
                                gearMeta.DisplayName,
                                gearMeta.InternalBasePath,
                                gearMeta.InternalNormalPath,
                                gearMeta.InternalMaskPath,
                                gearMeta.InternalMaterialPath);
                            categoryModName = "Gear " + gearMeta.SlotKey + (string.IsNullOrEmpty(gearMeta.MaterialName) ? "" : " " + gearMeta.MaterialName) + (string.IsNullOrEmpty(gearMeta.ModName) ? "" : " [" + gearMeta.ModName + "]");
                        }
                    }

                    if (item != null)
                    {
                        ApplyDefaultSkinType(item);
                        for (int _i = 0; _i < _textureHistory[categoryKey].Count; _i++)
                        {
                            string f = _textureHistory[categoryKey][_i];
                            System.Numerics.Vector4? t = _textureHistoryTints.ContainsKey(categoryKey) && _i < _textureHistoryTints[categoryKey].Count ? _textureHistoryTints[categoryKey][_i] : null;
                            AddToTextureSet(item, f, overrideType, t);
                        }

                        bool hasContextualLayers = false;
                        if (plugin.ContextualLayerManager != null && charName == plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue)
                        {
                            foreach (var activeLayer in plugin.ContextualLayerManager.GetActiveLayers())
                            {
                                if (categoryKey.EndsWith("_" + activeLayer.LayerDef.TargetBodyPart.ToLower()))
                                {
                                    for (int layerIdx = 0; layerIdx < activeLayer.CurrentStackCount; layerIdx++)
                                    {
                                        if (layerIdx < activeLayer.CachedTexturePaths.Count && File.Exists(activeLayer.CachedTexturePaths[layerIdx]))
                                        {
                                            hasContextualLayers = true;
                                            AddToTextureSet(item, activeLayer.CachedTexturePaths[layerIdx], overrideType);
                                        }
                                    }
                                }
                            }
                        }
                        if (_textureHistory[categoryKey].Count == 0 && !hasContextualLayers)
                        {
                            if (categoryKey.Contains("_gear_"))
                            {
                                localModName = localModName.Replace("Mod", categoryModName);
                                string deleteModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), localModName);

                                try
                                {
                                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, localModName, false, localModName);
                                }
                                catch { }

                                if (Directory.Exists(deleteModPath))
                                {
                                    try
                                    {
                                        Directory.Delete(deleteModPath, true);
                                    }
                                    catch { }
                                }

                                try
                                {
                                    PenumbraAndGlamourerIpcWrapper.Instance.DeleteMod.Invoke(localModName, localModName);
                                }
                                catch
                                {
                                    try
                                    {
                                        PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(localModName, "");
                                    }
                                    catch { }
                                }

                                plugin.PluginLog.Info($"[Drag And Drop Texturing] Deleted/disabled empty clothing mod: {localModName}");
                                PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                                return;
                            }
                            else
                            {
                                string emptyPath = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory?.FullName!, "empty_base.png");
                                if (!File.Exists(emptyPath))
                                {
                                    using (var emptyBmp = new System.Drawing.Bitmap(1024, 1024))
                                    {
                                        emptyBmp.Save(emptyPath, System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                }
                                AddToTextureSet(item, emptyPath, overrideType);
                            }
                        }

                        textureSets.Add(item);
                        localModName = localModName.Replace("Mod", categoryModName);

                        string fullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), localModName);
                        await Export(true, textureSets, fullModPath, localModName, new KeyValuePair<string, ICharacter>(character.Name.TextValue, character),
                            localCustomization.Customize.Race.Value - 1, localCustomization.Customize.Clan.Value - 1, localCustomization.Customize.Gender.Value, localCustomization.Customize.Face.Value - 1, hideProgressUI);
                    }
                }
                catch (Exception e)
                {
                    plugin.PluginLog.Error($"[Drag And Drop Texturing] Crash during generation: {e.Message}");
                    Plugin.PluginLog.Warning(e, e.Message);
                }
            });
        }

        public void RefreshWornGearCache()
        {
            var localPlayer = Plugin.ObjectTable.LocalPlayer as ICharacter;
            if (localPlayer == null)
            {
                CachedWornGear = new List<WornEquipmentPiece>();
                return;
            }

            CachedWornGear = WornEquipmentResolver.ResolveWornGear(localPlayer, plugin);
        }

        public void ImportWornGearSlot(WornEquipmentPiece piece)
        {
            if (piece == null || plugin == null) return;

            var localPlayer = Plugin.ObjectTable.LocalPlayer as ICharacter;
            if (localPlayer == null)
            {
                plugin.Chat.PrintError("[Drag And Drop Texturing] No local player — cannot import worn gear.");
                return;
            }

            Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
            string exportDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "WornGear");
            string source = !string.IsNullOrEmpty(piece.ResolvedBaseDiskPath) ? piece.ResolvedBaseDiskPath : piece.InternalBasePath;
            string pngPath = WornEquipmentResolver.ExportResolvedTextureToPng(source, collection, exportDir, plugin, piece.SlotKey, piece.MaterialName);

            if (string.IsNullOrEmpty(pngPath))
            {
                plugin.Chat.PrintError($"[Drag And Drop Texturing] Could not read texture for {piece.DisplayName}.");
                return;
            }

            string categoryKey = $"{localPlayer.Name.TextValue}_gear_{piece.SlotKey}" +
                (string.IsNullOrEmpty(piece.MaterialName) ? "" : "_" + piece.MaterialName) +
                (string.IsNullOrEmpty(piece.ModName) ? "" : "_[" + piece.ModName + "]");
            _gearCategoryMeta[categoryKey] = piece;

            if (!_textureHistory.ContainsKey(categoryKey))
            {
                _textureHistory[categoryKey] = new List<string>();
                _textureHistoryTints[categoryKey] = new List<System.Numerics.Vector4>();
            }

            if (!plugin.Configuration.EnableTextureStacking)
            {
                _textureHistory[categoryKey].Clear();
                _textureHistoryTints[categoryKey].Clear();
            }

            if (!_textureHistory[categoryKey].Contains(pngPath))
            {
                _textureHistory[categoryKey].Add(pngPath);
                _textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
            }

            plugin.Configuration.Save();
            UpdateWatchers();
            RebuildCategory(categoryKey, false);
            plugin.Chat.Print($"[Drag And Drop Texturing] Imported worn gear layer: {piece.DisplayName}");
        }

        private void ApplyDefaultSkinType(TextureSet item)
        {
            var availableSkins = UniversalTextureSetCreator.GetSkinTypeNames(item);
            if (availableSkins != null)
            {
                int index = availableSkins.IndexOf(plugin.Configuration.DefaultUnderlaySkinType);
                if (index != -1)
                {
                    item.SkinType = index;
                }
                else
                {
                    item.SkinType = 0;
                }
            }
        }

        /// <summary>
        /// Extracts a vanilla .tex file from FFXIV game data via Lumina, converts to PNG, 
        /// and upscales to match the input texture resolution for use as an underlay.
        /// </summary>
        private string ExtractVanillaTexViaLumina(string internalGamePath, TextureSet textureSet)
        {
            try
            {
                var texFile = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(internalGamePath);
                if (texFile == null) return "";

                // Use the existing TexIO pattern to convert the raw .tex data to Bitmap
                using (var stream = new MemoryStream(texFile.Data))
                {
                    var bitmap = TexIO.TexToBitmap(stream);
                    int texWidth = bitmap.Width;
                    int texHeight = bitmap.Height;

                    // Determine target resolution from the input texture
                    int targetWidth = texWidth;
                    int targetHeight = texHeight;
                    string inputFile = !string.IsNullOrEmpty(textureSet.Base) ? textureSet.Base :
                        (textureSet.BaseOverlays.Count > 0 ? textureSet.BaseOverlays[0] : "");
                    if (!string.IsNullOrEmpty(inputFile) && File.Exists(inputFile))
                    {
                        try
                        {
                            using (var inputImage = System.Drawing.Image.FromFile(inputFile))
                            {
                                targetWidth = inputImage.Width;
                                targetHeight = inputImage.Height;
                            }
                        }
                        catch { /* keep vanilla resolution */ }
                    }

                    // Upscale if needed
                    Bitmap finalBitmap = bitmap;
                    if (targetWidth != texWidth || targetHeight != texHeight)
                    {
                        finalBitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(finalBitmap))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(bitmap, 0, 0, targetWidth, targetHeight);
                        }
                    }

                    // Save as temp PNG
                    string tempDir = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", "vanilla_cache");
                    Directory.CreateDirectory(tempDir);
                    string safeName = internalGamePath.Replace("/", "_").Replace("\\", "_");
                    string tempPath = Path.Combine(tempDir, safeName + $"_{targetWidth}x{targetHeight}.png");
                    TexIO.SaveBitmap(finalBitmap, tempPath);

                    if (finalBitmap != bitmap) finalBitmap.Dispose();
                    bitmap.Dispose();

                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                plugin?.PluginLog?.Warning(ex, $"Failed to extract vanilla tex: {internalGamePath}");
                return "";
            }
        }

        private int DetectBaseBodyType(string file, Guid collection, int gender)
        {
            int penumbraBase = PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collection, gender, out string detectedModName, plugin);
            if (penumbraBase != -1)
            {
                string bodyName = penumbraBase == 1 ? "Bibo" : penumbraBase == 2 ? "Gen3" : penumbraBase == 3 ? "TBSE" : "Unknown";
                plugin.PluginLog.Information($"[Drag And Drop Texturing] Baseline Body Detected: {bodyName} (from Mod: '{detectedModName}')");
                plugin.PluginLog.Information($"[Drag And Drop Texturing] Baseline Body Detected: {bodyName} (from Mod: '{detectedModName}')");
                return penumbraBase;
            }
            else
            {
                plugin.PluginLog.Information($"[Drag And Drop Texturing] Penumbra detection found no body mod. Falling back to source texture detection.");
                plugin.PluginLog.Information($"[Drag And Drop Texturing] Penumbra detection found no body mod. Falling back to source texture detection.");
            }

            string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
            if (gender != 0)
            {
                if (fileName.Contains("bibo") || fileName.Contains("b+")) return 1;
                if (fileName.Contains("gen3") || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"(^|[^a-z])eve([^a-z]|$)") || fileName.Contains("exqb") || fileName.Contains("pythia") || fileName.Contains("gaia")) return 2;
            }
            if (gender != 1)
            {
                if (fileName.Contains("tbse")) return 3;
            }
            if (fileName.Contains("gen2") || fileName.Contains("body") || fileName.Contains("mata")) return 0;

            switch (ImageManipulation.FemaleBodyUVClassifier(file))
            {
                case BodyUVType.Bibo: return 1;
                case BodyUVType.Gen3: return 2;
                case BodyUVType.Gen2: return 0;
            }

            return 2; // Default to Gen3
        }

        private string GetGearSlotFromBone(Bone bone)
        {
            if (bone == null || bone.HkaBone.Name.String == null) return "body";
            string name = bone.HkaBone.Name.String.ToLower();

            // Head slot: head, face, eyes, hair, helmet, ears, neck, teeth etc.
            if (name.Contains("head") || name.Contains("kao") || name.Contains("mimi") || name.Contains("hair") || name.Contains("ear"))
                return "head";

            // Hands slot: hand, arm, wrist, finger
            if (name.Contains("shou") || name.Contains("te") || name.Contains("ude") || name.Contains("arm") || name.Contains("wrist") || name.Contains("finger") || name.Contains("hand"))
                return "hands";

            // Feet slot: foot, leg, calf, ankle, toe, shoe
            if (name.Contains("asi") || name.Contains("foot") || name.Contains("toe") || name.Contains("calf") || name.Contains("shin") || name.Contains("ankle") || name.Contains("shoe"))
            {
                if (name.Contains("asi_a") || name.Contains("thigh"))
                    return "legs";
                return "feet";
            }

            // Legs slot: leg, thigh, knee, pelvis, skirt, crotch, waist
            if (name.Contains("leg") || name.Contains("knee") || name.Contains("momo") || name.Contains("waist") || name.Contains("hara") || name.Contains("hip") || name.Contains("kosi") || name.Contains("pelvis"))
                return "legs";

            // Body slot: chest, spine, neck, shoulder, back, breast
            if (name.Contains("spine") || name.Contains("chest") || name.Contains("mune") || name.Contains("kubi") || name.Contains("neck") || name.Contains("kata") || name.Contains("shoulder") || name.Contains("back") || name.Contains("breast") || name.Contains("torso"))
                return "body";

            return "body"; // Default fallback
        }

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
        EyebrowsAndLashes,
        Clothing,
    }
}
