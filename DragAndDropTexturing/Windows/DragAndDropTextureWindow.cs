using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.DragDrop;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DragAndDropTexturing;
using DragAndDropTexturing.Equipment;
using DragAndDropTexturing.LanguageHelpers;
using FFXIVLooseTextureCompiler;
using FFXIVLooseTextureCompiler.Export;
using FFXIVLooseTextureCompiler.ImageProcessing;
using FFXIVLooseTextureCompiler.PathOrganization;
using FFXIVLooseTextureCompiler.Racial;
using Ktisis.Structs;
using Ktisis.Structs.Actor;
using LooseTextureCompilerCore.ProjectCreation;
using PenumbraAndGlamourerHelpers;
using PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using static FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation;
using Bone = Ktisis.Structs.Bones.Bone;
using ICharacter = Dalamud.Game.ClientState.Objects.Types.ICharacter;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
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
        private bool _isRegenerationPending;
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
        private Dictionary<string, Dictionary<string, List<string>>> _textureCollectionHistory;
        private Dictionary<string, Dictionary<string, List<Vector4>>> _textureCollectionHistoryTints;
        private Dictionary<string, Dictionary<string, Vector4>> _collectionSortedPenumbraOverlayTints;
        private Dictionary<string, Dictionary<string, Vector4>> _collectionSortedPenumbraOverlayGlowTints;
        private readonly Dictionary<string, WornEquipmentPiece> _gearCategoryMeta = new();
        public Dictionary<string, WornEquipmentPiece> GearCategoryMeta { get => _gearCategoryMeta; }
        public List<WornEquipmentPiece> CachedWornGear { get; private set; } = new();

        // Auto-regeneration tracking
        private System.Threading.Timer _regenerationDebounce;
        private HashSet<string> _pendingRegenerationCategories = new HashSet<string>();
        private readonly object _regenerationLock = new object();
        private volatile bool _bulkRebuildInProgress;

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
                if (string.IsNullOrEmpty(item.Base)) { item.Base = file; item.BaseUV = sourceUV; item.BaseTint = tint ?? System.Numerics.Vector4.One; }
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
                if (string.IsNullOrEmpty(item.Glow)) { item.Glow = file; item.GlowUV = sourceUV; item.GlowTint = tint ?? System.Numerics.Vector4.One; }
                else if (!item.GlowOverlays.Contains(file)) { item.GlowOverlays.Add(file); item.GlowOverlayUVs.Add(sourceUV); item.GlowOverlayTints.Add(tint ?? System.Numerics.Vector4.One); }
            }
        }

        private bool ApplyAdvancedOverlays(TextureSet item, string categoryKey, string collectionId)
        {
            bool applied = false;
            var overlaysList = new List<DragAndDropTexturing.Overlays.ResolvedAdvancedOverlay>(DragAndDropTexturing.Overlays.AdvancedOverlayParser.ActiveOverlays[collectionId]);
            overlaysList.Reverse();
            foreach (var activeOverlay in overlaysList)
            {
                if (categoryKey.EndsWith("_" + activeOverlay.TargetBodyPart.ToLower()))
                {
                    applied = true;
                    string diffusePath = activeOverlay.DiffusePath;
                    string normalPath = activeOverlay.NormalPath;
                    string maskPath = activeOverlay.MaskPath;

                    if (!string.IsNullOrEmpty(diffusePath))
                    {
                        if (!string.IsNullOrEmpty(normalPath))
                        {
                            string memoryPath = "memory:\\" + normalPath.GetHashCode() + "_" + diffusePath.GetHashCode() + "_masked";
                            if (!FFXIVLooseTextureCompiler.ImageProcessing.TexIO.VirtualFileSystem.ContainsKey(memoryPath))
                            {
                                var dims = FFXIVLooseTextureCompiler.ImageProcessing.ComputeSharpLayering.GetImageDimensions(normalPath);
                                if (dims.Width > 0 && dims.Height > 0)
                                {
                                    using (System.Drawing.Bitmap merged = FFXIVLooseTextureCompiler.ImageProcessing.ComputeSharpLayering.MergeAlphaChannelToRGBGpuFromPaths(normalPath, diffusePath, dims.Width, dims.Height, false))
                                    {
                                        FFXIVLooseTextureCompiler.ImageProcessing.TexIO.SaveMemoryBitmap(merged, memoryPath);
                                    }
                                }
                            }
                            normalPath = memoryPath;
                        }

                        if (!string.IsNullOrEmpty(maskPath))
                        {
                            string memoryPath = "memory:\\" + maskPath.GetHashCode() + "_" + diffusePath.GetHashCode() + "_masked_grayscale";
                            if (!FFXIVLooseTextureCompiler.ImageProcessing.TexIO.VirtualFileSystem.ContainsKey(memoryPath))
                            {
                                var dims = FFXIVLooseTextureCompiler.ImageProcessing.ComputeSharpLayering.GetImageDimensions(maskPath);
                                if (dims.Width > 0 && dims.Height > 0)
                                {
                                    using (System.Drawing.Bitmap merged = FFXIVLooseTextureCompiler.ImageProcessing.ComputeSharpLayering.MergeAlphaChannelToRGBGpuFromPaths(maskPath, diffusePath, dims.Width, dims.Height, false))
                                    {
                                        using (System.Drawing.Bitmap grayscale = FFXIVLooseTextureCompiler.ImageProcessing.Grayscale.MakeGrayscale(merged))
                                        {
                                            FFXIVLooseTextureCompiler.ImageProcessing.TexIO.SaveMemoryBitmap(grayscale, memoryPath);
                                        }
                                    }
                                }
                            }
                            maskPath = memoryPath;
                        }
                    }

                    string overlayKey = !string.IsNullOrEmpty(diffusePath) ? diffusePath : (!string.IsNullOrEmpty(normalPath) ? normalPath : maskPath);
                    System.Numerics.Vector4 tintColor = System.Numerics.Vector4.One;

                    // This variable does nothing. Sanity check to ensure github is using up to date code.
                    int sanityCheck = 0;

                    if (!_collectionSortedPenumbraOverlayTints.ContainsKey(collectionId))
                    {
                        _collectionSortedPenumbraOverlayTints.Add(collectionId, new Dictionary<string, Vector4>());
                    }
                    if (!_collectionSortedPenumbraOverlayGlowTints.ContainsKey(collectionId))
                    {
                        _collectionSortedPenumbraOverlayGlowTints.Add(collectionId, new Dictionary<string, Vector4>());
                    }
                    if (!_collectionSortedPenumbraOverlayTints[collectionId].ContainsKey(overlayKey))
                    {
                        _collectionSortedPenumbraOverlayTints[collectionId].Add(overlayKey, new Vector4());
                    }
                    if (!_collectionSortedPenumbraOverlayGlowTints[collectionId].ContainsKey(overlayKey))
                    {
                        _collectionSortedPenumbraOverlayGlowTints[collectionId].Add(overlayKey, new Vector4());
                    }
                    try
                    {
                        if (overlayKey != null && _collectionSortedPenumbraOverlayTints[collectionId].TryGetValue(overlayKey, out var savedTint))
                        {
                            tintColor = savedTint;
                        }

                        System.Numerics.Vector4 glowTintColor = new System.Numerics.Vector4(0, 0, 0, 1f);
                        if (overlayKey != null && _collectionSortedPenumbraOverlayGlowTints[collectionId].TryGetValue(overlayKey, out var savedGlowTint))
                        {
                            glowTintColor = savedGlowTint;
                        }

                        if (!string.IsNullOrEmpty(diffusePath))
                        {
                            if (string.IsNullOrEmpty(item.Base)) { item.Base = diffusePath; item.BaseUV = activeOverlay.UVType; item.BaseTint = tintColor; }
                            else if (!item.BaseOverlays.Contains(diffusePath)) { item.BaseOverlays.Add(diffusePath); item.BaseOverlayUVs.Add(activeOverlay.UVType); item.BaseOverlayTints.Add(tintColor); }
                        }
                        if (!string.IsNullOrEmpty(normalPath))
                        {
                            if (string.IsNullOrEmpty(item.Normal)) { item.Normal = normalPath; item.NormalUV = activeOverlay.UVType; }
                            else if (!item.NormalOverlays.Contains(normalPath)) { item.NormalOverlays.Add(normalPath); item.NormalOverlayUVs.Add(activeOverlay.UVType); }
                        }
                        if (!string.IsNullOrEmpty(maskPath))
                        {
                            if (string.IsNullOrEmpty(item.Glow)) { item.Glow = maskPath; item.GlowUV = activeOverlay.UVType; item.GlowTint = glowTintColor; }
                            else if (!item.GlowOverlays.Contains(maskPath)) { item.GlowOverlays.Add(maskPath); item.GlowOverlayUVs.Add(activeOverlay.UVType); item.GlowOverlayTints.Add(glowTintColor); }
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.PluginLog.Warning(e.Message, e.StackTrace);
                    }
                }
            }
            return applied;
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
                foreach (var textureHistoryCollection in _textureCollectionHistory)
                {
                    foreach (var textureHistoryItem in textureHistoryCollection.Value)
                    {
                        string category = textureHistoryItem.Key;
                        foreach (var file in textureHistoryItem.Value)
                        {
                            if (File.Exists(file))
                            {
                                string dir = Path.GetDirectoryName(file);
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
        }

        private void FileWatcher_Changed(object sender, System.IO.FileSystemEventArgs e)
        {
            if (_bulkRebuildInProgress) return;
            if ((DateTime.Now - _lastRebuildTime).TotalMilliseconds < 500) return; // Debounce

            bool triggered = false;
            HashSet<string> categoriesToRebuild = new HashSet<string>();

            lock (_watcherLock)
            {
                foreach (var textureCollectionHistory in _textureCollectionHistory)
                {
                    foreach (var textureHistoryItem in textureCollectionHistory.Value)
                    {
                        foreach (var file in textureHistoryItem.Value)
                        {
                            if (file.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase))
                            {
                                categoriesToRebuild.Add(textureHistoryItem.Key);
                                triggered = true;
                            }
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
                    _textureCollectionHistory = plugin.Configuration.CollectionSortedTextureHistory;
                    _textureCollectionHistoryTints = plugin.Configuration.CollectionSortedTextureHistoryTints;
                    _collectionSortedPenumbraOverlayTints = plugin.Configuration.CollectionSortedPenumbraOverlayTints;
                    _collectionSortedPenumbraOverlayGlowTints = plugin.Configuration.CollectionSortedPenumbraOverlayGlowTints;

                    foreach (var textureCollectionHistoryKey in _textureCollectionHistory.Keys)
                    {
                        foreach (var textureHistoryKey in _textureCollectionHistory[textureCollectionHistoryKey].Keys)
                        {
                            var textureHistoryTints = _textureCollectionHistoryTints[textureCollectionHistoryKey];
                            var textureHistory = _textureCollectionHistory[textureCollectionHistoryKey];
                            if (!textureHistoryTints.ContainsKey(textureHistoryKey))
                            {
                                textureHistoryTints[textureHistoryKey] = new List<Vector4>();
                                for (int i = 0; i < textureHistory[textureHistoryKey].Count; i++)
                                {
                                    textureHistoryTints[textureHistoryKey].Add(Vector4.One);
                                }
                            }
                        }
                    }

                    Task.Run(async () =>
                    {
                        await CheckAndDownloadDLC();

                        // Hook Glamourer state changes for auto-regeneration
                        PenumbraAndGlamourerIpcWrapper.Instance.OnGlamourerStateChanged += OnGlamourerStateChanged;
                        PenumbraAndGlamourerIpcWrapper.Instance.OnModSettingChanged += OnModSettingChanged;

                        // Trigger initial rebuild if player is already logged in with existing texture history
                        TryInitialRebuild();

                        while (Plugin.SafeGameObjectManager.LocalPlayer == null)
                        {
                            Thread.Sleep(100);
                        }
                        var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
                        var collectionId = collection.EffectiveCollection.Id.ToString();
                        if (plugin.Configuration.TextureHistory != null)
                        {
                            if (!_textureCollectionHistory.ContainsKey(collectionId))
                            {
                                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                            }
                            if (_textureCollectionHistoryTints[collectionId] == null)
                            {
                                _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
                            }
                            _textureCollectionHistory[collectionId] = plugin.Configuration.TextureHistory;
                            _textureCollectionHistoryTints[collectionId] = plugin.Configuration.TextureHistoryTints;
                            plugin.Configuration.TextureHistory = null;
                            plugin.Configuration.TextureHistoryTints = null;
                        }

                        if (plugin.Configuration.PenumbraOverlayTints != null)
                        {
                            if (_collectionSortedPenumbraOverlayTints.ContainsKey(collectionId))
                            {
                                _collectionSortedPenumbraOverlayTints[collectionId] = new Dictionary<string, Vector4>();
                            }
                            if (_collectionSortedPenumbraOverlayGlowTints.ContainsKey(collectionId))
                            {
                                _collectionSortedPenumbraOverlayGlowTints[collectionId] = new Dictionary<string, Vector4>();
                            }
                            _collectionSortedPenumbraOverlayTints[collectionId] = plugin.Configuration.PenumbraOverlayTints;
                            _collectionSortedPenumbraOverlayGlowTints[collectionId] = plugin.Configuration.PenumbraOverlayGlowTints;
                            plugin.Configuration.PenumbraOverlayTints = null;
                            plugin.Configuration.PenumbraOverlayGlowTints = null;
                        }
                    });
                }
            }
        }

        public Dictionary<string, Dictionary<string, List<string>>> TextureCollectionHistory { get => _textureCollectionHistory; set => _textureCollectionHistory = value; }
        public Dictionary<string, Dictionary<string, List<Vector4>>> TextureCollectionHistoryTints { get => _textureCollectionHistoryTints; set => _textureCollectionHistoryTints = value; }
        public Dictionary<string, Dictionary<string, Vector4>> CollectionSortedPenumbraOverlayTints { get => _collectionSortedPenumbraOverlayTints; set => _collectionSortedPenumbraOverlayTints = value; }
        public Dictionary<string, Dictionary<string, Vector4>> CollectionSortedPenumbraOverlayGlowTints { get => _collectionSortedPenumbraOverlayGlowTints; set => _collectionSortedPenumbraOverlayGlowTints = value; }

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
        private uint _lastJobId = 0;

        public override void Update()
        {
            if (plugin?.SafeGameObjectManager?.LocalPlayer != null)
            {
                uint currentJob = plugin.SafeGameObjectManager.LocalPlayer.ClassJob.RowId;
                if (_lastJobId != 0 && _lastJobId != currentJob)
                {
                    HandleJobChange(currentJob);
                }
                _lastJobId = currentJob;
            }

            if (Plugin.Configuration.AutoDistanceExportQuality && plugin?.SafeGameObjectManager?.LocalPlayer != null)
            {
                try
                {
                    string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                    var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
                    var collectionId = collection.EffectiveCollection.Id.ToString();
                    var textureHistory = _textureCollectionHistory[collectionId];
                    var charKeys = textureHistory.Keys.Where(k => k.StartsWith(charName + "_") && textureHistory[k].Count > 0).ToList();

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

        private void HandleJobChange(uint newJobId)
        {
            var preset = plugin.Configuration.ActiveLayerPresets?.FirstOrDefault(p => p.LinkedJobId == newJobId);
            if (preset != null)
            {
                plugin.PluginLog.Information($"[Drag And Drop Texturing] Auto-loading preset '{preset.Name}' for job {newJobId}.");
                plugin.MainWindow.ApplyPreset(preset, Plugin.SafeGameObjectManager.LocalPlayer);
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
                else if ((_lockDuplicateGeneration || _isRegenerationPending) && !_hideProgressUI)
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
                    if (_textureProcessor != null && _textureProcessor.ExportMax > 0 && !_isRegenerationPending)
                    {
                        ImGui.ProgressBar(_textureProcessor.ExportCompletion / (float)_textureProcessor.ExportMax, new Vector2(-1, 0), _exportStatus);
                    }
                    else
                    {
                        float bounce = (float)Math.Abs(Math.Sin(ImGui.GetTime() * 2.0));
                        ImGui.ProgressBar(bounce, new Vector2(-1, 0), _exportStatus);
                    }
                    ImGui.EndChild();
                }

                if (!_isDownloadingDLC && !_isWaitingForPenumbra)
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
                                Dalamud.Game.ClientState.Objects.Types.ICharacter character = item as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                                if (character != null && (item.ObjectKind == ObjectKind.Pc || item.ObjectKind == ObjectKind.Companion))
                                {
                                    string name = character.Name.TextValue;
                                    if (!string.IsNullOrEmpty(name))
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
                                    // For companions (minions), face bones don't exist so xPos is never set.
                                    // Use the center of the projected bone range instead.
                                    if (xPos == 0 && minWidth < float.MaxValue && maxWidth > 0)
                                    {
                                        xPos = (minWidth + maxWidth) / 2f;
                                    }
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
                                        selectedPlayer.Value == plugin.SafeGameObjectManager.LocalPlayer ||
                                        selectedPlayer.Value.ObjectKind == ObjectKind.Companion)
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

                        if (selectedPlayer.Value != null)
                        {
                            var dropTarget = selectedPlayer.Value;
                            Plugin.Framework.RunOnFrameworkThread(() => Plugin.MainWindow?.TrySetLayerTargetFromDrop(dropTarget));
                        }

                        // If an export is already in progress, queue the drop via InjectFilesAndRebuild
                        // which has built-in wait logic for _lockDuplicateGeneration.
                        if (_lockDuplicateGeneration && selectedPlayer.Value != null)
                        {
                            plugin.PluginLog.Information("[Drag And Drop Debug] Export in progress — queuing drop for processing after current export completes.");
                            var queuedFiles = files.ToList();
                            var queuedPlayer = selectedPlayer;
                            var queuedPart = bodyDragPart;
                            try
                            {
                                Plugin.Framework.RunOnFrameworkThread(() =>
                                {
                                    plugin.Chat.Print("[Drag And Drop Texturing] Export in progress — your drop has been queued and will process automatically.");
                                });
                            }
                            catch { }
                            InjectFilesAndRebuild(queuedFiles, queuedPlayer, queuedPart);
                            AllowClickthrough = true;
                            return;
                        }

                        if (selectedPlayer.Value != null &&
                            (selectedPlayerCollection != mainPlayerCollection ||
                             selectedPlayer.Value == plugin.SafeGameObjectManager.LocalPlayer ||
                             selectedPlayer.Value.ObjectKind == ObjectKind.Companion ||
                             selectedPlayer.Value.ObjectKind == ObjectKind.Companion))
                        {
                            plugin.PluginLog.Information("[Drag And Drop Debug] Valid player target, getting customization...");
                            modName = selectedPlayer.Key + " Texture Mod";
                            var targetCustomizationObject = selectedPlayer.Value;
                            if (selectedPlayer.Value.ObjectKind == ObjectKind.Companion)
                            {
                                var ownerId = selectedPlayer.Value.OwnerId;
                                bool found = false;
                                // OwnerId == 0xE0000000 is the local player sentinel
                                if (ownerId == 0xE0000000)
                                {
                                    var lp = plugin.SafeGameObjectManager.LocalPlayer as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                                    if (lp != null)
                                    {
                                        targetCustomizationObject = lp;
                                        selectedPlayer = new KeyValuePair<string, ICharacter>(lp.Name.TextValue, selectedPlayer.Value);
                                        found = true;
                                    }
                                }
                                if (!found)
                                {
                                    foreach (var obj in plugin.SafeGameObjectManager)
                                    {
                                        if (obj.GameObjectId == ownerId && obj is Dalamud.Game.ClientState.Objects.Types.ICharacter ownerChar)
                                        {
                                            targetCustomizationObject = ownerChar;
                                            selectedPlayer = new KeyValuePair<string, ICharacter>(ownerChar.Name.TextValue, selectedPlayer.Value);
                                            found = true;
                                            break;
                                        }
                                    }
                                }
                                if (!found)
                                {
                                    var lp = plugin.SafeGameObjectManager.LocalPlayer as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                                    targetCustomizationObject = lp;
                                    if (lp != null)
                                        selectedPlayer = new KeyValuePair<string, ICharacter>(lp.Name.TextValue, selectedPlayer.Value);
                                }
                            }
                            _currentCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(targetCustomizationObject);
                            plugin.PluginLog.Information("[Drag And Drop Debug] Customization retrieved! Starting task...");
                            Task.Run(async () =>
                            {
                                try
                                {
                                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(selectedPlayer.Value.ObjectIndex).Item3.Id;
                                    string collectionId = collection.ToString();
                                    PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(collection, _currentCustomization.Customize.Gender.Value, _currentCustomization.Customize.Clan.Value - 1, plugin);

                                    HashSet<string> dragAndDroppedCategories = new HashSet<string>();
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

                                    // For companion drops, resolve the minion name and cache gear metadata
                                    string minionCategorySuffix = null;
                                    if (selectedPlayer.Value != null && selectedPlayer.Value.ObjectKind == ObjectKind.Companion)
                                    {
                                        try
                                        {
                                            var companionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
                                            if (companionSheet != null)
                                            {
                                                var companion = companionSheet.GetRow(selectedPlayer.Value.DataId);
                                                if (companion.RowId != 0)
                                                {
                                                    string minionName = companion.Singular.ToString().ToLower().Replace(" ", "").Replace("'", "").Replace("-", "");
                                                    if (!string.IsNullOrEmpty(minionName))
                                                    {
                                                        minionCategorySuffix = "minion_" + minionName;
                                                        // Resolve the WornEquipmentPiece so RebuildCategory can find game paths
                                                        var ownerChar = targetCustomizationObject;
                                                        if (ownerChar != null)
                                                        {
                                                            var minionGear = WornEquipmentResolver.ResolveMinion(selectedPlayer.Value.DataId, collection, plugin);
                                                            string fullKey = selectedPlayer.Key + "_" + minionCategorySuffix;
                                                            if (minionGear != null && minionGear.Count > 0)
                                                            {
                                                                _gearCategoryMeta[fullKey] = minionGear[0];
                                                                plugin.PluginLog.Info($"[Drag And Drop] Cached minion gear for drag-drop: {fullKey} -> {minionGear[0].DisplayName}");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            plugin.PluginLog.Error($"[Drag And Drop] Failed to resolve minion name: {ex.Message}");
                                        }
                                        if (string.IsNullOrEmpty(minionCategorySuffix))
                                            minionCategorySuffix = "minion_body"; // fallback
                                    }

                                    // For mount drops, detect if the player is mounted via CurrentMount
                                    string mountCategorySuffix = null;
                                    bool isMountDrop = false;
                                    if (selectedPlayer.Value != null && selectedPlayer.Value.ObjectKind == ObjectKind.Pc)
                                    {
                                        try
                                        {
                                            var currentMount = selectedPlayer.Value.CurrentMount;
                                            if (currentMount != null && currentMount.Value.RowId != 0)
                                            {
                                                uint mountRowId = currentMount.Value.RowId;
                                                var mountSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Mount>();
                                                var mountRow = mountSheet?.GetRow(mountRowId);
                                                string mountSingular = mountRow?.Singular.ToString() ?? "";
                                                string mountName = mountSingular.ToLower().Replace(" ", "").Replace("'", "").Replace("-", "");

                                                // Check if any dropped file has _mount_ in its name
                                                bool hasExplicitMountFile = files.Any(f => Path.GetFileNameWithoutExtension(f).ToLower().Contains("_mount_") || Path.GetFileNameWithoutExtension(f).ToLower().StartsWith("mount"));
                                                if (hasExplicitMountFile)
                                                {
                                                    isMountDrop = true;
                                                    mountCategorySuffix = !string.IsNullOrEmpty(mountName) ? "mount_" + mountName : "mount_body";
                                                    var mountGear = WornEquipmentResolver.ResolveMount(mountRowId, collection, plugin);
                                                    string fullKey = selectedPlayer.Key + "_" + mountCategorySuffix;
                                                    if (mountGear != null && mountGear.Count > 0)
                                                    {
                                                        _gearCategoryMeta[fullKey] = mountGear[0];
                                                        plugin.PluginLog.Info($"[Drag And Drop] Cached mount gear for drag-drop: {fullKey} -> {mountGear[0].DisplayName}");
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            plugin.PluginLog.Error($"[Drag And Drop] Failed to detect mount: {ex.Message}");
                                        }
                                    }

                                    foreach (var file in files)
                                    {
                                        if (!ValidTextureExtensions.Contains(Path.GetExtension(file))) continue;
                                        string fileName = Path.GetFileNameWithoutExtension(file).ToLower();
                                        string categoryKey = selectedPlayer.Key + "_";
                                        if (selectedPlayer.Value != null && selectedPlayer.Value.ObjectKind == ObjectKind.Companion)
                                        {
                                            categoryKey += minionCategorySuffix;
                                        }
                                        else if (isMountDrop)
                                        {
                                            categoryKey += mountCategorySuffix;
                                        }
                                        else if (bodyDragPart == BodyDragPart.Clothing)
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
                                        else if (fileName.Contains("hair") || fileName.Contains("hir")) categoryKey += "gear_hair";
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
                                                case BodyDragPart.Tail: categoryKey += "tail"; break;
                                                case BodyDragPart.Hair: categoryKey += "gear_hair"; break;
                                                default: categoryKey += "fallback_" + bodyDragPart.ToString(); break;
                                            }
                                        }

                                        dragAndDroppedCategories.Add(categoryKey);
                                    }

                                    if (!plugin.Configuration.EnableTextureStacking)
                                    {
                                        foreach (var dragAndDroppedCategory in dragAndDroppedCategories)
                                        {
                                            _textureCollectionHistory[collectionId][dragAndDroppedCategory] = new List<string>();
                                        }
                                    }

                                    foreach (var file in files)
                                    {
                                        if (!ValidTextureExtensions.Contains(Path.GetExtension(file))) continue;
                                        string f = file;
                                        string fileName = Path.GetFileNameWithoutExtension(f).ToLower();
                                        string categoryKey = selectedPlayer.Key + "_";
                                        bool isBody = false;
                                        if (selectedPlayer.Value != null && selectedPlayer.Value.ObjectKind == ObjectKind.Companion)
                                        {
                                            categoryKey += minionCategorySuffix;
                                        }
                                        else if (isMountDrop)
                                        {
                                            categoryKey += mountCategorySuffix;
                                        }
                                        else if (bodyDragPart == BodyDragPart.Clothing)
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
                                        else if (fileName.Contains("tail") || fileName.Contains("sippo") || fileName.Contains("_etc_")) categoryKey += "tail";
                                        else if (fileName.Contains("hair") || fileName.Contains("hir")) categoryKey += "gear_hair";
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
                                                case BodyDragPart.Tail: categoryKey += "tail"; break;
                                                case BodyDragPart.Hair: categoryKey += "gear_hair"; break;
                                                default: categoryKey += "fallback_" + bodyDragPart.ToString(); break;
                                            }
                                        }

                                        bool isFace = categoryKey.EndsWith("face");
                                        bool needsClassification = false;
                                        bool isMinion = categoryKey.Contains("minion_");

                                        // Minions don't need texture format classification
                                        // Completely unknown drag drop destination?
                                        if (!isMinion && !isBody && !isFace && !categoryKey.Contains("gear_") && !categoryKey.EndsWith("tail") && !categoryKey.EndsWith("eyes") && !categoryKey.EndsWith("eyebrows"))
                                            needsClassification = true;

                                        // Body UV layout missing?
                                        if (!isMinion && isBody && !fileName.Contains("bibo") && !fileName.Contains("b+") && !fileName.Contains("gen3") && !fileName.Contains("tbse") && !fileName.Contains("gen2") && !fileName.Contains("mata") && !fileName.Contains("amat"))
                                            needsClassification = true;

                                        // Map type missing?
                                        if (!isMinion && (isBody || isFace || needsClassification) && !fileName.Contains("norm") && !fileName.EndsWith("_n") && !fileName.Contains("_n_") && !fileName.Contains("mask") && !fileName.EndsWith("_m") && !fileName.Contains("_m_") && !fileName.Contains("base") && !fileName.Contains("diffuse") && !fileName.EndsWith("_d") && !fileName.Contains("_d_") && !fileName.Contains("glow") && !fileName.EndsWith("_g") && !fileName.Contains("_g_"))
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
                                        if (!_textureCollectionHistory.ContainsKey(collectionId))
                                        {
                                            _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                                        }
                                        if (!_textureCollectionHistory[collectionId].ContainsKey(categoryKey))
                                        {
                                            _textureCollectionHistory[collectionId][categoryKey] = new List<string>();
                                            _textureCollectionHistoryTints[collectionId][categoryKey] = new List<System.Numerics.Vector4>();
                                        }
                                        _textureCollectionHistory[collectionId][categoryKey].Add(f);
                                        _textureCollectionHistoryTints[collectionId][categoryKey].Add(System.Numerics.Vector4.One);
                                        dragAndDroppedCategories.Add(categoryKey);
                                        Plugin.Configuration.Save();
                                        UpdateWatchers();
                                    }

                                    int effectiveRace = RaceInfo.SubRaceToMainRace(_currentCustomization.Customize.Clan.Value - 1);

                                    if (!_textureCollectionHistory.ContainsKey(collectionId))
                                    {
                                        _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                                    }
                                    foreach (var categoryKey in dragAndDroppedCategories)
                                    {
                                        if (!_textureCollectionHistory[collectionId].ContainsKey(categoryKey)
                                        || _textureCollectionHistory[collectionId][categoryKey].Count == 0) continue;

                                        // Minion categories go through the dedicated minion rebuild pipeline
                                        if (categoryKey.Contains("_minion_"))
                                        {
                                            string charName = selectedPlayer.Key;
                                            string suffix = categoryKey.Substring(charName.Length);
                                            plugin.PluginLog.Info($"[Drag And Drop] Routing minion drop to ScheduleRegeneration: charName={charName}, suffix={suffix}");
                                            ScheduleRegeneration(charName, new[] { suffix }, true, false);
                                            continue;
                                        }

                                        // Mount categories go through the dedicated mount rebuild pipeline
                                        if (categoryKey.Contains("_mount_"))
                                        {
                                            string charName = selectedPlayer.Key;
                                            string suffix = categoryKey.Substring(charName.Length);
                                            plugin.PluginLog.Info($"[Drag And Drop] Routing mount drop to ScheduleRegeneration: charName={charName}, suffix={suffix}");
                                            ScheduleRegeneration(charName, new[] { suffix }, true, false);
                                            continue;
                                        }
                                        if (!_textureCollectionHistory.ContainsKey(collectionId))
                                        {
                                            _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                                        }
                                        string lastFile = _textureCollectionHistory[collectionId][categoryKey].First();
                                        TextureSet item = null;
                                        string categoryModName = "";
                                        string overrideType = "";

                                        if (categoryKey.EndsWith("_body") && !categoryKey.Contains("_minion_") && !categoryKey.Contains("_mount_"))
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
                                        else if (categoryKey.EndsWith("_tail"))
                                        {
                                            item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 4,
                                            effectiveRace,
                                            _currentCustomization.Customize.TailShape.Value - 1, false);
                                            TryOverrideTailTextureSet(item, collection, _currentCustomization.Customize.Gender.Value, effectiveRace, _currentCustomization.Customize.TailShape.Value - 1);
                                            categoryModName = "Tail";
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
                                                case BodyDragPart.Tail:
                                                    item = ProjectHelper.CreateBodyTextureSet(_currentCustomization.Customize.Gender.Value, 4,
                                                    effectiveRace,
                                                    _currentCustomization.Customize.TailShape.Value - 1, false);
                                                    TryOverrideTailTextureSet(item, collection, _currentCustomization.Customize.Gender.Value, effectiveRace, _currentCustomization.Customize.TailShape.Value - 1);
                                                    categoryModName = "Tail";
                                                    break;
                                            }
                                        }

                                        if (item != null)
                                        {
                                            ApplyDefaultSkinType(item);
                                            if (!_textureCollectionHistory.ContainsKey(collectionId))
                                            {
                                                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                                            }
                                            if (!_textureCollectionHistoryTints.ContainsKey(collectionId))
                                            {
                                                _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
                                            }
                                            var textureHistory = _textureCollectionHistory[collectionId];
                                            var textureTintHistory = _textureCollectionHistoryTints[collectionId];
                                            for (int _i = 0; _i < textureHistory[categoryKey].Count; _i++)
                                            {
                                                try
                                                {
                                                    string f = textureHistory[categoryKey][_i];
                                                    System.Numerics.Vector4? t = textureTintHistory.ContainsKey(categoryKey) && _i < textureTintHistory[categoryKey].Count ? textureTintHistory[categoryKey][_i] : null;
                                                    AddToTextureSet(item, f, overrideType, t);
                                                }
                                                catch (Exception e)
                                                {
                                                    plugin.PluginLog.Warning(e.Message, e.StackTrace);
                                                }
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

                                            ApplyAdvancedOverlays(item, categoryKey, collectionId);
                                            plugin.PluginLog.Information($"[Glow Debug] TextureSet '{item.TextureSetName}': Base='{item.Base}', Normal='{item.Normal}', Mask='{item.Mask}', Glow='{item.Glow}', Material='{item.Material}', InternalMtrl='{item.InternalMaterialPath}'");
                                            textureSets.Add(item);

                                            string singleModName = selectedPlayer.Key + " Texture Mod";
                                            singleModName = singleModName.Replace("Mod", categoryModName);
                                            string singleFullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), singleModName);

                                            List<TextureSet> singleList = new List<TextureSet>() { item };
                                            await Export(true, singleList, singleFullModPath, singleModName, selectedPlayer);
                                        }
                                    }

                                    if (textureSets.Count == 0 && !dragAndDroppedCategories.Any(c => c.Contains("_minion_")))
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

                    var character = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
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
                _isRegenerationPending = false;

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

                        if (tPath.Contains("chara/monster/") || tPath.Contains("chara/demihuman/"))
                            category = "_minion";
                        else if (tName.Contains("body") || tName.Contains("bibo") || tName.Contains("gen3") || tName.Contains("tbse") || tPath.Contains("obj/body") || tPath.Contains("bibo") || tPath.Contains("otopop") || tPath.Contains("asym lala") || tPath.Contains("relala"))
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
                        else if (tName.Contains("tail", StringComparison.OrdinalIgnoreCase) || tPath.Contains("obj/tail", StringComparison.OrdinalIgnoreCase))
                        {
                            category = "_tail";
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

                            string baseTex = "", normTex = "", maskTex = "";

                            if (category == "_minion")
                            {
                                // Minions: extract directly from Lumina using the minion's own game paths
                                // Do NOT use ExtractActiveTextureFromPenumbra — it would pull human body textures
                                plugin.PluginLog.Information($"[Drag And Drop Debug] Minion category — extracting underlay from Lumina directly.");
                                baseTex = ExtractVanillaTexViaLumina(i.InternalBasePath, i);
                                normTex = !string.IsNullOrEmpty(i.InternalNormalPath) ? ExtractVanillaTexViaLumina(i.InternalNormalPath, i) : "";
                                maskTex = !string.IsNullOrEmpty(i.InternalMaskPath) ? ExtractVanillaTexViaLumina(i.InternalMaskPath, i) : "";
                                plugin.PluginLog.Information($"[Drag And Drop Debug] Minion Lumina extraction: base={!string.IsNullOrEmpty(baseTex)}, norm={!string.IsNullOrEmpty(normTex)}, mask={!string.IsNullOrEmpty(maskTex)}");
                            }
                            else
                            {
                                plugin.PluginLog.Information($"[Drag And Drop Debug] Extracting underlay for {category}...");
                                PenumbraAndGlamourerHelperFunctions.ExtractActiveTextureFromPenumbra(collection, category, raceCode, subRaceName, out _, out baseTex, out normTex, out maskTex, plugin, i);

                                // Refresh OmniOverrides now that they've been loaded
                                if (category == "_body")
                                {
                                    int mainRace = RaceInfo.SubRaceToMainRace(useClan);
                                    BackupTexturePaths.AddBodyBackupPaths(useGender, mainRace, i);
                                }
                            }

                            // Non-playable items (minions): base is already set directly in item.Base
                            // Do NOT set BackupTexturePaths — it would interfere with the compiler
                            if (category == "_minion")
                            {
                                i.BackupTexturePaths = null;
                                plugin.PluginLog.Information($"[Drag And Drop Debug] Minion — skipping BackupTexturePaths.");
                            }
                            else
                            {
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
                                bool hasValidBase = !string.IsNullOrEmpty(baseTex);
                                bool hasValidNorm = !string.IsNullOrEmpty(normTex);

                                string finalBase = i.BackupTexturePaths != null ? i.BackupTexturePaths.Base : "";
                                string finalNorm = i.BackupTexturePaths != null ? i.BackupTexturePaths.Normal : "";

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

                string probeGamePath = null;
                foreach (var ts in exportTextureSets)
                {
                    if (!string.IsNullOrEmpty(ts.InternalBasePath))
                    {
                        probeGamePath = ts.InternalBasePath;
                        break;
                    }
                }

                string resolvedBefore = null;
                if (!string.IsNullOrEmpty(probeGamePath))
                {
                    try { PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collection, probeGamePath, out resolvedBefore); } catch { }
                }

                // Skip AddMod on re-exports to avoid Penumbra's async Xpress8K compaction race.
                var existingMods = PenumbraAndGlamourerIpcWrapper.Instance.GetModList.Invoke();
                bool modAlreadyExists = existingMods != null && existingMods.ContainsKey(path);
                if (!modAlreadyExists)
                {
                    try { PenumbraAndGlamourerIpcWrapper.Instance.AddMod.Invoke(name); } catch { }
                    plugin.PluginLog.Information("[Drag And Drop Texturing] Mod registered with Penumbra (first time).");
                }
                try { PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(path, name); } catch { }
                collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.Value.ObjectIndex).Item3.Id;
                try { PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, path, true, name); } catch { }
                try { PenumbraAndGlamourerIpcWrapper.Instance.TrySetModPriority.Invoke(collection, path, 100, name); } catch { }
                try
                {
                    var settings = PenumbraAndGlamourerIpcWrapper.Instance.GetCurrentModSettings.Invoke(collection, path, name, true);
                    foreach (var group in settings.Item2.Value.Item3)
                    {
                        try { PenumbraAndGlamourerIpcWrapper.Instance.TrySetModSetting.Invoke(collection, path, group.Key, "Enable", name); } catch { }
                    }
                }
                catch { }

                // First-time AddMod triggers async file compaction; wait for it.
                if (!modAlreadyExists)
                {
                    Thread.Sleep(200);
                }
                else
                {
                    Thread.Sleep(100);
                }
                // Double-yield: Penumbra defers path recalculation to the next framework tick, so we nest two RunOnFrameworkThread calls to guarantee it completes first.
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character.Value);
                        if (customization != null && customization.Equipment != null)
                        {
                            // Minion mod: find the companion game object and redraw it directly
                            if (name.Contains("Minion", StringComparison.OrdinalIgnoreCase))
                            {
                                plugin.PluginLog.Info($"[Drag And Drop] Minion redraw: looking for companion owned by {character.Value.Name?.TextValue} (GameObjectId={character.Value.GameObjectId}, ObjectIndex={character.Value.ObjectIndex})");
                                bool found = false;
                                foreach (var obj in plugin.SafeGameObjectManager)
                                {
                                    if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion)
                                    {
                                        plugin.PluginLog.Info($"[Drag And Drop] Found companion: {obj.Name} OwnerId={obj.OwnerId} ObjectIndex={obj.ObjectIndex} DataId={obj.DataId}");
                                        // OwnerId == 0xE0000000 is FFXIV's sentinel for "owned by local player"
                                        bool isOwnedByCharacter = obj.OwnerId == character.Value.GameObjectId
                                            || obj.OwnerId == 0xE0000000;
                                        if (isOwnedByCharacter)
                                        {
                                            plugin.PluginLog.Info($"[Drag And Drop] Redrawing minion object: {obj.Name} (ObjectIndex={obj.ObjectIndex})");
                                            PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(obj.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                                            found = true;
                                            break;
                                        }
                                    }
                                }
                                if (!found)
                                {
                                    plugin.PluginLog.Warning($"[Drag And Drop] No companion found for owner. Trying RedrawObject on player instead.");
                                    PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.Value.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                                }
                            }
                            else if (name.Contains("face", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("eyebrow", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("hair", StringComparison.OrdinalIgnoreCase))
                            {
                                PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(character.Value.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                            }
                            else if (name.Contains("tail", StringComparison.OrdinalIgnoreCase))
                            {
                                var stateResult = PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.Value.ObjectIndex);
                                if (stateResult.Item1 == 0 && !string.IsNullOrEmpty(stateResult.Item2))
                                {
                                    var cust = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(stateResult.Item2);
                                    int originalTail = cust.Customize.TailShape.Value;

                                    // Change the tail shape to force Glamourer to reload the geometry
                                    cust.Customize.TailShape.Value = (byte)(originalTail == 1 ? 2 : 1);
                                    PenumbraAndGlamourerIpcWrapper.Instance.ApplyState.Invoke(cust.ToBase64(), character.Value.ObjectIndex);

                                    // Restore the original tail shape immediately in the same tick
                                    cust.Customize.TailShape.Value = (byte)originalTail;
                                    PenumbraAndGlamourerIpcWrapper.Instance.ApplyState.Invoke(cust.ToBase64(), character.Value.ObjectIndex);
                                }
                            }
                            else if (name.Contains("head", StringComparison.OrdinalIgnoreCase) || name.Contains("hat", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Head, (ulong)customization.Equipment.Head.ItemId, new List<byte> { (byte)customization.Equipment.Head.Stain, (byte)customization.Equipment.Head.Stain2 });
                            else if (name.Contains("hands", StringComparison.OrdinalIgnoreCase) || name.Contains("glv", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, (ulong)customization.Equipment.Hands.ItemId, new List<byte> { (byte)customization.Equipment.Hands.Stain, (byte)customization.Equipment.Hands.Stain2 });
                            else if (name.Contains("legs", StringComparison.OrdinalIgnoreCase) || name.Contains("dwn", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, (ulong)customization.Equipment.Legs.ItemId, new List<byte> { (byte)customization.Equipment.Legs.Stain, (byte)customization.Equipment.Legs.Stain2 });
                            else if (name.Contains("feet", StringComparison.OrdinalIgnoreCase) || name.Contains("sho", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, (ulong)customization.Equipment.Feet.ItemId, new List<byte> { (byte)customization.Equipment.Feet.Stain, (byte)customization.Equipment.Feet.Stain2 });
                            else if (name.Contains("ears", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Ears, (ulong)customization.Equipment.Ears.ItemId, new List<byte> { (byte)customization.Equipment.Ears.Stain, (byte)customization.Equipment.Ears.Stain2 });
                            else if (name.Contains("neck", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Neck, (ulong)customization.Equipment.Neck.ItemId, new List<byte> { (byte)customization.Equipment.Neck.Stain, (byte)customization.Equipment.Neck.Stain2 });
                            else if (name.Contains("wrists", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Wrists, (ulong)customization.Equipment.Wrists.ItemId, new List<byte> { (byte)customization.Equipment.Wrists.Stain, (byte)customization.Equipment.Wrists.Stain2 });
                            else if (name.Contains("ring_r", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.RFinger, (ulong)customization.Equipment.RFinger.ItemId, new List<byte> { (byte)customization.Equipment.RFinger.Stain, (byte)customization.Equipment.RFinger.Stain2 });
                            else if (name.Contains("ring_l", StringComparison.OrdinalIgnoreCase))
                                PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.LFinger, (ulong)customization.Equipment.LFinger.ItemId, new List<byte> { (byte)customization.Equipment.LFinger.Stain, (byte)customization.Equipment.LFinger.Stain2 });
                            else
                            {
                                Task.Run(() =>
                                {
                                    // A body texture update (e.g. Bibo+) affects multiple equipment slots since the mesh is often split.

                                    Plugin.Framework.RunOnFrameworkThread(() =>
                                    {
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Body, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Body, (ulong)customization.Equipment.Body.ItemId, new List<byte> { (byte)customization.Equipment.Body.Stain, (byte)customization.Equipment.Body.Stain2 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, (ulong)customization.Equipment.Legs.ItemId, new List<byte> { (byte)customization.Equipment.Legs.Stain, (byte)customization.Equipment.Legs.Stain2 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, (ulong)customization.Equipment.Feet.ItemId, new List<byte> { (byte)customization.Equipment.Feet.Stain, (byte)customization.Equipment.Feet.Stain2 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, (ulong)customization.Equipment.Hands.ItemId, new List<byte> { (byte)customization.Equipment.Hands.Stain, (byte)customization.Equipment.Hands.Stain2 });
                                    });

                                    Task.Delay(500);

                                    Plugin.Framework.RunOnFrameworkThread(() =>
                                    {
                                        // Double tap in case the first refresh failed.
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Body, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, 0, new List<byte> { 0 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Body, (ulong)customization.Equipment.Body.ItemId, new List<byte> { (byte)customization.Equipment.Body.Stain, (byte)customization.Equipment.Body.Stain2 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Legs, (ulong)customization.Equipment.Legs.ItemId, new List<byte> { (byte)customization.Equipment.Legs.Stain, (byte)customization.Equipment.Legs.Stain2 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Feet, (ulong)customization.Equipment.Feet.ItemId, new List<byte> { (byte)customization.Equipment.Feet.Stain, (byte)customization.Equipment.Feet.Stain2 });
                                        PenumbraAndGlamourerIpcWrapper.Instance.SetItem.Invoke(character.Value.ObjectIndex, Glamourer.Api.Enums.ApiEquipSlot.Hands, (ulong)customization.Equipment.Hands.ItemId, new List<byte> { (byte)customization.Equipment.Hands.Stain, (byte)customization.Equipment.Hands.Stain2 });
                                    });
                                });
                            }
                        }
                    });
                });

                plugin.PluginLog.Information("[Drag And Drop Texturing] Import complete! Created mod is toggleable in penumbra.");
                sw.Stop();
                plugin.PluginLog.Information($"[Drag & Drop] Generated in {compilationTime}ms. Total Export+IPC: {sw.ElapsedMilliseconds}ms.");

                // Notify animated layers that static textures changed so they re-render onto the new base
                try
                {
                    string categorySuffix = name.Contains("Body", StringComparison.OrdinalIgnoreCase) ? "body" :
                                            name.Contains("Face", StringComparison.OrdinalIgnoreCase) ? "face" : null;
                    if (categorySuffix != null)
                        plugin.AnimatedLayerManager?.OnStaticLayersChanged(character.Key, categorySuffix);
                }
                catch { }
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
                        var character = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
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
                string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                var playerCollectionResult = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
                var collectionId = playerCollectionResult.EffectiveCollection.Id.ToString();
                if (!penumbraReady) return;
                if (!_textureCollectionHistory.ContainsKey(collectionId))
                {
                    _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                }
                if (_textureCollectionHistory[collectionId].Count == 0) return;

                var charKeys = _textureCollectionHistory[collectionId].Keys.Where(k => k.StartsWith(charName + "_") && _textureCollectionHistory[collectionId].Count > 0).ToList();
                if (charKeys.Count == 0) return;

                // Snapshot the current customization
                var character2 = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
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

                // Wait for the last rebuild to finish, then refresh Penumbra Found Mods
                int finalWait = 0;
                while (_lockDuplicateGeneration && finalWait < 60)
                {
                    Thread.Sleep(1000);
                    finalWait++;
                }
                RefreshActiveOverrides();
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
                if (_bulkRebuildInProgress || _lockDuplicateGeneration) return;
                if (plugin?.SafeGameObjectManager?.LocalPlayer == null) return;
                if (e.GameObjectPtr != plugin.SafeGameObjectManager.LocalPlayer.Address) return;

                var character = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
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
                    ScheduleRegeneration(charName, partsToRegenerate.ToArray(), !(raceChanged || genderChanged), false);
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
                if (_bulkRebuildInProgress || _lockDuplicateGeneration) return;
                if (plugin?.SafeGameObjectManager?.LocalPlayer == null) return;

                string modDir = e.ModDirectory?.ToLower() ?? "";
                plugin?.PluginLog?.Information($"[Drag And Drop Debug] ModSettingChanged: Type={e.ChangeType}, ModDir='{e.ModDirectory}', CollectionId={e.CollectionId}, Inherited={e.Inherited}");

                // Skip our own generated mods
                if (modDir.Contains("drag and drop") || modDir.Contains("do_not_edit") || modDir.Contains("texture body") || modDir.Contains("texture face") || modDir.Contains("texture eyes") || modDir.Contains("texture eyebrows") || modDir.Contains("texture gear") || modDir.Contains("texture mod")) return;

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

                bool hasAdvancedOverlay = false;
                try
                {
                    string fullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), e.ModDirectory);
                    if (Directory.Exists(fullModPath))
                    {
                        if (File.Exists(Path.Combine(fullModPath, "metadata.json")))
                        {
                            hasAdvancedOverlay = true;
                        }
                        else
                        {
                            foreach (var d in Directory.GetDirectories(fullModPath))
                            {
                                if (File.Exists(Path.Combine(d, "metadata.json")))
                                {
                                    hasAdvancedOverlay = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                if (isSkinMod || isInheritanceChange || hasAdvancedOverlay)
                {
                    string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
                    if (isInheritanceChange)
                        plugin.PluginLog.Information($"[Drag And Drop Texturing] Inheritance change detected (ModDir: '{e.ModDirectory}', Collection: {e.CollectionId}). Rebuilding body textures...");
                    else
                        plugin.PluginLog.Information("[Drag And Drop Texturing] Skin mod change detected (" + e.ModDirectory + "). Rebuilding body textures...");
                    ScheduleRegeneration(charName, new[] { "_body" }, true, false);
                }
            }
            catch (Exception ex)
            {
                plugin?.PluginLog?.Warning(ex, "Error in mod setting change handler");
            }
        }

        public void ScheduleRegeneration(string charName, string[] categorySuffixes, bool skipDelays = false, bool hideProgressUI = true)
        {
            if (!hideProgressUI)
            {
                _isRegenerationPending = true;
                _hideProgressUI = false;
                _exportStatus = "Waiting for Penumbra...";
                _currentTarget = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
            }
            lock (_regenerationLock)
            {
                foreach (var suffix in categorySuffixes)
                {
                    string key = charName + suffix;
                    var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
                    var collectionId = collection.EffectiveCollection.Id.ToString();
                    if (!_textureCollectionHistory.ContainsKey(collectionId))
                    {
                        _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                    }
                    if (_textureCollectionHistory[collectionId].ContainsKey(key))
                    {
                        _pendingRegenerationCategories.Add(key);
                        plugin.PluginLog.Info($"[ScheduleRegeneration] Added pending key: {key}");
                    }
                    else
                    {
                        plugin.PluginLog.Warning($"[ScheduleRegeneration] Key NOT found in history: {key} (history has {_textureCollectionHistory[collectionId].Count} keys: [{string.Join(", ", _textureCollectionHistory[collectionId].Keys)}])");
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
                    if (categories.Count == 0)
                    {
                        _isRegenerationPending = false;
                        _hideProgressUI = true;
                        return;
                    }
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

                    // Wait for the last rebuild to finish before refreshing overrides
                    int finalWait = 0;
                    while (_lockDuplicateGeneration && finalWait < 60)
                    {
                        Thread.Sleep(1000);
                        finalWait++;
                    }

                    // Refresh the Penumbra Found Mods / ActiveBodyOverrides after rebuilds
                    // so the UI and subsequent exports reflect the new race's mods
                    RefreshActiveOverrides();
                }, null, skipDelays ? 200 : 2000, System.Threading.Timeout.Infinite);
            }
        }
        private void TryOverrideTailTextureSet(TextureSet item, Guid collectionId, int gender, int race, int tailShape)
        {
            try
            {
                string xaelaCheck = (race == 7 ? "010" : "000") + (tailShape + 1);
                string genderRaceCode = (gender == 0 ? FFXIVLooseTextureCompiler.Racial.RaceInfo.RaceCodeBody.Masculine[race] : FFXIVLooseTextureCompiler.Racial.RaceInfo.RaceCodeBody.Feminine[race]);

                string tailMdlPath = $"chara/human/c{genderRaceCode}/obj/tail/t{xaelaCheck}/model/c{genderRaceCode}t{xaelaCheck}_til.mdl";
                Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Checking for modded tail model at path: {tailMdlPath}");

                if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryResolveGamePath(collectionId, tailMdlPath, out string mdlDiskPath))
                {
                    Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Modded tail model FOUND at: {mdlDiskPath}");
                    byte[] mdlBytes = System.IO.File.ReadAllBytes(mdlDiskPath);
                    string mdlString = System.Text.Encoding.ASCII.GetString(mdlBytes);
                    var matches = System.Text.RegularExpressions.Regex.Matches(mdlString, @"[\w/\-]+\.mtrl");

                    Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Extracted {matches.Count} .mtrl string(s) from .mdl file.");

                    foreach (System.Text.RegularExpressions.Match match in matches)
                    {
                        string mtrlName = match.Value.TrimStart('/');
                        string mtrlCandidate = $"chara/human/c{genderRaceCode}/obj/tail/t{xaelaCheck}/material/v0001/{mtrlName}";
                        Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Checking extracted material candidate: {mtrlCandidate}");

                        if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryResolveGamePath(collectionId, mtrlCandidate, out string mtrlDiskPath))
                        {
                            Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Modded material FOUND at: {mtrlDiskPath}");
                            if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryReadMtrlTexturePaths(mtrlDiskPath, out string bPath, out string nPath, out string mPath))
                            {
                                if (!string.IsNullOrEmpty(bPath)) item.InternalBasePath = bPath;
                                if (!string.IsNullOrEmpty(nPath)) item.InternalNormalPath = nPath;
                                if (!string.IsNullOrEmpty(mPath)) item.InternalMaskPath = mPath;
                                item.InternalMaterialPath = mtrlCandidate;
                                Plugin.PluginLog.Information($"[Drag And Drop Debug] Custom Tail Paths Overridden from {tailMdlPath} -> {mtrlCandidate} (Base: {bPath})");
                                break;
                            }
                            else
                            {
                                Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Failed to read texture paths from material.");
                            }
                        }
                        else
                        {
                            Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: Material candidate was not found in Penumbra.");
                        }
                    }
                }
                else
                {
                    Plugin.PluginLog.Information($"[Drag And Drop Debug] TailOverride: No modded tail model found. Vanilla path assumed.");
                }
            }
            catch (Exception ex)
            {
                Plugin.PluginLog.Warning(ex, "[Drag And Drop Debug] Failed to check for modded tail materials.");
            }
        }

        public Task InjectFilesAndRebuild(List<string> extractedFiles, KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter> selectedPlayer, BodyDragPart bodyDragPart)
        {
            _currentTarget = selectedPlayer.Value;
            if (selectedPlayer.Value != null)
            {
                var dropTarget = selectedPlayer.Value;
                Plugin.Framework.RunOnFrameworkThread(() => Plugin.MainWindow?.TrySetLayerTargetFromDrop(dropTarget));
            }
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
                    else if (fileName.Contains("tail") || fileName.Contains("sippo") || fileName.Contains("_etc_")) categoryKey += "tail";
                    else if (fileName.Contains("hair") || fileName.Contains("hir")) categoryKey += "gear_hair";
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
                            case BodyDragPart.Tail: categoryKey += "tail"; break;
                            case BodyDragPart.Hair: categoryKey += "gear_hair"; break;
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

                    var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
                    var collectionId = collection.EffectiveCollection.Id.ToString();
                    if (!_textureCollectionHistory.ContainsKey(collectionId))
                    {
                        _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                    }
                    if (!_textureCollectionHistoryTints.ContainsKey(collectionId))
                    {
                        _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
                    }
                    var textureHistory = _textureCollectionHistory[collectionId];
                    var textureHistoryTints = _textureCollectionHistoryTints[collectionId];
                    if (!textureHistory.ContainsKey(categoryKey))
                    {
                        textureHistory[categoryKey] = new List<string>();
                    }
                    if (!textureHistoryTints.ContainsKey(categoryKey))
                    {
                        textureHistoryTints[categoryKey] = new List<System.Numerics.Vector4>();
                        for (int h = 0; h < textureHistory[categoryKey].Count; h++)
                        {
                            textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
                        }
                    }

                    if (!plugin.Configuration.EnableTextureStacking && !droppedCategories.Contains(categoryKey))
                    {
                        textureHistory[categoryKey].Clear();
                        textureHistoryTints[categoryKey].Clear();
                    }

                    if (!textureHistory[categoryKey].Contains(file))
                    {
                        textureHistory[categoryKey].Add(file);
                        textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
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
            if (plugin?.SafeGameObjectManager?.LocalPlayer == null) return;

            var localPlayer = plugin.SafeGameObjectManager.LocalPlayer;
            string charName = localPlayer.Name.TextValue;
            var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex);
            var collectionId = collection.EffectiveCollection.Id.ToString();
            if (!_textureCollectionHistory.ContainsKey(collectionId))
            {
                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
            }
            EnsureActiveContextualLayerCategories(collectionId);
            var textureHistory = _textureCollectionHistory[collectionId];
            if (textureHistory == null || textureHistory.Count == 0) return;

            var activeContextualKeys = new HashSet<string>(StringComparer.Ordinal);
            if (plugin.ContextualLayerManager != null)
            {
                foreach (var active in plugin.ContextualLayerManager.GetActiveLayers())
                {
                    string part = active?.LayerDef?.TargetBodyPart;
                    if (!string.IsNullOrWhiteSpace(part))
                        activeContextualKeys.Add(charName + "_" + part.ToLowerInvariant());
                }
            }

            var categoriesToRebuild = textureHistory.Keys
                .Where(k => k.StartsWith(charName + "_", StringComparison.Ordinal)
                    && (textureHistory[k].Count > 0 || activeContextualKeys.Contains(k)))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
            if (categoriesToRebuild.Count == 0) return;

            plugin.PluginLog.Information($"[Drag And Drop Texturing] Bulk re-export started for {categoriesToRebuild.Count} categories in collection {collectionId}.");

            Task.Run(() =>
            {
                _bulkRebuildInProgress = true;
                lock (_regenerationLock)
                {
                    _pendingRegenerationCategories.Clear();
                    _regenerationDebounce?.Dispose();
                    _regenerationDebounce = null;
                }
                try
                {
                    foreach (var categoryKey in categoriesToRebuild)
                    {
                        int waitAttempts = 0;
                        while (_lockDuplicateGeneration && waitAttempts < 120)
                        {
                            Thread.Sleep(1000);
                            waitAttempts++;
                        }
                        RebuildCategory(categoryKey, false);
                        Thread.Sleep(500);
                    }

                    int finalWait = 0;
                    while (_lockDuplicateGeneration && finalWait < 120)
                    {
                        Thread.Sleep(1000);
                        finalWait++;
                    }
                }
                finally
                {
                    _bulkRebuildInProgress = false;
                }
            });
        }

        private void EnsureActiveContextualLayerCategories(string collectionId)
        {
            if (plugin?.ContextualLayerManager == null || plugin.SafeGameObjectManager?.LocalPlayer == null)
                return;

            var activeLayers = plugin.ContextualLayerManager.GetActiveLayers();
            if (activeLayers == null || activeLayers.Count == 0)
                return;

            string charName = plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue;
            if (!_textureCollectionHistory.ContainsKey(collectionId))
                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();

            if (!_textureCollectionHistoryTints.ContainsKey(collectionId))
                _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();

            foreach (var activeLayer in activeLayers)
            {
                string targetPart = activeLayer?.LayerDef?.TargetBodyPart;
                if (string.IsNullOrWhiteSpace(targetPart))
                    continue;

                string categoryKey = charName + "_" + targetPart.ToLowerInvariant();
                if (!_textureCollectionHistory[collectionId].ContainsKey(categoryKey))
                    _textureCollectionHistory[collectionId][categoryKey] = new List<string>();

                if (!_textureCollectionHistoryTints[collectionId].ContainsKey(categoryKey))
                    _textureCollectionHistoryTints[collectionId][categoryKey] = new List<Vector4>();
            }
        }
        /// <summary>
        /// Fully separate minion texture rebuild pipeline.
        /// Does NOT touch any body/face/gear/skin/contextual layer logic.
        /// </summary>
        private void RebuildMinionCategory(string categoryKey, bool hideProgressUI)
        {
            var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
            var collectionId = collection.EffectiveCollection.Id.ToString();
            if (!_textureCollectionHistory.ContainsKey(collectionId))
            {
                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
            }
            var textureHistory = _textureCollectionHistory[collectionId];
            if (!textureHistory.ContainsKey(categoryKey)) return;

            string charName = categoryKey.Substring(0, categoryKey.IndexOf("_minion_"));
            ICharacter character = null;
            if (plugin.SafeGameObjectManager.LocalPlayer != null && plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue == charName)
                character = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;

            if (character == null)
            {
                _isRegenerationPending = false;
                plugin.PluginLog.Warning($"[MINION REBUILD] Character '{charName}' not found. Cannot export minion.");
                return;
            }

            Task.Run(async () =>
            {
                int waitAttempts = 0;
                while (_lockDuplicateGeneration && waitAttempts < 60)
                {
                    Thread.Sleep(1000);
                    waitAttempts++;
                }
                if (_lockDuplicateGeneration)
                {
                    _isRegenerationPending = false;
                    return;
                }

                try
                {
                    // Get cached minion gear metadata, or auto-resolve if missing
                    if (!_gearCategoryMeta.TryGetValue(categoryKey, out var gearMeta))
                    {
                        plugin.PluginLog.Info($"[MINION REBUILD] No cached gearMeta for '{categoryKey}'. Attempting auto-resolve...");
                        try
                        {
                            var localPlayer = plugin.SafeGameObjectManager.LocalPlayer;
                            if (localPlayer != null)
                            {
                                // Find the companion object to get its DataId
                                uint minionDataId = 0;
                                foreach (var obj in plugin.SafeGameObjectManager)
                                {
                                    if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion
                                        && (obj.OwnerId == localPlayer.GameObjectId || obj.OwnerId == 0xE0000000))
                                    {
                                        minionDataId = obj.DataId;
                                        break;
                                    }
                                }
                                if (minionDataId != 0)
                                {
                                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                                    var resolved = WornEquipmentResolver.ResolveMinion(minionDataId, collection, plugin);
                                    if (resolved != null && resolved.Count > 0)
                                    {
                                        gearMeta = resolved[0];
                                        _gearCategoryMeta[categoryKey] = gearMeta;
                                        plugin.PluginLog.Info($"[MINION REBUILD] Auto-resolved gearMeta: {gearMeta.DisplayName}");
                                    }
                                }
                            }
                        }
                        catch (Exception resolveEx)
                        {
                            plugin.PluginLog.Error($"[MINION REBUILD] Auto-resolve failed: {resolveEx.Message}");
                        }

                        if (gearMeta == null)
                        {
                            plugin.PluginLog.Warning($"[MINION REBUILD] Could not resolve gearMeta for '{categoryKey}'. Cannot export.");
                            _isRegenerationPending = false;
                            return;
                        }
                    }

                    plugin.PluginLog.Info($"[MINION REBUILD] Starting rebuild for '{categoryKey}'. DisplayName={gearMeta.DisplayName}, BasePath={gearMeta.InternalBasePath}");

                    // Create the TextureSet from the minion's internal paths
                    TextureSet item = ProjectHelper.CreateEquipmentTextureSet(
                        gearMeta.DisplayName,
                        gearMeta.InternalBasePath,
                        gearMeta.InternalNormalPath,
                        gearMeta.InternalMaskPath,
                        gearMeta.InternalMaterialPath);

                    if (item == null)
                    {
                        plugin.PluginLog.Warning($"[MINION REBUILD] CreateEquipmentTextureSet returned null.");
                        _isRegenerationPending = false;
                        return;
                    }

                    // Flag as non-playable so the LooseTextureCompiler skips UV conversion
                    // and other humanoid-specific processing
                    item.NotAPlayableItem = true;
                    // Skip normal/mask generation — just pass-through the Lumina textures directly
                    // The MergeNormal path would otherwise stretch the 256x256 minion normal
                    item.IgnoreNormalGeneration = true;
                    item.IgnoreMaskGeneration = true;

                    // Extract the minion's actual texture from Lumina and use it as the base
                    // This ensures the real minion texture is the underlay, not a human body
                    string minionBasePng = ExtractVanillaTexViaLumina(gearMeta.InternalBasePath, item, forceOpaqueAlpha: true);
                    if (!string.IsNullOrEmpty(minionBasePng))
                    {
                        item.Base = minionBasePng;
                        item.BaseUV = "";
                        item.BaseTint = System.Numerics.Vector4.One;
                        plugin.PluginLog.Info($"[MINION REBUILD] Set Lumina base texture: {minionBasePng}");
                    }
                    else
                    {
                        plugin.PluginLog.Warning($"[MINION REBUILD] Failed to extract Lumina base for {gearMeta.InternalBasePath}");
                    }

                    // Extract normal map from Lumina too
                    if (!string.IsNullOrEmpty(gearMeta.InternalNormalPath))
                    {
                        string minionNormPng = ExtractVanillaTexViaLumina(gearMeta.InternalNormalPath, item);
                        if (!string.IsNullOrEmpty(minionNormPng))
                        {
                            item.Normal = minionNormPng;
                            item.NormalUV = "";
                        }
                    }

                    // Pre-composite base + paint overlays into a single PNG
                    // This bypasses the compiler's GPU merge pipeline (memory:\\ .raw)
                    // which has stride issues at small (256x256) resolutions
                    {
                        var overlayPaths = new List<string>();
                        var overlayTints = new List<Vector4>();
                        var playerCollection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
                        var playerCollectionId = playerCollection.EffectiveCollection.Id.ToString();
                        if (!_textureCollectionHistory.ContainsKey(playerCollectionId))
                        {
                            _textureCollectionHistory[playerCollectionId] = new Dictionary<string, List<string>>();
                        }
                        if (!_textureCollectionHistoryTints.ContainsKey(playerCollectionId))
                        {
                            _textureCollectionHistoryTints[playerCollectionId] = new Dictionary<string, List<Vector4>>();
                        }
                        var textureHistory = _textureCollectionHistory[playerCollectionId];
                        var textureHistoryTints = _textureCollectionHistoryTints[playerCollectionId];
                        for (int i = 0; i < textureHistory[categoryKey].Count; i++)
                        {
                            string f = textureHistory[categoryKey][i];
                            Vector4 t = (textureHistoryTints.ContainsKey(categoryKey) && i < textureHistoryTints[categoryKey].Count)
                                ? textureHistoryTints[categoryKey][i] : System.Numerics.Vector4.One;
                            if (File.Exists(f))
                            {
                                overlayPaths.Add(f);
                                overlayTints.Add(t);
                            }
                            plugin.PluginLog.Info($"[MINION REBUILD] Overlay[{i}]: exists={File.Exists(f)} tint={t} path={f}");
                        }

                        if (overlayPaths.Count == 0)
                        {
                            plugin.PluginLog.Info($"[MINION REBUILD] No overlay layers for '{categoryKey}'. Disabling mod and reverting to vanilla.");
                            // Disable the Penumbra mod so the minion reverts to vanilla
                            try
                            {
                                string cleanName = gearMeta.DisplayName.Replace("(", "").Replace(")", "").Trim();
                                cleanName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanName.ToLower());
                                string modName = charName + " Texture " + cleanName;
                                string modPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), modName);
                                var localPlayer = plugin.SafeGameObjectManager.LocalPlayer;
                                if (localPlayer != null)
                                {
                                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                                    var collectionId = collection.ToString();
                                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, modPath, false, modName);
                                    plugin.PluginLog.Info($"[MINION REBUILD] Disabled mod '{modName}' in collection {collection}");
                                    // Redraw the companion
                                    Plugin.Framework.RunOnFrameworkThread(() =>
                                    {
                                        foreach (var obj in plugin.SafeGameObjectManager)
                                        {
                                            if (obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion
                                                && (obj.OwnerId == localPlayer.GameObjectId || obj.OwnerId == 0xE0000000))
                                            {
                                                plugin.PluginLog.Info($"[MINION REBUILD] Redrawing companion after layer removal: {obj.Name} (ObjectIndex={obj.ObjectIndex})");
                                                PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(obj.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                                                break;
                                            }
                                        }
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                plugin.PluginLog.Error($"[MINION REBUILD] Failed to disable mod: {ex.Message}");
                            }
                            _isRegenerationPending = false;
                            return;
                        }

                        // Composite: draw base, then each overlay on top with alpha blending + tint
                        using (var baseBmp = new Bitmap(item.Base))
                        {
                            int w = baseBmp.Width;
                            int h = baseBmp.Height;
                            using (var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                            {
                                using (var g = Graphics.FromImage(canvas))
                                {
                                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    // Draw opaque base first
                                    g.DrawImage(baseBmp, 0, 0, w, h);
                                    // Layer each paint overlay on top, applying tint if needed
                                    for (int idx = 0; idx < overlayPaths.Count; idx++)
                                    {
                                        using (var overlay = new Bitmap(overlayPaths[idx]))
                                        {
                                            var tint = overlayTints[idx];
                                            if (tint == System.Numerics.Vector4.One)
                                            {
                                                g.DrawImage(overlay, 0, 0, w, h);
                                            }
                                            else
                                            {
                                                // Apply tint via ColorMatrix
                                                var cm = new System.Drawing.Imaging.ColorMatrix(new float[][] {
                                                    new float[] { tint.X, 0, 0, 0, 0 },
                                                    new float[] { 0, tint.Y, 0, 0, 0 },
                                                    new float[] { 0, 0, tint.Z, 0, 0 },
                                                    new float[] { 0, 0, 0, tint.W, 0 },
                                                    new float[] { 0, 0, 0, 0, 1 }
                                                });
                                                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                                                {
                                                    attrs.SetColorMatrix(cm);
                                                    g.DrawImage(overlay,
                                                        new System.Drawing.Rectangle(0, 0, w, h),
                                                        0, 0, overlay.Width, overlay.Height,
                                                        GraphicsUnit.Pixel, attrs);
                                                }
                                            }
                                        }
                                    }
                                }
                                // Save pre-composited result
                                string tempDir = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", "vanilla_cache");
                                Directory.CreateDirectory(tempDir);
                                string compositedPath = Path.Combine(tempDir, $"minion_composited_{categoryKey.GetHashCode():X8}.png");
                                canvas.Save(compositedPath, System.Drawing.Imaging.ImageFormat.Png);
                                // Set this as the sole base — no overlays for the compiler
                                item.Base = compositedPath;
                                plugin.PluginLog.Info($"[MINION REBUILD] Pre-composited {overlayPaths.Count} overlays → {compositedPath} ({w}x{h})");
                            }
                        }
                    }

                    // Build the mod name: "CharName Texture Minion Fat Cat"
                    string cleanMinionName = gearMeta.DisplayName;
                    cleanMinionName = cleanMinionName.Replace("(", "").Replace(")", "").Trim();
                    cleanMinionName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanMinionName.ToLower());
                    string localModName = charName + " Texture " + cleanMinionName;

                    var textureSets = new List<TextureSet> { item };
                    string fullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), localModName);

                    plugin.PluginLog.Info($"[MINION REBUILD] Exporting. ModName={localModName}, Path={fullModPath}, Layers={_textureCollectionHistory[collectionId][categoryKey].Count}");

                    // Clean stale tex files from previous exports to avoid dimension mismatches
                    string texDir = Path.Combine(fullModPath, "do_not_edit", "textures");
                    if (Directory.Exists(texDir))
                    {
                        foreach (var staleFile in Directory.GetFiles(texDir, "*.tex"))
                        {
                            try { File.Delete(staleFile); } catch { }
                        }
                    }

                    // Export — pass character info for collection binding, but the TextureSet uses minion paths
                    var localCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                    await Export(true, textureSets, fullModPath, localModName,
                        new KeyValuePair<string, ICharacter>(character.Name.TextValue, character),
                        localCustomization.Customize.Race.Value - 1,
                        localCustomization.Customize.Clan.Value - 1,
                        localCustomization.Customize.Gender.Value,
                        localCustomization.Customize.Face.Value - 1,
                        hideProgressUI);

                    plugin.PluginLog.Info($"[MINION REBUILD] Export completed for '{localModName}'.");
                }
                catch (Exception e)
                {
                    _isRegenerationPending = false;
                    plugin.PluginLog.Error($"[MINION REBUILD] Crash: {e.Message}");
                    Plugin.PluginLog.Warning(e, e.Message);
                }
            });
        }

        private void RebuildMountCategory(string categoryKey, bool hideProgressUI)
        {

            string charName = categoryKey.Substring(0, categoryKey.IndexOf("_mount_"));
            ICharacter character = null;
            var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(plugin.SafeGameObjectManager.LocalPlayer.ObjectIndex);
            var collectionId = collection.EffectiveCollection.Id.ToString();
            if (!_textureCollectionHistory.ContainsKey(collectionId))
            {
                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
            }
            var textureHistory = _textureCollectionHistory[collectionId];
            if (plugin.SafeGameObjectManager.LocalPlayer != null && plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue == charName)
                character = plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
            if (!textureHistory.ContainsKey(categoryKey)) return;

            if (character == null)
            {
                _isRegenerationPending = false;
                plugin.PluginLog.Warning($"[MOUNT REBUILD] Character '{charName}' not found. Cannot export mount.");
                return;
            }

            Task.Run(async () =>
            {
                int waitAttempts = 0;
                while (_lockDuplicateGeneration && waitAttempts < 60)
                {
                    Thread.Sleep(1000);
                    waitAttempts++;
                }
                if (_lockDuplicateGeneration)
                {
                    _isRegenerationPending = false;
                    return;
                }

                try
                {

                    var localPlayer = plugin.SafeGameObjectManager.LocalPlayer;
                    // Get cached mount gear metadata, or auto-resolve if missing
                    if (!_gearCategoryMeta.TryGetValue(categoryKey, out var gearMeta))
                    {
                        plugin.PluginLog.Info($"[MOUNT REBUILD] No cached gearMeta for '{categoryKey}'. Attempting auto-resolve...");
                        try
                        {
                            if (localPlayer != null)
                            {
                                // Mounts are embedded in the character, use CurrentMount property
                                uint mountDataId = 0;
                                try
                                {
                                    var currentMount = localPlayer.CurrentMount;
                                    if (currentMount != null && currentMount.Value.RowId != 0)
                                    {
                                        mountDataId = currentMount.Value.RowId;
                                    }
                                }
                                catch { }
                                if (mountDataId != 0)
                                {
                                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                                    var resolved = WornEquipmentResolver.ResolveMount(mountDataId, collection, plugin);
                                    if (resolved != null && resolved.Count > 0)
                                    {
                                        gearMeta = resolved[0];
                                        _gearCategoryMeta[categoryKey] = gearMeta;
                                        plugin.PluginLog.Info($"[MOUNT REBUILD] Auto-resolved gearMeta: {gearMeta.DisplayName}");
                                    }
                                }
                            }
                        }
                        catch (Exception resolveEx)
                        {
                            plugin.PluginLog.Error($"[MOUNT REBUILD] Auto-resolve failed: {resolveEx.Message}");
                        }

                        if (gearMeta == null)
                        {
                            plugin.PluginLog.Warning($"[MOUNT REBUILD] No gearMeta found for '{categoryKey}'. Cannot export.");
                            _isRegenerationPending = false;
                            return;
                        }
                    }

                    // Build TextureSet using mount paths (same approach as minion)
                    var item = ProjectHelper.CreateEquipmentTextureSet(
                        gearMeta.DisplayName,
                        gearMeta.InternalBasePath,
                        gearMeta.InternalNormalPath,
                        gearMeta.InternalMaskPath,
                        gearMeta.InternalMaterialPath);

                    if (!_textureCollectionHistory.ContainsKey(collectionId))
                    {
                        _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                    }
                    var textureHistory = _textureCollectionHistory[collectionId];
                    // If mount has no overlay layers, disable mod and revert to vanilla
                    if (textureHistory[categoryKey].Count == 0 || textureHistory[categoryKey].All(f => !File.Exists(f)))
                    {
                        plugin.PluginLog.Info($"[MOUNT REBUILD] No overlay layers for '{categoryKey}'. Disabling mod and reverting to vanilla.");
                        try
                        {
                            string cleanName = gearMeta.DisplayName.Replace("(", "").Replace(")", "").Trim();
                            cleanName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanName.ToLower());
                            string modName = charName + " Texture " + cleanName;
                            string modPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), modName);
                            PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection.EffectiveCollection.Id, modPath, false, modName);
                            plugin.PluginLog.Info($"[MOUNT REBUILD] Disabled mod '{modName}' in collection {collection}");
                            // Redraw the player (mount is part of the player object)
                            Plugin.Framework.RunOnFrameworkThread(() =>
                            {
                                plugin.PluginLog.Info($"[MOUNT REBUILD] Redrawing player after mount layer removal: {localPlayer.Name} (ObjectIndex={localPlayer.ObjectIndex})");
                                PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(localPlayer.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                            });
                        }
                        catch (Exception ex)
                        {
                            plugin.PluginLog.Error($"[MOUNT REBUILD] Failed to disable mod: {ex.Message}");
                        }
                        _isRegenerationPending = false;
                        return;
                    }

                    // Flag as non-playable so the LooseTextureCompiler skips UV conversion
                    item.NotAPlayableItem = true;
                    item.IgnoreNormalGeneration = true;
                    item.IgnoreMaskGeneration = true;

                    // Extract the mount's actual texture from Lumina and use it as the base
                    string mountBasePng = ExtractVanillaTexViaLumina(gearMeta.InternalBasePath, item, forceOpaqueAlpha: true);
                    if (!string.IsNullOrEmpty(mountBasePng))
                    {
                        item.Base = mountBasePng;
                        item.BaseUV = "";
                        item.BaseTint = System.Numerics.Vector4.One;
                        plugin.PluginLog.Info($"[MOUNT REBUILD] Set Lumina base texture: {mountBasePng}");
                    }
                    else
                    {
                        plugin.PluginLog.Warning($"[MOUNT REBUILD] Failed to extract Lumina base for {gearMeta.InternalBasePath}");
                    }

                    // Extract normal map from Lumina too
                    if (!string.IsNullOrEmpty(gearMeta.InternalNormalPath))
                    {
                        string mountNormPng = ExtractVanillaTexViaLumina(gearMeta.InternalNormalPath, item);
                        if (!string.IsNullOrEmpty(mountNormPng))
                        {
                            item.Normal = mountNormPng;
                            item.NormalUV = "";
                        }
                    }

                    // Pre-composite base + paint overlays into a single PNG
                    {
                        var overlayPaths = new List<string>();
                        var overlayTints = new List<System.Numerics.Vector4>();
                        if (!_textureCollectionHistory.ContainsKey(collectionId))
                        {
                            _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
                        }
                        var textureHistoryTints = _textureCollectionHistoryTints[collectionId];
                        for (int i = 0; i < textureHistory[categoryKey].Count; i++)
                        {
                            string f = textureHistory[categoryKey][i];
                            System.Numerics.Vector4 t = (textureHistoryTints.ContainsKey(categoryKey) && i < textureHistoryTints[categoryKey].Count)
                                ? textureHistoryTints[categoryKey][i] : System.Numerics.Vector4.One;
                            if (File.Exists(f))
                            {
                                overlayPaths.Add(f);
                                overlayTints.Add(t);
                            }
                            plugin.PluginLog.Info($"[MOUNT REBUILD] Overlay[{i}]: exists={File.Exists(f)} tint={t} path={f}");
                        }

                        if (overlayPaths.Count == 0)
                        {
                            plugin.PluginLog.Info($"[MOUNT REBUILD] No overlay layers for '{categoryKey}'. Disabling mod and reverting to vanilla.");
                            try
                            {
                                string cleanName = gearMeta.DisplayName.Replace("(", "").Replace(")", "").Trim();
                                cleanName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanName.ToLower());
                                string modName = charName + " Texture " + cleanName;
                                string modPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), modName);
                                if (localPlayer != null)
                                {
                                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, modPath, false, modName);
                                    plugin.PluginLog.Info($"[MOUNT REBUILD] Disabled mod '{modName}' in collection {collection}");
                                    // Redraw the player (mount is part of the player object)
                                    Plugin.Framework.RunOnFrameworkThread(() =>
                                    {
                                        plugin.PluginLog.Info($"[MOUNT REBUILD] Redrawing player after mount layer removal: {localPlayer.Name} (ObjectIndex={localPlayer.ObjectIndex})");
                                        PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(localPlayer.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                plugin.PluginLog.Error($"[MOUNT REBUILD] Failed to disable mod: {ex.Message}");
                            }
                            _isRegenerationPending = false;
                            return;
                        }

                        // Composite: draw base, then each overlay on top with alpha blending + tint
                        using (var baseBmp = new Bitmap(item.Base))
                        {
                            int w = baseBmp.Width;
                            int h = baseBmp.Height;
                            using (var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                            {
                                using (var g = Graphics.FromImage(canvas))
                                {
                                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                    g.DrawImage(baseBmp, 0, 0, w, h);
                                    for (int idx = 0; idx < overlayPaths.Count; idx++)
                                    {
                                        using (var overlay = new Bitmap(overlayPaths[idx]))
                                        {
                                            var tint = overlayTints[idx];
                                            if (tint == System.Numerics.Vector4.One)
                                            {
                                                g.DrawImage(overlay, 0, 0, w, h);
                                            }
                                            else
                                            {
                                                var cm = new System.Drawing.Imaging.ColorMatrix(new float[][] {
                                                    new float[] { tint.X, 0, 0, 0, 0 },
                                                    new float[] { 0, tint.Y, 0, 0, 0 },
                                                    new float[] { 0, 0, tint.Z, 0, 0 },
                                                    new float[] { 0, 0, 0, tint.W, 0 },
                                                    new float[] { 0, 0, 0, 0, 1 }
                                                });
                                                using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                                                {
                                                    attrs.SetColorMatrix(cm);
                                                    g.DrawImage(overlay,
                                                        new System.Drawing.Rectangle(0, 0, w, h),
                                                        0, 0, overlay.Width, overlay.Height,
                                                        GraphicsUnit.Pixel, attrs);
                                                }
                                            }
                                        }
                                    }
                                }
                                string tempDir = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", "vanilla_cache");
                                Directory.CreateDirectory(tempDir);
                                string compositedPath = Path.Combine(tempDir, $"mount_composited_{categoryKey.GetHashCode():X8}.png");
                                canvas.Save(compositedPath, System.Drawing.Imaging.ImageFormat.Png);
                                item.Base = compositedPath;
                                plugin.PluginLog.Info($"[MOUNT REBUILD] Pre-composited {overlayPaths.Count} overlays → {compositedPath} ({w}x{h})");
                            }
                        }
                    }

                    // Build the mod name: "CharName Texture Mount Company Chocobo"
                    string cleanMountName = gearMeta.DisplayName;
                    cleanMountName = cleanMountName.Replace("(", "").Replace(")", "").Trim();
                    cleanMountName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanMountName.ToLower());
                    string localModName = charName + " Texture " + cleanMountName;

                    var textureSets = new List<TextureSet> { item };
                    string fullModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), localModName);

                    plugin.PluginLog.Info($"[MOUNT REBUILD] Exporting. ModName={localModName}, Path={fullModPath}, Layers={textureHistory[categoryKey].Count}");

                    // Clean stale tex files from previous exports to avoid dimension mismatches
                    string texDir = Path.Combine(fullModPath, "do_not_edit", "textures");
                    if (Directory.Exists(texDir))
                    {
                        foreach (var staleFile in Directory.GetFiles(texDir, "*.tex"))
                        {
                            try { File.Delete(staleFile); } catch { }
                        }
                    }

                    // Export — pass character info for collection binding, but the TextureSet uses mount paths
                    var localCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                    await Export(true, textureSets, fullModPath, localModName,
                        new KeyValuePair<string, ICharacter>(character.Name.TextValue, character),
                        localCustomization.Customize.Race.Value - 1,
                        localCustomization.Customize.Clan.Value - 1,
                        localCustomization.Customize.Gender.Value,
                        localCustomization.Customize.Face.Value - 1,
                        hideProgressUI);

                    plugin.PluginLog.Info($"[MOUNT REBUILD] Export completed for '{localModName}'. Redrawing player to apply mount texture.");

                    // Redraw the player so the mount texture updates in-game
                    Plugin.Framework.RunOnFrameworkThread(() =>
                    {
                        var lp = plugin.SafeGameObjectManager.LocalPlayer;
                        if (lp != null)
                        {
                            PenumbraAndGlamourerIpcWrapper.Instance.RedrawObject.Invoke(lp.ObjectIndex, Penumbra.Api.Enums.RedrawType.Redraw);
                        }
                    });
                }
                catch (Exception e)
                {
                    _isRegenerationPending = false;
                    plugin.PluginLog.Error($"[MOUNT REBUILD] Crash: {e.Message}");
                    Plugin.PluginLog.Warning(e, e.Message);
                }
            });
        }

        public void RebuildCategory(string categoryKey, bool hideProgressUI = true)
        {
            // Route minion keys to the dedicated minion pipeline — completely separate from body/face/gear
            if (categoryKey.Contains("_minion_"))
            {
                RebuildMinionCategory(categoryKey, hideProgressUI);
                return;
            }

            // Route mount keys to the dedicated mount pipeline
            if (categoryKey.Contains("_mount_"))
            {
                RebuildMountCategory(categoryKey, hideProgressUI);
                return;
            }

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
                _isRegenerationPending = false;
                plugin.PluginLog.Error("[Drag And Drop Texturing] Character " + charName + " not found nearby. Cannot re-export.");
                return;
            }

            var localCustomization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
            string raceCode = PenumbraAndGlamourerHelperFunctions.ModelRaceToRaceCode(localCustomization.Customize.Race.Value - 1, localCustomization.Customize.Clan.Value - 1, localCustomization.Customize.Gender.Value);
            string subRaceName = PenumbraAndGlamourerHelperFunctions.SubRaceToSubRaceName(localCustomization.Customize.Race.Value - 1, localCustomization.Customize.Clan.Value - 1);
            bool holdingModifier = Plugin.Configuration.AutoUniversalConvert;

            var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex);
            var collectionId = collection.EffectiveCollection.Id.ToString();
            if (!_textureCollectionHistory.ContainsKey(collectionId))
            {
                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
            }
            var textureHistory = _textureCollectionHistory[collectionId];
            if (!textureHistory.ContainsKey(categoryKey)) return;

            Task.Run(async () =>
            {
                int waitAttempts = 0;
                while (_lockDuplicateGeneration && waitAttempts < 60)
                {
                    Thread.Sleep(1000);
                    waitAttempts++;
                }
                if (_lockDuplicateGeneration)
                {
                    _isRegenerationPending = false;
                    return;
                }

                try
                {
                    Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).EffectiveCollection.Id;
                    PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(collection, localCustomization.Customize.Gender.Value, localCustomization.Customize.Clan.Value - 1, plugin);
                    List<TextureSet> textureSets = new List<TextureSet>();
                    string localModName = charName + " Texture Mod";

                    TextureSet item = null;
                    string categoryModName = "";
                    string overrideType = "";
                    var collectionId = collection.ToString();
                    if (!_textureCollectionHistory.ContainsKey(collectionId))
                    {
                        _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
                    }
                    var textureHistory = _textureCollectionHistory[collectionId];
                    string lastFile = textureHistory[categoryKey].FirstOrDefault();
                    if (string.IsNullOrEmpty(lastFile))
                    {
                        lastFile = "empty.png";
                    }

                    if (categoryKey.EndsWith("_body") && !categoryKey.Contains("_minion_"))
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
                    else if (categoryKey.Contains("_minion_"))
                    {
                        if (!_gearCategoryMeta.TryGetValue(categoryKey, out var gearMeta))
                        {
                            var minionPieces = DragAndDropTexturing.Equipment.WornEquipmentResolver.ResolveMinion(character.DataId, collection, plugin);
                            if (minionPieces.Count > 0)
                            {
                                gearMeta = minionPieces[0]; // Minions generally have one primary body piece
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
                            // Extract a clean minion name for the mod folder, e.g. "Minion (fat cat)" -> "Minion Fat Cat"
                            string cleanMinionName = gearMeta.DisplayName;
                            cleanMinionName = cleanMinionName.Replace("(", "").Replace(")", "").Trim();
                            // Title-case each word
                            cleanMinionName = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanMinionName.ToLower());
                            categoryModName = cleanMinionName;
                        }
                    }
                    else if (categoryKey.EndsWith("_tail") || categoryKey.Contains("fallback_Tail"))
                    {
                        int effectiveRace = RaceInfo.SubRaceToMainRace(localCustomization.Customize.Clan.Value - 1);
                        item = ProjectHelper.CreateBodyTextureSet(localCustomization.Customize.Gender.Value, 4,
                        effectiveRace,
                        localCustomization.Customize.TailShape.Value - 1, false);
                        TryOverrideTailTextureSet(item, collection, localCustomization.Customize.Gender.Value, effectiveRace, localCustomization.Customize.TailShape.Value - 1);
                        categoryModName = "Tail";
                    }

                    if (item != null)
                    {
                        if (!categoryKey.Contains("_minion_"))
                            ApplyDefaultSkinType(item);
                        if (!_textureCollectionHistoryTints.ContainsKey(collectionId))
                        {
                            _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
                        }
                        var textureHistoryTints = _textureCollectionHistoryTints[collectionId];
                        for (int _i = 0; _i < textureHistory[categoryKey].Count; _i++)
                        {
                            string f = textureHistory[categoryKey][_i];
                            System.Numerics.Vector4? t = textureHistoryTints.ContainsKey(categoryKey) && _i < textureHistoryTints[categoryKey].Count ? textureHistoryTints[categoryKey][_i] : null;
                            AddToTextureSet(item, f, overrideType, t);
                        }

                        bool hasContextualLayers = false;
                        bool isMinion = categoryKey.Contains("_minion_");
                        if (!isMinion && plugin.ContextualLayerManager != null && charName == plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue)
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

                            // Advanced Overlays (Proteus/Metadata from Penumbra mods)
                            if (ApplyAdvancedOverlays(item, categoryKey, collectionId))
                            {
                                hasContextualLayers = true;
                            }
                        }
                        if (textureHistory[categoryKey].Count == 0 && !hasContextualLayers)
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
                            else if (categoryKey.EndsWith("_eyes") || categoryKey.EndsWith("_eyebrows"))
                            {
                                // No layers assigned — skip export entirely so the vanilla eye/eyebrow
                                // textures aren't replaced with a transparent image.
                                localModName = localModName.Replace("Mod", categoryModName);
                                string disableModPath = Path.Combine(PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke(), localModName);

                                try
                                {
                                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, localModName, false, localModName);
                                }
                                catch { }

                                if (Directory.Exists(disableModPath))
                                {
                                    try { Directory.Delete(disableModPath, true); } catch { }
                                }

                                try { PenumbraAndGlamourerIpcWrapper.Instance.DeleteMod.Invoke(localModName, localModName); }
                                catch { try { PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(localModName, ""); } catch { } }

                                plugin.PluginLog.Info($"[Drag And Drop Texturing] No layers for {categoryModName} — skipped export to preserve vanilla textures.");
                                _isRegenerationPending = false;
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
                        plugin.PluginLog.Info($"[MINION EXPORT] About to Export. localModName={localModName}, fullModPath={fullModPath}, textureSets.Count={textureSets.Count}, historyCount={textureHistory[categoryKey].Count}");
                        await Export(true, textureSets, fullModPath, localModName, new KeyValuePair<string, ICharacter>(character.Name.TextValue, character),
                            localCustomization.Customize.Race.Value - 1, localCustomization.Customize.Clan.Value - 1, localCustomization.Customize.Gender.Value, localCustomization.Customize.Face.Value - 1, hideProgressUI);
                        plugin.PluginLog.Info($"[MINION EXPORT] Export completed for {localModName}");
                    }
                }
                catch (Exception e)
                {
                    _isRegenerationPending = false;
                    plugin.PluginLog.Error($"[Drag And Drop Texturing] Crash during generation: {e.Message}");
                    Plugin.PluginLog.Warning(e, e.Message);
                }
            });
        }

        public void RefreshWornGearCache(ICharacter targetCharacter = null)
        {
            targetCharacter ??= plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
            if (targetCharacter == null)
            {
                CachedWornGear = new List<WornEquipmentPiece>();
                return;
            }

            CachedWornGear = WornEquipmentResolver.ResolveWornGear(targetCharacter, plugin);
        }

        public void ImportWornGearSlot(WornEquipmentPiece piece, ICharacter targetCharacter = null)
        {
            if (piece == null || plugin == null) return;

            targetCharacter ??= plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
            if (targetCharacter == null)
            {
                plugin.Chat.PrintError("[Drag And Drop Texturing] No target character — cannot import worn gear.");
                return;
            }

            Guid collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(targetCharacter.ObjectIndex).EffectiveCollection.Id;
            string exportDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "WornGear");
            string source = !string.IsNullOrEmpty(piece.ResolvedBaseDiskPath) ? piece.ResolvedBaseDiskPath : piece.InternalBasePath;
            string pngPath = WornEquipmentResolver.ExportResolvedTextureToPng(source, collection, exportDir, plugin, piece.SlotKey, piece.MaterialName);

            if (string.IsNullOrEmpty(pngPath))
            {
                plugin.Chat.PrintError($"[Drag And Drop Texturing] Could not read texture for {piece.DisplayName}.");
                return;
            }

            string categoryKey = $"{targetCharacter.Name.TextValue}_gear_{piece.SlotKey}" +
                (string.IsNullOrEmpty(piece.MaterialName) ? "" : "_" + piece.MaterialName) +
                (string.IsNullOrEmpty(piece.ModName) ? "" : "_[" + piece.ModName + "]");
            _gearCategoryMeta[categoryKey] = piece;
            var collectionId = collection.ToString();
            if (!_textureCollectionHistory.ContainsKey(collectionId))
            {
                _textureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
            }
            if (!_textureCollectionHistoryTints.ContainsKey(collectionId))
            {
                _textureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
            }
            var textureHistory = _textureCollectionHistory[collectionId];
            var textureHistoryTints = _textureCollectionHistoryTints[collectionId];
            if (!textureHistory.ContainsKey(categoryKey))
            {
                textureHistory[categoryKey] = new List<string>();
                textureHistoryTints[categoryKey] = new List<Vector4>();
            }

            if (!plugin.Configuration.EnableTextureStacking)
            {
                textureHistory[categoryKey].Clear();
                textureHistoryTints[categoryKey].Clear();
            }

            if (!textureHistory[categoryKey].Contains(pngPath))
            {
                textureHistory[categoryKey].Add(pngPath);
                textureHistoryTints[categoryKey].Add(System.Numerics.Vector4.One);
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
        private string ExtractVanillaTexViaLumina(string internalGamePath, TextureSet textureSet, bool forceOpaqueAlpha = false)
        {
            try
            {
                var texFile = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(internalGamePath);
                if (texFile == null) return "";

                // Use the existing TexIO pattern to convert the raw .tex data to Bitmap
                using (var stream = new MemoryStream(texFile.Data))
                {
                    var rawBitmap = TexIO.TexToBitmap(stream);
                    if (rawBitmap == null) return "";

                    using (rawBitmap)
                    {
                        // FFXIV stores specular/shininess in the diffuse alpha channel.
                        // Force alpha=255 so the GPU compositor treats the base as fully opaque.
                        if (forceOpaqueAlpha)
                        {
                            var rect = new System.Drawing.Rectangle(0, 0, rawBitmap.Width, rawBitmap.Height);
                            var bmpData = rawBitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                            unsafe
                            {
                                byte* ptr = (byte*)bmpData.Scan0;
                                int bytes = Math.Abs(bmpData.Stride) * rawBitmap.Height;
                                for (int i = 3; i < bytes; i += 4)
                                {
                                    ptr[i] = 255; // Set alpha to fully opaque
                                }
                            }
                            rawBitmap.UnlockBits(bmpData);
                        }

                        // Save as temp PNG using standard ImageFormat.Png (same as painter's working version)
                        string tempDir = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", "vanilla_cache");
                        Directory.CreateDirectory(tempDir);
                        string safeName = internalGamePath.Replace("/", "_").Replace("\\", "_");
                        string suffix = forceOpaqueAlpha ? "_opaque" : "";
                        string tempPath = Path.Combine(tempDir, safeName + $"_{rawBitmap.Width}x{rawBitmap.Height}{suffix}.png");
                        rawBitmap.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
                        return tempPath;
                    }
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

            // Hair slot
            if (name.Contains("hair") || name.Contains("kami"))
                return "hair";

            // Head slot: head, face, eyes, helmet, ears, neck, teeth etc.
            if (name.Contains("head") || name.Contains("kao") || name.Contains("mimi") || name.Contains("ear"))
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

            // Tail slot: sippo, tail
            if (name.Contains("sippo") || name.Contains("tail"))
                return "tail";

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
        Tail,
        Hair,
    }
}
