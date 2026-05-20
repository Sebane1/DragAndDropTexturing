using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DragAndDropTexturing.Windows
{
    public unsafe class ModelRenderer : IDisposable
    {
        private ID3D11Device _device;
        private ID3D11DeviceContext _context;
        private ID3D11Texture2D _renderTargetTexture;
        private ID3D11RenderTargetView _renderTargetView;
        private ID3D11ShaderResourceView _shaderResourceView;
        private ID3D11Texture2D _depthStencilTexture;
        private ID3D11DepthStencilView _depthStencilView;
        private ID3D11DepthStencilState _depthStencilState;
        private ID3D11RasterizerState _rasterizerState;

        // GPU Paint system
        private ID3D11Texture2D _gpuPaintTex;
        private ID3D11UnorderedAccessView _gpuPaintUAV;
        private ID3D11ShaderResourceView _gpuPaintSRV;

        private ID3D11Texture2D _gpuBaseTex;
        private ID3D11ShaderResourceView _gpuBaseSRV;
        private int _baseTexWidth = 0;
        private int _baseTexHeight = 0;

        private ID3D11Texture2D _gpuCompositeTex;
        private ID3D11UnorderedAccessView _gpuCompositeUAV;
        private ID3D11ShaderResourceView _gpuCompositeSRV;

        private ID3D11ComputeShader _paintBrushCS;
        private ID3D11ComputeShader _compositeCS;
        private ID3D11ComputeShader _stampCS;
        private ID3D11Buffer _brushCB;
        private ID3D11Buffer _stampCB;
        private ID3D11SamplerState _stampSampler;
        private int _paintTexWidth, _paintTexHeight;
        private bool _gpuPaintReady;
        private bool _disposed;

        // Undo / Redo system
        private List<ID3D11Texture2D> _undoStack = new List<ID3D11Texture2D>();
        private List<ID3D11Texture2D> _redoStack = new List<ID3D11Texture2D>();
        private const int MaxUndoSteps = 10;

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct BrushParams
        {
            public Vector2 Center;       // 8 bytes
            public Vector2 PrevCenter;   // 8 bytes
            public float Radius;         // 4
            public float Hardness;       // 4
            public int HasPrev;          // 4
            public int BlendMode;        // 4  (0=Normal, 1=Eraser, 2=Multiply, 3=Screen, 4=Overlay, 5=SoftLight)
            public int ShapeMode;        // 4
            public float Flow;           // 4  (per-dab alpha multiplier, 0-1)
            public float Angle;          // 4  (rotation in radians)
            public float NoiseScale;     // 4  (grain frequency, 0 = off)
            public float NoiseAmount;    // 4  (how much noise modulates alpha, 0-1)  offset 48
            public float Seed;           // 4  (random seed for noise)                 offset 52
            public float Padding_A;      // 4  alignment padding                       offset 56
            public float Padding_B;      // 4  alignment padding                       offset 60
            public Vector4 Color;        // 16                                         offset 64
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StampParams
        {
            public Vector2 Position;
            public Vector2 Scale;
            public Vector3 DecalCenter;
            public float DecalDepth;
            public Vector3 DecalNormal;
            public float DecalRadius;
            public Vector3 DecalTangent;
            public int ProjectionMode;
            public Vector3 DecalBitangent;
            public float Padding1;
            public Matrix4x4 ViewProj;
            public Vector3 CameraEye;
            public float AspectRatio;
        }

        public IntPtr ShaderResourceViewHandle => _shaderResourceView?.NativePointer ?? IntPtr.Zero;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public ModelRenderer(int width, int height)
        {
            Width = width;
            Height = height;
            
            // Attempt to hook into FFXIV's native D3D11 device
            var ffxivDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
            if (ffxivDevice != null && ffxivDevice->D3D11DeviceContext != null)
            {
                _context = new ID3D11DeviceContext((IntPtr)ffxivDevice->D3D11DeviceContext);
                _device = _context.Device;
            }
            else
            {
                throw new Exception("Failed to locate FFXIV D3D11 Device Context.");
            }

            Resize(Width, Height);
        }

        public void Resize(int width, int height)
        {
            if (_disposed || width <= 0 || height <= 0 || _device == null) return;
            
            Width = width;
            Height = height;

            _renderTargetView?.Dispose();
            _shaderResourceView?.Dispose();
            _renderTargetTexture?.Dispose();

            var textureDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            _renderTargetTexture = _device.CreateTexture2D(textureDesc);
            _renderTargetView = _device.CreateRenderTargetView(_renderTargetTexture);
            _shaderResourceView = _device.CreateShaderResourceView(_renderTargetTexture);

            // Create depth-stencil buffer
            _depthStencilTexture?.Dispose();
            _depthStencilView?.Dispose();
            _depthStencilState?.Dispose();

            var depthDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D24_UNorm_S8_UInt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CPUAccessFlags = CpuAccessFlags.None
            };
            _depthStencilTexture = _device.CreateTexture2D(depthDesc);
            _depthStencilView = _device.CreateDepthStencilView(_depthStencilTexture);

            var dsStateDesc = new DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.Less
            };
            _depthStencilState = _device.CreateDepthStencilState(dsStateDesc);

            _rasterizerState?.Dispose();
            var rsDesc = new RasterizerDescription
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                FrontCounterClockwise = true,
                DepthBias = 0,
                DepthBiasClamp = 0.0f,
                SlopeScaledDepthBias = 0.0f,
                DepthClipEnable = true,
                ScissorEnable = false,
                MultisampleEnable = false,
                AntialiasedLineEnable = false
            };
            _rasterizerState = _device.CreateRasterizerState(rsDesc);
        }

        private ID3D11VertexShader _vertexShader;
        private ID3D11PixelShader _pixelShader;
        private ID3D11InputLayout _inputLayout;
        private ID3D11Buffer _constantBuffer;
        private ID3D11Buffer _psConstantBuffer;

        public class RenderModel : IDisposable
        {
            public Vertex[] Vertices { get; set; }
            public ushort[] Indices { get; set; }
            public ID3D11Buffer VertexBuffer { get; set; }
            public ID3D11Buffer IndexBuffer { get; set; }
            public int IndexCount { get; set; }
            
            public ID3D11Texture2D Texture { get; set; }
            public ID3D11ShaderResourceView TextureSRV { get; set; }
            public ID3D11SamplerState SamplerState { get; set; }
            public bool HasTexture { get; set; } = false;

            public Vector3 BoundsMin { get; set; } = new Vector3(float.MaxValue);
            public Vector3 BoundsMax { get; set; } = new Vector3(float.MinValue);

            public void Dispose()
            {
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
                Texture?.Dispose();
                TextureSRV?.Dispose();
                SamplerState?.Dispose();
            }
        }

        private Dictionary<string, RenderModel> _models = new Dictionary<string, RenderModel>();
        public HashSet<string> HiddenSlots { get; } = new HashSet<string>();

        public struct Vertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 UV;
        }

        private struct Constants
        {
            public Matrix4x4 WorldViewProj;
        }

        private const string ShaderCode = @"
cbuffer Constants : register(b0)
{
    matrix WorldViewProj;
};
cbuffer PixelConstants : register(b1)
{
    float4 Flags; // x = hasTexture (1.0 or 0.0)
};
Texture2D diffuseMap : register(t0);
SamplerState diffuseSampler : register(s0);
struct VS_IN
{
    float3 pos : POSITION;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD;
};
struct PS_IN
{
    float4 pos : SV_POSITION;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD;
};
PS_IN VS(VS_IN input)
{
    PS_IN output = (PS_IN)0;
    output.pos = mul(float4(input.pos, 1.0f), WorldViewProj);
    output.norm = input.norm;
    output.uv = input.uv;
    return output;
}
float4 PS(PS_IN input) : SV_TARGET
{
    float3 normalColor = input.norm * 0.5f + 0.5f;
    if (Flags.x > 0.5f)
    {
        float4 texColor = diffuseMap.Sample(diffuseSampler, input.uv);
        return float4(texColor.rgb, 1.0f);
    }
    return float4(normalColor, 1.0f);
}";

        private void RecalculateBounds()
        {
            _boundsMin = new Vector3(float.MaxValue);
            _boundsMax = new Vector3(float.MinValue);
            foreach (var model in _models.Values)
            {
                _boundsMin = Vector3.Min(_boundsMin, model.BoundsMin);
                _boundsMax = Vector3.Max(_boundsMax, model.BoundsMax);
            }
            if (_boundsMin.X > _boundsMax.X) // No models
            {
                _boundsMin = Vector3.Zero;
                _boundsMax = Vector3.Zero;
            }
            _boundsCenter = (_boundsMin + _boundsMax) * 0.5f;
            _boundsRadius = Vector3.Distance(_boundsMin, _boundsMax) * 0.5f;
            if (_boundsRadius < 0.001f) _boundsRadius = 1.0f;
            
            _meshLoaded = _models.Count > 0;
        }

        
        private ID3D11Texture2D _positionMapTex;
        public ID3D11ShaderResourceView PositionMapSRV { get; private set; }
        private ID3D11Texture2D _normalMapTex;
        public ID3D11ShaderResourceView NormalMapSRV { get; private set; }

        public void BakeUVMaps() {
            if (_device == null || _paintTexWidth <= 0 || _paintTexHeight <= 0) return;
            if (HasBakedUvMaps) return;

            int width = _paintTexWidth;
            int height = _paintTexHeight;
            var posData = new float[width * height * 4];
            var normData = new float[width * height * 4];
            BakeUVMapsCpu(width, height, posData, normData);
            UploadUvMaps(width, height, posData, normData);
            _uvMapsBakedRevision = _meshRevision;
        }

        public void RequestBakeUVMapsAsync(Action onComplete = null) {
            if (HasBakedUvMaps) {
                onComplete?.Invoke();
                return;
            }

            if (onComplete != null) {
                _pendingBakeCallbacks = (Action)Delegate.Combine(_pendingBakeCallbacks, onComplete);
            }

            if (_uvBakeRunning) return;
            if (_device == null || _paintTexWidth <= 0 || _paintTexHeight <= 0) return;

            _uvBakeRunning = true;
            int width = _paintTexWidth;
            int height = _paintTexHeight;
            int bakeRevision = _meshRevision;

            Task.Run(() => {
                var posData = new float[width * height * 4];
                var normData = new float[width * height * 4];
                BakeUVMapsCpu(width, height, posData, normData);
                _pendingPosMapData = posData;
                _pendingNormMapData = normData;
                _pendingBakeRevision = bakeRevision;
            });
        }

        /// <summary>Upload CPU-baked UV maps on the D3D11 thread. Returns true when an upload ran.</summary>
        public bool ProcessUvBakeUpload() {
            if (_pendingPosMapData == null || _pendingNormMapData == null) return false;
            if (_device == null || _paintTexWidth <= 0 || _paintTexHeight <= 0) {
                _pendingPosMapData = null;
                _pendingNormMapData = null;
                _uvBakeRunning = false;
                return false;
            }

            if (_pendingBakeRevision != _meshRevision) {
                _pendingPosMapData = null;
                _pendingNormMapData = null;
                _uvBakeRunning = false;
                return false;
            }

            int width = _paintTexWidth;
            int height = _paintTexHeight;
            UploadUvMaps(width, height, _pendingPosMapData, _pendingNormMapData);
            _pendingPosMapData = null;
            _pendingNormMapData = null;
            _uvMapsBakedRevision = _meshRevision;
            _uvBakeRunning = false;

            var callbacks = _pendingBakeCallbacks;
            _pendingBakeCallbacks = null;
            callbacks?.Invoke();
            return true;
        }

        private void BakeUVMapsCpu(int width, int height, float[] posData, float[] normData)
        {

            foreach (var kvp in _models)
            {
                if (kvp.Key.Contains("_")) continue; // Skip sub-meshes like Top_1, Bottom_1
                var model = kvp.Value;
                if (model.Vertices == null || model.Indices == null) continue;

                for (int i = 0; i < model.Indices.Length; i += 3)
                {
                    var v0 = model.Vertices[model.Indices[i]];
                    var v1 = model.Vertices[model.Indices[i+1]];
                    var v2 = model.Vertices[model.Indices[i+2]];

                    Vector2 p0 = new Vector2(v0.UV.X * width, v0.UV.Y * height);
                    Vector2 p1 = new Vector2(v1.UV.X * width, v1.UV.Y * height);
                    Vector2 p2 = new Vector2(v2.UV.X * width, v2.UV.Y * height);

                    int minX = (int)Math.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X)));
                    int maxX = (int)Math.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X)));
                    int minY = (int)Math.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y)));
                    int maxY = (int)Math.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y)));

                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                            float w0 = EdgeFunction(p1, p2, p);
                            float w1 = EdgeFunction(p2, p0, p);
                            float w2 = EdgeFunction(p0, p1, p);
                            
                            bool inside = (w0 >= -0.001f && w1 >= -0.001f && w2 >= -0.001f) || (w0 <= 0.001f && w1 <= 0.001f && w2 <= 0.001f);
                            if (inside)
                            {
                                float area = Math.Abs(w0) + Math.Abs(w1) + Math.Abs(w2);
                                if (area == 0) continue;
                                w0 = Math.Abs(w0) / area;
                                w1 = Math.Abs(w1) / area;
                                w2 = Math.Abs(w2) / area;

                                Vector3 pos = v0.Position * w0 + v1.Position * w1 + v2.Position * w2;
                                Vector3 norm = v0.Normal * w0 + v1.Normal * w1 + v2.Normal * w2;
                                float nLen = norm.Length();
                                if (nLen > 0.0001f) norm /= nLen;

                                int wrapX = ((x % width) + width) % width;
                                int wrapY = ((y % height) + height) % height;
                                int idx = (wrapY * width + wrapX) * 4;
                                
                                // Basic Z-buffer for UV map (keep front-most faces if overlapping in UV space)
                                // If multiple meshes map to the same UV, we want the one with a valid position.
                                // In FFXIV, we just overwrite for now, but a proper UV z-buffer could be added.
                                posData[idx] = pos.X;
                                posData[idx+1] = pos.Y;
                                posData[idx+2] = pos.Z;
                                posData[idx+3] = 1.0f;

                                normData[idx] = norm.X;
                                normData[idx+1] = norm.Y;
                                normData[idx+2] = norm.Z;
                                normData[idx+3] = 1.0f;
                            }
                        }
                    }
                }
            }
            
            // Dilation pass to fix UV seams/creases (Edge Padding)
            int dilationPasses = 2;
            for (int pass = 0; pass < dilationPasses; pass++)
            {
                float[] newPosData = (float[])posData.Clone();
                float[] newNormData = (float[])normData.Clone();
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * width + x) * 4;
                        if (posData[idx + 3] < 0.5f) // Empty pixel
                        {
                            // Look for valid neighbor
                            int[] offsetsX = { -1, 1, 0, 0, -1, 1, -1, 1 };
                            int[] offsetsY = { 0, 0, -1, 1, -1, -1, 1, 1 };
                            for (int n = 0; n < 8; n++)
                            {
                                int nx = x + offsetsX[n];
                                int ny = y + offsetsY[n];
                                if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                                {
                                    int nIdx = (ny * width + nx) * 4;
                                    if (posData[nIdx + 3] > 0.5f)
                                    {
                                        newPosData[idx] = posData[nIdx];
                                        newPosData[idx + 1] = posData[nIdx + 1];
                                        newPosData[idx + 2] = posData[nIdx + 2];
                                        newPosData[idx + 3] = 1.0f; // Mark as filled

                                        newNormData[idx] = normData[nIdx];
                                        newNormData[idx + 1] = normData[nIdx + 1];
                                        newNormData[idx + 2] = normData[nIdx + 2];
                                        newNormData[idx + 3] = 1.0f;
                                        break; // Found a valid neighbor, stop searching
                                    }
                                }
                            }
                        }
                    }
                }
                posData = newPosData;
                normData = newNormData;
            }
        }

        private unsafe void UploadUvMaps(int width, int height, float[] posData, float[] normData) {
            if (_device == null) return;

            fixed (float* pPos = posData)
            fixed (float* pNorm = normData)
            {
                if (_positionMapTex != null && _normalMapTex != null && 
                    _positionMapTex.Description.Width == width && _positionMapTex.Description.Height == height)
                {
                    _context.UpdateSubresource(_positionMapTex, 0, null, (IntPtr)pPos, width * 16, 0);
                    _context.UpdateSubresource(_normalMapTex, 0, null, (IntPtr)pNorm, width * 16, 0);
                    return;
                }

                _positionMapTex?.Dispose();
                PositionMapSRV?.Dispose();
                _normalMapTex?.Dispose();
                NormalMapSRV?.Dispose();

                var texDesc = new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None
                };
                
                var posSubData = new SubresourceData((IntPtr)pPos, width * 16);
                _positionMapTex = _device.CreateTexture2D(texDesc, new[] { posSubData });
                PositionMapSRV = _device.CreateShaderResourceView(_positionMapTex);
                
                var normSubData = new SubresourceData((IntPtr)pNorm, width * 16);
                _normalMapTex = _device.CreateTexture2D(texDesc, new[] { normSubData });
                NormalMapSRV = _device.CreateShaderResourceView(_normalMapTex);
            }
        }
        
        private float EdgeFunction(Vector2 a, Vector2 b, Vector2 c)
        {
            return (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);
        }

        public void LoadMeshes(string slot, List<ExtractedMesh> meshes)
        {
            if (_device == null || meshes == null || meshes.Count == 0) return;

            var allVertices = new List<Vertex>();
            var allIndices = new List<ushort>();

            var bMin = new Vector3(float.MaxValue);
            var bMax = new Vector3(float.MinValue);

            foreach (var mesh in meshes)
            {
                int baseVertex = allVertices.Count;

                for (int i = 0; i < mesh.Positions.Count; i++)
                {
                    var pos = mesh.Positions[i];
                    allVertices.Add(new Vertex
                    {
                        Position = pos,
                        Normal = mesh.Normals.Count > i ? mesh.Normals[i] : Vector3.UnitY,
                        UV = mesh.UVs.Count > i ? mesh.UVs[i] : Vector2.Zero
                    });

                    bMin = Vector3.Min(bMin, pos);
                    bMax = Vector3.Max(bMax, pos);
                }

                foreach (var idx in mesh.Indices)
                {
                    allIndices.Add((ushort)(baseVertex + idx));
                }
            }

            if (!_models.TryGetValue(slot, out var model))
            {
                model = new RenderModel();
                _models[slot] = model;
            }

            model.BoundsMin = bMin;
            model.BoundsMax = bMax;
            model.IndexCount = allIndices.Count;
            model.Vertices = allVertices.ToArray();
            model.Indices = allIndices.ToArray();

            model.VertexBuffer?.Dispose();
            model.VertexBuffer = _device.CreateBuffer(model.Vertices, BindFlags.VertexBuffer);

            model.IndexBuffer?.Dispose();
            model.IndexBuffer = _device.CreateBuffer(model.Indices, BindFlags.IndexBuffer);

            RecalculateBounds();
            if (_vertexShader == null)
            {
                try
                {
                    var vsBytecode = Vortice.D3DCompiler.Compiler.Compile(ShaderCode, "VS", "", "vs_5_0");
                    _vertexShader = _device.CreateVertexShader(vsBytecode.Span);

                    var inputElements = new[]
                    {
                        new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                        new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                        new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 24, 0)
                    };
                    _inputLayout = _device.CreateInputLayout(inputElements, vsBytecode.Span);

                    var psBytecode = Vortice.D3DCompiler.Compiler.Compile(ShaderCode, "PS", "", "ps_5_0");
                    _pixelShader = _device.CreatePixelShader(psBytecode.Span);

                    // Use Default usage so we can UpdateSubresource instead of Map
                    var cbDesc = new BufferDescription
                    {
                        ByteWidth = 64, // Matrix4x4 is 64 bytes
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ConstantBuffer,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    _constantBuffer = _device.CreateBuffer(cbDesc);

                    // Pixel shader constant buffer for texture flags (16 bytes = float4)
                    var psCbDesc = new BufferDescription
                    {
                        ByteWidth = 16,
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ConstantBuffer,
                        CPUAccessFlags = CpuAccessFlags.None
                    };
                    _psConstantBuffer = _device.CreateBuffer(psCbDesc);
                }
                catch (Exception ex)
                {
                    _renderError = "Shader compile failed: " + ex.Message;
                    return;
                }
            }

            InvalidateMeshDerivedData();
            _meshLoaded = true;
        }

        private bool _meshLoaded = false;
        private string _renderError = null;
        public string RenderError => _renderError;

        // Bounding box for auto-centering and camera fit
        private Vector3 _boundsMin = Vector3.Zero;
        private Vector3 _boundsMax = Vector3.Zero;
        private Vector3 _boundsCenter = Vector3.Zero;
        private Vector3 _lastEye = Vector3.Zero;
        private float _boundsRadius = 1.0f;

        // Interactive orbital camera state
        public float CameraYaw = 0f;
        public float CameraPitch = 0.3f;
        private float _cameraDistance = 1f; // Multiplier of boundsRadius * 2.5
        private Vector3 _cameraPan = Vector3.Zero;

        /// <summary>Toggle between perspective and orthographic projection.</summary>
        public bool UseOrthographic { get; set; } = false;

        /// <summary>Rotate the camera orbit by yaw/pitch delta (radians).</summary>
        public void RotateCamera(float deltaYaw, float deltaPitch)
        {
            CameraYaw += deltaYaw;
            CameraPitch += deltaPitch;
            // Clamp pitch to avoid flipping
            CameraPitch = Math.Clamp(CameraPitch, -1.5f, 1.5f);
        }

        /// <summary>Zoom camera in/out by a multiplicative factor.</summary>
        public void ZoomCamera(float delta)
        {
            _cameraDistance *= (1f - delta * 0.1f);
            _cameraDistance = Math.Clamp(_cameraDistance, 0.05f, 20f);
        }

        /// <summary>Pan the camera target by screen-space delta.</summary>
        public void PanCamera(float deltaX, float deltaY)
        {
            // Pan in camera-local space, scaled to model size
            float panScale = _boundsRadius * _cameraDistance * 0.002f;
            // Compute right and up vectors from current yaw/pitch
            float cosYaw = MathF.Cos(CameraYaw);
            float sinYaw = MathF.Sin(CameraYaw);
            float cosPitch = MathF.Cos(CameraPitch);
            float sinPitch = MathF.Sin(CameraPitch);

            var right = new Vector3(cosYaw, 0, -sinYaw);
            var up = new Vector3(sinYaw * sinPitch, cosPitch, cosYaw * sinPitch);

            _cameraPan += right * (-deltaX * panScale) + up * (deltaY * panScale);
        }

        /// <summary>Reset camera to default view.</summary>
        public void ResetCamera()
        {
            CameraYaw = 0f;
            CameraPitch = 0.3f;
            _cameraDistance = 1f;
            _cameraPan = Vector3.Zero;
        }

        private Matrix4x4 _lastWvp;

        private int _meshRevision;
        private int _uvMapsBakedRevision = -1;
        private volatile bool _uvBakeRunning;
        private float[] _pendingPosMapData;
        private float[] _pendingNormMapData;
        private int _pendingBakeRevision = -1;
        private Action _pendingBakeCallbacks;

        public bool HasBakedUvMaps => _uvMapsBakedRevision == _meshRevision && PositionMapSRV != null;
        public bool IsUvBakeInProgress => _uvBakeRunning || _pendingPosMapData != null;

        private struct RaycastTriangle {
            public Vector3 V0, V1, V2;
            public Vector2 Uv0, Uv1, Uv2;
            public Vector3 N0, N1, N2;
        }

        private sealed class RaycastSlotCache {
            public Vector3 BoundsMin;
            public Vector3 BoundsMax;
            public RaycastTriangle[] Triangles;
        }

        private readonly Dictionary<string, RaycastSlotCache> _raycastCache = new();
        private int _raycastCacheRevision = -1;

        private void InvalidateMeshDerivedData() {
            _meshRevision++;
            _uvMapsBakedRevision = -1;
            _raycastCacheRevision = -1;
            _raycastCache.Clear();
        }

        private void EnsureRaycastCache() {
            if (_raycastCacheRevision == _meshRevision) return;

            _raycastCache.Clear();
            foreach (var kvp in _models) {
                if (kvp.Key.Contains("_")) continue;

                var model = kvp.Value;
                if (model.Vertices == null || model.Indices == null || model.Indices.Length < 3) continue;

                var tris = new List<RaycastTriangle>(model.Indices.Length / 3);
                var boundsMin = new Vector3(float.MaxValue);
                var boundsMax = new Vector3(float.MinValue);

                for (int i = 0; i < model.Indices.Length; i += 3) {
                    var v0 = model.Vertices[model.Indices[i]];
                    var v1 = model.Vertices[model.Indices[i + 1]];
                    var v2 = model.Vertices[model.Indices[i + 2]];
                    tris.Add(new RaycastTriangle {
                        V0 = v0.Position, V1 = v1.Position, V2 = v2.Position,
                        Uv0 = v0.UV, Uv1 = v1.UV, Uv2 = v2.UV,
                        N0 = v0.Normal, N1 = v1.Normal, N2 = v2.Normal
                    });
                    boundsMin = Vector3.Min(boundsMin, Vector3.Min(v0.Position, Vector3.Min(v1.Position, v2.Position)));
                    boundsMax = Vector3.Max(boundsMax, Vector3.Max(v0.Position, Vector3.Max(v1.Position, v2.Position)));
                }

                _raycastCache[kvp.Key] = new RaycastSlotCache {
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax,
                    Triangles = tris.ToArray()
                };
            }

            _raycastCacheRevision = _meshRevision;
        }

        private static bool RayIntersectsAabb(Vector3 origin, Vector3 dir, Vector3 min, Vector3 max, out float tEnter, out float tExit) {
            tEnter = 0f;
            tExit = float.MaxValue;

            if (Math.Abs(dir.X) < 1e-8f) {
                if (origin.X < min.X || origin.X > max.X) return false;
            } else {
                float tx1 = (min.X - origin.X) / dir.X;
                float tx2 = (max.X - origin.X) / dir.X;
                tEnter = Math.Max(tEnter, Math.Min(tx1, tx2));
                tExit = Math.Min(tExit, Math.Max(tx1, tx2));
            }

            if (Math.Abs(dir.Y) < 1e-8f) {
                if (origin.Y < min.Y || origin.Y > max.Y) return false;
            } else {
                float ty1 = (min.Y - origin.Y) / dir.Y;
                float ty2 = (max.Y - origin.Y) / dir.Y;
                tEnter = Math.Max(tEnter, Math.Min(ty1, ty2));
                tExit = Math.Min(tExit, Math.Max(ty1, ty2));
            }

            if (Math.Abs(dir.Z) < 1e-8f) {
                if (origin.Z < min.Z || origin.Z > max.Z) return false;
            } else {
                float tz1 = (min.Z - origin.Z) / dir.Z;
                float tz2 = (max.Z - origin.Z) / dir.Z;
                tEnter = Math.Max(tEnter, Math.Min(tz1, tz2));
                tExit = Math.Min(tExit, Math.Max(tz1, tz2));
            }

            return tExit >= Math.Max(tEnter, 0f);
        }

        public bool Raycast(Vector2 screenPos, out Vector2 uvHit, out string hitSlot, out Vector3 worldPos, out Vector3 worldNormal, HashSet<string> allowedSlots = null)
        {
            uvHit = Vector2.Zero;
            hitSlot = null;
            worldPos = Vector3.Zero;
            worldNormal = Vector3.Zero;
            if (_models.Count == 0 || Width == 0 || Height == 0) return false;

            EnsureRaycastCache();

            float ndcX = (2.0f * screenPos.X) / Width - 1.0f;
            float ndcY = 1.0f - (2.0f * screenPos.Y) / Height;

            if (!Matrix4x4.Invert(_lastWvp, out Matrix4x4 invWvp)) return false;

            Vector4 nearPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 0.0f, 1.0f), invWvp);
            Vector4 farPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 1.0f, 1.0f), invWvp);

            nearPoint /= nearPoint.W;
            farPoint /= farPoint.W;

            Vector3 rayOrigin = new Vector3(nearPoint.X, nearPoint.Y, nearPoint.Z);
            Vector3 rayDir = Vector3.Normalize(new Vector3(farPoint.X, farPoint.Y, farPoint.Z) - rayOrigin);

            float closestT = float.MaxValue;
            bool hit = false;

            foreach (var kvp in _raycastCache) {
                if (allowedSlots != null && !allowedSlots.Contains(kvp.Key)) continue;
                if (allowedSlots == null && kvp.Key.Contains("_")) continue;

                var cache = kvp.Value;
                if (!RayIntersectsAabb(rayOrigin, rayDir, cache.BoundsMin, cache.BoundsMax, out float tEnter, out float tExit)) continue;
                if (tEnter > closestT) continue;

                foreach (var tri in cache.Triangles) {
                    if (RayIntersectsTriangle(rayOrigin, rayDir, tri.V0, tri.V1, tri.V2, out float t, out float u, out float v)) {
                        if (t < closestT && t > 0) {
                            closestT = t;
                            hitSlot = kvp.Key;
                            hit = true;

                            float w = 1.0f - u - v;
                            Vector2 rawUv = tri.Uv0 * w + tri.Uv1 * u + tri.Uv2 * v;
                            uvHit = new Vector2((rawUv.X % 1.0f + 1.0f) % 1.0f, (rawUv.Y % 1.0f + 1.0f) % 1.0f);
                            worldPos = rayOrigin + rayDir * t;

                            Vector3 interpolatedNormal = tri.N0 * w + tri.N1 * u + tri.N2 * v;
                            float nLen = interpolatedNormal.Length();
                            worldNormal = nLen > 0.0001f ? interpolatedNormal / nLen : Vector3.UnitZ;
                        }
                    }
                }
            }

            return hit;
        }

        private bool RayIntersectsTriangle(Vector3 rayOrigin, Vector3 rayDir, Vector3 v0, Vector3 v1, Vector3 v2, out float t, out float u, out float v)
        {
            t = 0; u = 0; v = 0;
            const float EPSILON = 0.0000001f;
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 h = Vector3.Cross(rayDir, edge2);
            float a = Vector3.Dot(edge1, h);

            if (a > -EPSILON && a < EPSILON)
                return false;

            float f = 1.0f / a;
            Vector3 s = rayOrigin - v0;
            u = f * Vector3.Dot(s, h);
            if (u < 0.0f || u > 1.0f)
                return false;

            Vector3 q = Vector3.Cross(s, edge1);
            v = f * Vector3.Dot(rayDir, q);
            if (v < 0.0f || u + v > 1.0f)
                return false;

            t = f * Vector3.Dot(edge2, q);
            return t > EPSILON;
        }

        public void Render()
        {
            if (_disposed || _context == null || _renderTargetView == null) return;

            try
            {
                // Bind our RTV + depth buffer
                _context.OMSetRenderTargets(_renderTargetView, _depthStencilView);
                _context.OMSetDepthStencilState(_depthStencilState);
                
                // Clear to a dark gray background + depth
                _context.ClearRenderTargetView(_renderTargetView, new Vortice.Mathematics.Color4(0.15f, 0.15f, 0.15f, 1.0f));
                _context.ClearDepthStencilView(_depthStencilView, DepthStencilClearFlags.Depth, 1.0f, 0);

                // Set Viewport and Rasterizer
                _context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, Width, Height));
                _context.RSSetState(_rasterizerState);

                if (_meshLoaded)
                {
                    // Orbital camera: compute eye position from spherical coordinates
                    float camDist = _boundsRadius * 2.5f * _cameraDistance;
                    float farPlane = Math.Max(camDist + _boundsRadius * 10f, 100f);
                    float nearPlane = Math.Max(_boundsRadius * 0.01f, 0.01f);

                    float cosPitch = MathF.Cos(CameraPitch);
                    float sinPitch = MathF.Sin(CameraPitch);
                    float cosYaw = MathF.Cos(CameraYaw);
                    float sinYaw = MathF.Sin(CameraYaw);

                    var eyeOffset = new Vector3(
                        sinYaw * cosPitch,
                        sinPitch,
                        cosYaw * cosPitch
                    ) * camDist;

                    var target = _cameraPan;
                    var eye = target + eyeOffset;
                    _lastEye = eye + _boundsCenter;

                    var centerOffset = Matrix4x4.CreateTranslation(-_boundsCenter);
                    var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
                    Matrix4x4 proj;
                    if (UseOrthographic)
                    {
                        float orthoHeight = _boundsRadius * 2f * _cameraDistance;
                        float orthoWidth = orthoHeight * (float)Width / Height;
                        proj = Matrix4x4.CreateOrthographic(orthoWidth, orthoHeight, nearPlane, farPlane);
                    }
                    else
                    {
                        proj = Matrix4x4.CreatePerspectiveFieldOfView((float)Math.PI / 4f, (float)Width / Height, nearPlane, farPlane);
                    }
                    
                    var wvp = centerOffset * view * proj;
                    _lastWvp = wvp; // Store for raycasting
                    _context.UpdateSubresource(Matrix4x4.Transpose(wvp), _constantBuffer);

                    _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                    _context.IASetInputLayout(_inputLayout);
                    
                    _context.VSSetShader(_vertexShader);
                    _context.VSSetConstantBuffer(0, _constantBuffer);
                    _context.PSSetShader(_pixelShader);
                    _context.PSSetConstantBuffer(1, _psConstantBuffer);

                    // Render each model slot
                    foreach (var kvp in _models)
                    {
                        var model = kvp.Value;
                        if (HiddenSlots.Contains(kvp.Key)) continue;
                        if (model.IndexCount == 0 || model.VertexBuffer == null || model.IndexBuffer == null) continue;

                        _context.IASetVertexBuffer(0, model.VertexBuffer, 32); // 32 bytes stride
                        _context.IASetIndexBuffer(model.IndexBuffer, Format.R16_UInt, 0);

                        // Bind texture if loaded
                        if (model.HasTexture && model.TextureSRV != null && model.SamplerState != null)
                        {
                            _context.PSSetShaderResource(0, model.TextureSRV);
                            _context.PSSetSampler(0, model.SamplerState);
                            // Set pixel shader flags: hasTexture = 1.0
                            _context.UpdateSubresource(new Vector4(1, 0, 0, 0), _psConstantBuffer);
                        }
                        else
                        {
                            _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
                            _context.UpdateSubresource(new Vector4(0, 0, 0, 0), _psConstantBuffer);
                        }

                        _context.DrawIndexed(model.IndexCount, 0, 0);
                    }

                    // Unbind texture to avoid state leaking into FFXIV
                    _context.PSSetShaderResource(0, (ID3D11ShaderResourceView)null);
                }
                
                // Unbind to prevent issues with FFXIV
                _context.OMSetRenderTargets((ID3D11RenderTargetView)null, (ID3D11DepthStencilView)null);
                _context.RSSetState(null);
            }
            catch (Exception ex)
            {
                _renderError = "Render error: " + ex.Message;
            }
        }

        /// <summary>
        /// Load RGBA pixel data as a diffuse texture for a specific model slot.
        /// </summary>
        public void LoadTexture(string slot, byte[] rgbaPixels, int width, int height)
        {
            if (_device == null || rgbaPixels == null || rgbaPixels.Length < width * height * 4) return;
            if (!_models.TryGetValue(slot, out var model)) return; // Model must exist

            model.Texture?.Dispose();
            model.TextureSRV?.Dispose();
            model.SamplerState?.Dispose();

            var texDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };

            unsafe
            {
                fixed (byte* pPixels = rgbaPixels)
                {
                    var subData = new SubresourceData((IntPtr)pPixels, width * 4);
                    model.Texture = _device.CreateTexture2D(texDesc, new[] { subData });
                }
            }

            model.TextureSRV = _device.CreateShaderResourceView(model.Texture);

            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            };
            model.SamplerState = _device.CreateSamplerState(samplerDesc);

            model.HasTexture = true;
        }

        public void LoadTextureRaw(string slot, IntPtr pPixels, int width, int height)
        {
            if (_device == null || pPixels == IntPtr.Zero) return;
            if (!_models.TryGetValue(slot, out var model)) return; // Model must exist

            model.Texture?.Dispose();
            model.TextureSRV?.Dispose();
            model.SamplerState?.Dispose();

            var texDesc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };

            var subData = new SubresourceData(pPixels, width * 4);
            model.Texture = _device.CreateTexture2D(texDesc, new[] { subData });

            model.TextureSRV = _device.CreateShaderResourceView(model.Texture);

            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Wrap,
                AddressV = TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            };
            model.SamplerState = _device.CreateSamplerState(samplerDesc);

            model.HasTexture = true;
        }

        /// <summary>Remove the currently loaded texture for a slot, reverting to normal-colored shading.</summary>
        public void ClearTexture(string slot)
        {
            if (_models.TryGetValue(slot, out var model))
            {
                model.Texture?.Dispose();
                model.TextureSRV?.Dispose();
                model.SamplerState?.Dispose();
                model.Texture = null;
                model.TextureSRV = null;
                model.SamplerState = null;
                model.HasTexture = false;
            }
        }

        public bool HasTexture(string slot) => _models.TryGetValue(slot, out var m) && m.HasTexture;

        public IntPtr GetTextureHandle(string slot)
        {
            if (_models.TryGetValue(slot, out var model) && model.TextureSRV != null)
            {
                return model.TextureSRV.NativePointer;
            }
            return IntPtr.Zero;
        }

        public string[] GetAllSlotNames()
        {
            return _models.Keys.ToArray();
        }

        // ─── GPU Paint Implementation ───────────────────────────────────────

        private const string PaintBrushShaderCode = @"
cbuffer BrushParams : register(b0)
{
    float2 Center;
    float2 PrevCenter;
    float Radius;
    float Hardness;
    int HasPrev;
    int BlendMode;     // 0=Normal, 1=Eraser, 2=Multiply, 3=Screen, 4=Overlay, 5=SoftLight
    int ShapeMode;     // 0=Circle, 1=Square (kept for struct alignment)
    float Flow;        // per-dab alpha multiplier
    float Angle;       // rotation in radians
    float NoiseScale;  // grain frequency, 0 = off
    float NoiseAmount; // how much noise modulates alpha, 0-1
    float Seed;        // random seed for noise
    float Padding_A;   // alignment padding
    float Padding_B;   // alignment padding
    float4 Color;
};
RWTexture2D<float4> PaintLayer : register(u0);

//   GPU hash noise (no textures needed)  
float hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float valueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f); // smoothstep interpolation
    float a = hash21(i);
    float b = hash21(i + float2(1,0));
    float c = hash21(i + float2(0,1));
    float d = hash21(i + float2(1,1));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float distToSegment(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float2 ap = p - a;
    float t = saturate(dot(ap, ab) / max(dot(ab, ab), 0.0001f));
    float2 closest = a + ab * t;
    return length(p - closest);
}

//   Blend mode helpers  
float3 blendMultiply(float3 base, float3 blend) { return base * blend; }
float3 blendScreen(float3 base, float3 blend) { return 1.0 - (1.0 - base) * (1.0 - blend); }
float3 blendOverlay(float3 base, float3 blend)
{
    return lerp(
        2.0 * base * blend,
        1.0 - 2.0 * (1.0 - base) * (1.0 - blend),
        step(0.5, base));
}
float3 blendSoftLight(float3 base, float3 blend)
{
    return lerp(
        2.0 * base * blend + base * base * (1.0 - 2.0 * blend),
        sqrt(base) * (2.0 * blend - 1.0) + 2.0 * base * (1.0 - blend),
        step(0.5, blend));
}

[numthreads(16, 16, 1)]
void CSPaint(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    PaintLayer.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    //   Fill Tool  
    if (ShapeMode == 2) // Fill
    {
        PaintLayer[id.xy] = Color;
        return;
    }

    float2 pixel = float2(id.x, id.y) + 0.5f;

    //   Apply rotation: transform pixel into brush-local space  
    float2 localPixel = pixel - Center;
    if (abs(Angle) > 0.001f)
    {
        float cs = cos(-Angle);
        float sn = sin(-Angle);
        localPixel = float2(localPixel.x * cs - localPixel.y * sn,
                            localPixel.x * sn + localPixel.y * cs);
    }

    //   Distance computation  
    float dist = 0.0f;
    
    // For rotated square brush we use Chebyshev distance in rotated space
    if (ShapeMode == 1) // Square
    {
        float2 d = abs(localPixel);
        dist = max(d.x, d.y);
    }
    else
    {
        // Circle mode
        if (HasPrev > 0)
            dist = distToSegment(pixel, PrevCenter, Center);
        else
            dist = length(localPixel);
    }

    if (dist <= Radius)
    {
        //   Edge falloff  
        float softEdge = Radius * (1.0f - saturate(Hardness));
        float edge = (softEdge < 0.01f)
            ? step(dist, Radius)
            : smoothstep(Radius, Radius - softEdge, dist);

        //   Noise / texture grain modulation  
        float noiseMod = 1.0f;
        if (NoiseScale > 0.001f)
        {
            float n = valueNoise(pixel * NoiseScale + Seed);
            noiseMod = lerp(1.0f, n, NoiseAmount);
        }

        float alpha = Color.a * edge * Flow * noiseMod;
        float4 existing = PaintLayer[id.xy];
        
        if (BlendMode == 1) // Eraser
        {
            float outA = saturate(existing.a - alpha);
            PaintLayer[id.xy] = float4(existing.rgb, outA);
        }
        else
        {
            // Determine blended color
            float3 brushRGB = Color.rgb;
            if (BlendMode == 2) // Multiply
                brushRGB = blendMultiply(existing.rgb, Color.rgb);
            else if (BlendMode == 3) // Screen
                brushRGB = blendScreen(existing.rgb, Color.rgb);
            else if (BlendMode == 4) // Overlay
                brushRGB = blendOverlay(existing.rgb, Color.rgb);
            else if (BlendMode == 5) // Soft Light
                brushRGB = blendSoftLight(existing.rgb, Color.rgb);
            // else BlendMode == 0: Normal - brushRGB stays as Color.rgb

            float outA = alpha + existing.a * (1.0f - alpha);
            float3 outRGB = (outA > 0.001f)
                ? (brushRGB * alpha + existing.rgb * existing.a * (1.0f - alpha)) / outA
                : float3(0,0,0);
            PaintLayer[id.xy] = float4(outRGB, outA);
        }
    }
}";

        private const string CompositeShaderCode = @"
Texture2D<float4> BaseTexture : register(t0);
Texture2D<float4> PaintTexture : register(t1);
RWTexture2D<float4> Output : register(u0);

[numthreads(16, 16, 1)]
void CSComposite(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    Output.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    float4 baseCol = BaseTexture[id.xy];
    float4 paint = PaintTexture[id.xy];

    // Alpha blend paint over base
    float outA = paint.a + baseCol.a * (1.0f - paint.a);
    float3 outRGB = (outA > 0.001f)
        ? (paint.rgb * paint.a + baseCol.rgb * baseCol.a * (1.0f - paint.a)) / outA
        : float3(0,0,0);
    Output[id.xy] = float4(outRGB, outA);
}";

        private const string StampShaderCode = @"
cbuffer StampParams : register(b0)
{
    float2 UVPosition;
    float2 UVScale;
    float3 DecalCenter;
    float DecalDepth;
    float3 DecalNormal;
    float DecalRadius;
    float3 DecalTangent;
    int ProjectionMode;
    float3 DecalBitangent;
    float Padding1;
    matrix ViewProj;
    float3 CameraEye;
    float AspectRatio;
};
Texture2D<float4> StampTex : register(t0);
SamplerState StampSampler : register(s0);
RWTexture2D<float4> PaintLayer : register(u0);
Texture2D<float4> PositionMap : register(t1);
Texture2D<float4> NormalMap : register(t2);

[numthreads(16, 16, 1)]
void CSStamp(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    PaintLayer.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    float2 uv = float2(id.x, id.y) / float2(w, h);
    float4 stamp = float4(0,0,0,0);
    
    if (ProjectionMode == 1) // 3D Tangent Space
    {
        float4 posData = PositionMap[id.xy];
        if (posData.a > 0.5f)
        {
            float3 pos = posData.xyz;
            float3 norm = NormalMap[id.xy].xyz;
            
            float3 offset = pos - DecalCenter;
            float z = dot(offset, DecalNormal);
            
            if (z <= 0.1f && z >= -DecalDepth && dot(norm, DecalNormal) > 0.0f)
            {
                if (length(offset) <= DecalRadius * 1.5f)
                {
                    float x = dot(offset, DecalTangent);
                    float y = dot(offset, DecalBitangent);
                    
                    float radiusX = DecalRadius;
                    float radiusY = DecalRadius * (UVScale.y / UVScale.x);
                    
                    if (abs(x) <= radiusX && abs(y) <= radiusY)
                    {
                        float2 localUv = float2(x / radiusX * 0.5f + 0.5f, -y / radiusY * 0.5f + 0.5f);
                        stamp = StampTex.SampleLevel(StampSampler, localUv, 0);
                    }
                }
            }
        }
    }
    else if (ProjectionMode == 2) // 3D Camera Space
    {
        float4 posData = PositionMap[id.xy];
        if (posData.a > 0.5f)
        {
            float3 pos = posData.xyz;
            float3 norm = NormalMap[id.xy].xyz;
            
            float3 offset = pos - DecalCenter;
            float3 viewDir = normalize(pos - CameraEye);
            
            // True screen space: we only cull backfaces and limit the overall 3D radius.
            // We do NOT use the tangent plane 'z' depth bounds, because that causes flat cut-offs on curved surfaces.
            if (dot(norm, -viewDir) > -0.1f)
            {
                if (length(offset) <= DecalRadius * 5.0f) // Slightly larger radius for screen-space to prevent sphere cutoffs
                {
                    float4 clipPos = mul(float4(pos, 1.0f), ViewProj);
                    if (clipPos.w > 0.001f)
                    {
                        clipPos.xyz /= clipPos.w;
                        float2 screenUv = float2(clipPos.x * 0.5f + 0.5f, 1.0f - (clipPos.y * 0.5f + 0.5f));
                        float2 screenUvAdjusted = screenUv * float2(AspectRatio, 1.0f);
                        
                        float4 centerClip = mul(float4(DecalCenter, 1.0f), ViewProj);
                        centerClip.xyz /= centerClip.w;
                        float2 centerScreenUv = float2(centerClip.x * 0.5f + 0.5f, 1.0f - (centerClip.y * 0.5f + 0.5f));
                        float2 centerScreenUvAdjusted = centerScreenUv * float2(AspectRatio, 1.0f);
                        
                        float2 minPos = centerScreenUvAdjusted - UVScale * 0.5f;
                        float2 maxPos = centerScreenUvAdjusted + UVScale * 0.5f;
                        
                        if (screenUvAdjusted.x >= minPos.x && screenUvAdjusted.x <= maxPos.x &&
                            screenUvAdjusted.y >= minPos.y && screenUvAdjusted.y <= maxPos.y)
                        {
                            float2 localUv = (screenUvAdjusted - minPos) / UVScale;
                            stamp = StampTex.SampleLevel(StampSampler, localUv, 0);
                        }
                    }
                }
            }
        }
    }
    else // 2D Canvas
    {
        if (uv.x >= UVPosition.x && uv.x <= UVPosition.x + UVScale.x &&
            uv.y >= UVPosition.y && uv.y <= UVPosition.y + UVScale.y)
        {
            float2 localUv = (uv - UVPosition) / UVScale;
            stamp = StampTex.SampleLevel(StampSampler, localUv, 0);
        }
    }
    
    if (stamp.a > 0.001f)
    {
        float4 existing = PaintLayer[id.xy];
        float outA = stamp.a + existing.a * (1.0f - stamp.a);
        float3 outRGB = (outA > 0.001f)
            ? (stamp.rgb * stamp.a + existing.rgb * existing.a * (1.0f - stamp.a)) / outA
            : float3(0,0,0);
        PaintLayer[id.xy] = float4(outRGB, outA);
    }
}";

        public void InitGpuPaint(int width, int height)
        {
            if (_disposed || _device == null) return;

            // Dispose old resources
            DisposeGpuPaint();

            _paintTexWidth = width;
            _paintTexHeight = height;

            // Paint layer: UAV + SRV, float for precision
            var paintDesc = new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };
            _gpuPaintTex = _device.CreateTexture2D(paintDesc);
            _gpuPaintUAV = _device.CreateUnorderedAccessView(_gpuPaintTex);
            _gpuPaintSRV = _device.CreateShaderResourceView(_gpuPaintTex);

            // Composite output: UAV + SRV
            var compDesc = new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };
            _gpuCompositeTex = _device.CreateTexture2D(compDesc);
            _gpuCompositeUAV = _device.CreateUnorderedAccessView(_gpuCompositeTex);
            _gpuCompositeSRV = _device.CreateShaderResourceView(_gpuCompositeTex);

            // Compile compute shaders
            var paintBlob = Vortice.D3DCompiler.Compiler.Compile(PaintBrushShaderCode, "CSPaint", "", "cs_5_0");
            _paintBrushCS = _device.CreateComputeShader(paintBlob.Span);

            var compBlob = Vortice.D3DCompiler.Compiler.Compile(CompositeShaderCode, "CSComposite", "", "cs_5_0");
            _compositeCS = _device.CreateComputeShader(compBlob.Span);

            var stampBlob = Vortice.D3DCompiler.Compiler.Compile(StampShaderCode, "CSStamp", "", "cs_5_0");
            _stampCS = _device.CreateComputeShader(stampBlob.Span);

            // Constant buffer for brush params (64 bytes)
            _brushCB = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = Marshal.SizeOf<BrushParams>(),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer
            });

            _stampCB = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = Marshal.SizeOf<StampParams>(), // 16 bytes
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer
            });

            // Clear the paint layer to transparent by dispatching a zero-fill
            GpuClearPaint();

            if (_stampSampler == null)
            {
                _stampSampler = _device.CreateSamplerState(new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunc = ComparisonFunction.Never,
                    MinLOD = 0,
                    MaxLOD = float.MaxValue
                });
            }

            _gpuPaintReady = true;
        }

        public void SetBaseTexture(IntPtr bgraPixels, int width, int height)
        {
            if (_disposed || _device == null || bgraPixels == IntPtr.Zero) return;

            if (_gpuBaseTex != null && _gpuBaseTex.NativePointer != IntPtr.Zero && _baseTexWidth == width && _baseTexHeight == height)
            {
                _context.UpdateSubresource(_gpuBaseTex, 0, null, bgraPixels, width * 4, 0);
                return;
            }

            _gpuBaseTex?.Dispose();
            _gpuBaseSRV?.Dispose();

            var texDesc = new Vortice.Direct3D11.Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = Vortice.Direct3D11.ResourceUsage.Default,
                BindFlags = Vortice.Direct3D11.BindFlags.ShaderResource,
                CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.None
            };

            _gpuBaseTex = _device.CreateTexture2D(texDesc, new Vortice.Direct3D11.SubresourceData(bgraPixels, width * 4));
            _gpuBaseSRV = _device.CreateShaderResourceView(_gpuBaseTex);
            _baseTexWidth = width;
            _baseTexHeight = height;
        }

        public void GpuPaintStroke(Vector2 uvCenter, Vector2? uvPrev, float radiusPixels, float hardness, Vector4 color, int blendMode = 0, int shapeMode = 0, float flow = 1.0f, float angle = 0f, float noiseScale = 0f, float noiseAmount = 0f, float seed = 0f)
        {
            if (_disposed || !_gpuPaintReady || _context == null) return;

            var brushParams = new BrushParams
            {
                Center = new Vector2(uvCenter.X * _paintTexWidth, uvCenter.Y * _paintTexHeight),
                PrevCenter = uvPrev.HasValue
                    ? new Vector2(uvPrev.Value.X * _paintTexWidth, uvPrev.Value.Y * _paintTexHeight)
                    : Vector2.Zero,
                Radius = radiusPixels,
                Hardness = hardness,
                HasPrev = uvPrev.HasValue ? 1 : 0,
                BlendMode = blendMode,
                ShapeMode = shapeMode,
                Flow = flow,
                Angle = angle,
                NoiseScale = noiseScale,
                NoiseAmount = noiseAmount,
                Seed = seed,
                Color = color
            };

            _context.UpdateSubresource(brushParams, _brushCB);

            _context.CSSetShader(_paintBrushCS);
            _context.CSSetConstantBuffer(0, _brushCB);
            _context.CSSetUnorderedAccessView(0, _gpuPaintUAV);

            int groupsX = (_paintTexWidth + 15) / 16;
            int groupsY = (_paintTexHeight + 15) / 16;
            _context.Dispatch(groupsX, groupsY, 1);

            _context.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView)null);
            _context.CSSetShader(null);
        }

        /// <summary>
        /// Uploads RGBA byte pixel data into the GPU paint layer, replacing its current content.
        /// The input must match the paint texture dimensions (PaintTexWidth x PaintTexHeight).
        /// </summary>
        public void LoadPaintLayerFromRgba(byte[] rgbaPixels, int width, int height)
        {
            if (!_gpuPaintReady || _context == null || _gpuPaintTex == null) return;
            if (width != _paintTexWidth || height != _paintTexHeight) return;

            // Convert byte RGBA to float RGBA (paint texture is R32G32B32A32_Float)
            float[] floatPixels = new float[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int bi = i * 4;
                floatPixels[bi + 0] = rgbaPixels[bi + 0] / 255f; // R
                floatPixels[bi + 1] = rgbaPixels[bi + 1] / 255f; // G
                floatPixels[bi + 2] = rgbaPixels[bi + 2] / 255f; // B
                floatPixels[bi + 3] = rgbaPixels[bi + 3] / 255f; // A
            }

            // Create a staging texture, copy data in, then copy to paint texture
            var stagingDesc = new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.None
            };

            unsafe
            {
                fixed (float* ptr = floatPixels)
                {
                    var subData = new SubresourceData((IntPtr)ptr, width * 16); // 16 bytes per pixel (4 floats)
                    using var staging = _device.CreateTexture2D(stagingDesc, new[] { subData });
                    _context.CopyResource(_gpuPaintTex, staging);
                }
            }
        }

        public void GpuStampTexture(ID3D11ShaderResourceView stampSrv, Vector2 position, Vector2 scale, int projectionMode = 0, Vector3 center = default, Vector3 normal = default, Vector3 tangent = default, Vector3 bitangent = default, float radius = 0.5f, float depth = 1f)
        {
            if (!_gpuPaintReady || _context == null || stampSrv == null) return;
            if (projectionMode > 0 && (PositionMapSRV == null || NormalMapSRV == null)) return;

            var stampParams = new StampParams
            {
                Position = position,
                Scale = scale,
                DecalCenter = center,
                DecalDepth = depth,
                DecalNormal = normal,
                DecalRadius = radius,
                DecalTangent = tangent,
                ProjectionMode = projectionMode,
                DecalBitangent = bitangent,
                Padding1 = 0,
                ViewProj = Matrix4x4.Transpose(_lastWvp),
                CameraEye = _lastEye,
                AspectRatio = (float)Width / Height
            };

            _context.UpdateSubresource(stampParams, _stampCB);

            _context.CSSetShader(_stampCS);
            _context.CSSetConstantBuffer(0, _stampCB);
            _context.CSSetShaderResource(0, stampSrv);
            _context.CSSetShaderResource(1, PositionMapSRV);
            _context.CSSetShaderResource(2, NormalMapSRV);
            _context.CSSetSampler(0, _stampSampler);
            _context.CSSetUnorderedAccessView(0, _gpuPaintUAV);

            int groupsX = (_paintTexWidth + 15) / 16;
            int groupsY = (_paintTexHeight + 15) / 16;
            _context.Dispatch(groupsX, groupsY, 1);

            _context.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView)null);
            _context.CSSetShaderResource(0, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(1, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(2, (ID3D11ShaderResourceView)null);
            _context.CSSetSampler(0, (ID3D11SamplerState)null);
            _context.CSSetShader(null);
        }

        public void GpuPreviewStampTexture(ID3D11ShaderResourceView stampSrv, Vector2 position, Vector2 scale, int projectionMode = 0, Vector3 center = default, Vector3 normal = default, Vector3 tangent = default, Vector3 bitangent = default, float radius = 0.5f, float depth = 1f)
        {
            if (!_gpuPaintReady || _context == null || stampSrv == null) return;
            if (projectionMode > 0 && (PositionMapSRV == null || NormalMapSRV == null)) return;

            var stampParams = new StampParams
            {
                Position = position,
                Scale = scale,
                DecalCenter = center,
                DecalDepth = depth,
                DecalNormal = normal,
                DecalRadius = radius,
                DecalTangent = tangent,
                ProjectionMode = projectionMode,
                DecalBitangent = bitangent,
                Padding1 = 0,
                ViewProj = Matrix4x4.Transpose(_lastWvp),
                CameraEye = _lastEye,
                AspectRatio = (float)Width / Height
            };

            _context.UpdateSubresource(stampParams, _stampCB);

            _context.CSSetShader(_stampCS);
            _context.CSSetConstantBuffer(0, _stampCB);
            _context.CSSetShaderResource(0, stampSrv);
            _context.CSSetShaderResource(1, PositionMapSRV);
            _context.CSSetShaderResource(2, NormalMapSRV);
            _context.CSSetSampler(0, _stampSampler);
            _context.CSSetUnorderedAccessView(0, _gpuCompositeUAV); // Preview goes onto Composite texture

            int groupsX = (_paintTexWidth + 15) / 16;
            int groupsY = (_paintTexHeight + 15) / 16;
            _context.Dispatch(groupsX, groupsY, 1);

            _context.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView)null);
            _context.CSSetShaderResource(0, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(1, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(2, (ID3D11ShaderResourceView)null);
            _context.CSSetSampler(0, (ID3D11SamplerState)null);
            _context.CSSetShader(null);
        }

        public ID3D11ShaderResourceView CreateSrvFromRgba(byte[] rgba, int width, int height)
        {
            if (_device == null || rgba == null) return null;
            var texDesc = new Texture2DDescription
            {
                Width = width, Height = height,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None
            };
            unsafe
            {
                fixed (byte* pPixels = rgba)
                {
                    var subData = new SubresourceData((IntPtr)pPixels, width * 4);
                    using var tex = _device.CreateTexture2D(texDesc, new[] { subData });
                    return _device.CreateShaderResourceView(tex);
                }
            }
        }

        public void GpuComposite(string[] slots)
        {
            if (!_gpuPaintReady || _context == null || _gpuBaseSRV == null) return;

            _context.CSSetShader(_compositeCS);
            _context.CSSetShaderResource(0, _gpuBaseSRV);
            _context.CSSetShaderResource(1, _gpuPaintSRV);
            _context.CSSetUnorderedAccessView(0, _gpuCompositeUAV);

            int groupsX = (_paintTexWidth + 15) / 16;
            int groupsY = (_paintTexHeight + 15) / 16;
            _context.Dispatch(groupsX, groupsY, 1);

            // Unbind
            _context.CSSetUnorderedAccessView(0, (ID3D11UnorderedAccessView)null);
            _context.CSSetShaderResource(0, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(1, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(2, (ID3D11ShaderResourceView)null);
            _context.CSSetShaderResource(1, (ID3D11ShaderResourceView)null);
            _context.CSSetShader(null);

            // Point the model texture SRVs at the composite
            foreach (var slotName in slots)
            {
                if (_models.TryGetValue(slotName, out var model))
                {
                    // Replace the model's texture SRV with the composite SRV
                    // (don't dispose the old one if it was previously the composite SRV)
                    model.TextureSRV = _gpuCompositeSRV;
                    model.HasTexture = true;

                    // Ensure a SamplerState exists so the render pipeline uses the texture
                    if (model.SamplerState == null)
                    {
                        model.SamplerState = _device.CreateSamplerState(new SamplerDescription
                        {
                            Filter = Filter.MinMagMipLinear,
                            AddressU = TextureAddressMode.Wrap,
                            AddressV = TextureAddressMode.Wrap,
                            AddressW = TextureAddressMode.Wrap,
                            ComparisonFunc = ComparisonFunction.Never,
                            MinLOD = 0,
                            MaxLOD = float.MaxValue
                        });
                    }
                }
            }
        }

        public void GpuClearPaint()
        {
            if (_context == null || _gpuPaintUAV == null) return;

            // Clear the UAV memory directly instead of recreating the texture
            _context.ClearUnorderedAccessView(_gpuPaintUAV, System.Numerics.Vector4.Zero);
        }

        public IntPtr GetCompositeSrvHandle()
        {
            return _gpuCompositeSRV?.NativePointer ?? IntPtr.Zero;
        }

        /// <summary>
        /// Reads the paint layer back from GPU to CPU as BGRA8 pixels for saving to disk.
        /// </summary>
        public byte[] ReadbackPaintLayer()
        {
            if (!_gpuPaintReady || _gpuPaintTex == null) return null;

            // Create a staging texture to read back
            var stagingDesc = new Texture2DDescription
            {
                Width = _paintTexWidth, Height = _paintTexHeight,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            };
            using var staging = _device.CreateTexture2D(stagingDesc);
            _context.CopyResource(staging, _gpuPaintTex);

            var mapped = _context.Map(staging, 0, MapMode.Read);
            try
            {
                byte[] result = new byte[_paintTexWidth * _paintTexHeight * 4];
                unsafe
                {
                    float* src = (float*)mapped.DataPointer;
                    for (int y = 0; y < _paintTexHeight; y++)
                    {
                        float* row = (float*)((byte*)mapped.DataPointer + y * mapped.RowPitch);
                        for (int x = 0; x < _paintTexWidth; x++)
                        {
                            int si = x * 4;
                            int di = (y * _paintTexWidth + x) * 4;
                            // Convert float RGBA to byte BGRA for System.Drawing
                            result[di + 0] = (byte)(Math.Clamp(row[si + 2], 0f, 1f) * 255f); // B
                            result[di + 1] = (byte)(Math.Clamp(row[si + 1], 0f, 1f) * 255f); // G
                            result[di + 2] = (byte)(Math.Clamp(row[si + 0], 0f, 1f) * 255f); // R
                            result[di + 3] = (byte)(Math.Clamp(row[si + 3], 0f, 1f) * 255f); // A
                        }
                    }
                }
                return result;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }

        public Vector4 ReadCompositePixel(Vector2 uv)
        {
            if (!_gpuPaintReady || _gpuCompositeTex == null || _context == null) return Vector4.Zero;

            int px = (int)(uv.X * _paintTexWidth);
            int py = (int)(uv.Y * _paintTexHeight);
            px = Math.Clamp(px, 0, _paintTexWidth - 1);
            py = Math.Clamp(py, 0, _paintTexHeight - 1);

            var box = new Vortice.Mathematics.Box(px, py, 0, px + 1, py + 1, 1);

            var stagingDesc = new Texture2DDescription
            {
                Width = 1, Height = 1,
                MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            };

            using var staging = _device.CreateTexture2D(stagingDesc);
            _context.CopySubresourceRegion(staging, 0, 0, 0, 0, _gpuCompositeTex, 0, box);

            var mapped = _context.Map(staging, 0, MapMode.Read);
            Vector4 result = Vector4.Zero;
            try
            {
                unsafe
                {
                    float* data = (float*)mapped.DataPointer;
                    result = new Vector4(data[0], data[1], data[2], data[3]);
                }
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
            return result;
        }

        public int PaintTexWidth => _paintTexWidth;
        public int PaintTexHeight => _paintTexHeight;

        public void PushUndoSnapshot()
        {
            if (!_gpuPaintReady || _gpuPaintTex == null || _device == null || _context == null) return;
            
            // Clear redo stack on new action
            foreach(var t in _redoStack) t.Dispose();
            _redoStack.Clear();
            
            // Create a copy of the current paint texture
            var desc = _gpuPaintTex.Description;
            var snapshot = _device.CreateTexture2D(desc);
            _context.CopyResource(snapshot, _gpuPaintTex);
            
            _undoStack.Add(snapshot);
            
            // Trim if over limit
            if (_undoStack.Count > MaxUndoSteps)
            {
                _undoStack[0].Dispose();
                _undoStack.RemoveAt(0);
            }
        }
        
        public void Undo()
        {
            if (!CanUndo || !_gpuPaintReady || _gpuPaintTex == null || _context == null) return;
            
            // Push current state to redo
            var desc = _gpuPaintTex.Description;
            var currentSnapshot = _device.CreateTexture2D(desc);
            _context.CopyResource(currentSnapshot, _gpuPaintTex);
            _redoStack.Add(currentSnapshot);
            
            // Pop from undo and apply
            var undoTex = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            
            _context.CopyResource(_gpuPaintTex, undoTex);
            undoTex.Dispose();
        }
        
        public void Redo()
        {
            if (!CanRedo || !_gpuPaintReady || _gpuPaintTex == null || _context == null) return;
            
            // Push current state to undo
            var desc = _gpuPaintTex.Description;
            var currentSnapshot = _device.CreateTexture2D(desc);
            _context.CopyResource(currentSnapshot, _gpuPaintTex);
            _undoStack.Add(currentSnapshot);
            
            // Pop from redo and apply
            var redoTex = _redoStack[_redoStack.Count - 1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            
            _context.CopyResource(_gpuPaintTex, redoTex);
            redoTex.Dispose();
        }

        private void DisposeGpuPaint()
        {
            _gpuPaintReady = false;
            foreach(var t in _undoStack) t.Dispose();
            _undoStack.Clear();
            foreach(var t in _redoStack) t.Dispose();
            _redoStack.Clear();
            _gpuPaintUAV?.Dispose(); _gpuPaintUAV = null;
            _gpuPaintSRV?.Dispose(); _gpuPaintSRV = null;
            _gpuPaintTex?.Dispose(); _gpuPaintTex = null;
            _gpuCompositeUAV?.Dispose(); _gpuCompositeUAV = null;
            _gpuCompositeSRV?.Dispose(); _gpuCompositeSRV = null;
            _gpuCompositeTex?.Dispose(); _gpuCompositeTex = null;
            _gpuBaseSRV?.Dispose(); _gpuBaseSRV = null;
            _gpuBaseTex?.Dispose(); _gpuBaseTex = null;
            _paintBrushCS?.Dispose(); _paintBrushCS = null;
            _compositeCS?.Dispose(); _compositeCS = null;
            _stampCS?.Dispose(); _stampCS = null;
            _brushCB?.Dispose(); _brushCB = null;
            _stampCB?.Dispose(); _stampCB = null;
            _stampSampler?.Dispose(); _stampSampler = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var model in _models.Values)
            {
                // Don't dispose TextureSRV if it's the shared composite SRV
                if (model.TextureSRV == _gpuCompositeSRV)
                    model.TextureSRV = null;
                model.Dispose();
            }
            _models.Clear();

            DisposeGpuPaint();

            _vertexShader?.Dispose();
            _pixelShader?.Dispose();
            _inputLayout?.Dispose();
            _constantBuffer?.Dispose();
            _psConstantBuffer?.Dispose();

            _depthStencilView?.Dispose();
            _depthStencilTexture?.Dispose();
            _depthStencilState?.Dispose();
            _rasterizerState?.Dispose();

            _renderTargetView?.Dispose();
            _shaderResourceView?.Dispose();
            _renderTargetTexture?.Dispose();
            // We do NOT dispose _device or _context because FFXIV owns them!
        }
    }
}
