using System;
using System.Numerics;
using System.Collections.Generic;
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
            if (width <= 0 || height <= 0 || _device == null) return;
            
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

        private struct Vertex
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

            model.VertexBuffer?.Dispose();
            model.VertexBuffer = _device.CreateBuffer(allVertices.ToArray(), BindFlags.VertexBuffer);

            model.IndexBuffer?.Dispose();
            model.IndexBuffer = _device.CreateBuffer(allIndices.ToArray(), BindFlags.IndexBuffer);

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

            _meshLoaded = true;
        }

        private bool _meshLoaded = false;
        private string _renderError = null;
        public string RenderError => _renderError;

        // Bounding box for auto-centering and camera fit
        private Vector3 _boundsMin = Vector3.Zero;
        private Vector3 _boundsMax = Vector3.Zero;
        private Vector3 _boundsCenter = Vector3.Zero;
        private float _boundsRadius = 1.0f;

        // Interactive orbital camera state
        private float _cameraYaw = 0f;
        private float _cameraPitch = 0.3f;
        private float _cameraDistance = 1f; // Multiplier of boundsRadius * 2.5
        private Vector3 _cameraPan = Vector3.Zero;

        /// <summary>Toggle between perspective and orthographic projection.</summary>
        public bool UseOrthographic { get; set; } = false;

        /// <summary>Rotate the camera orbit by yaw/pitch delta (radians).</summary>
        public void RotateCamera(float deltaYaw, float deltaPitch)
        {
            _cameraYaw += deltaYaw;
            _cameraPitch += deltaPitch;
            // Clamp pitch to avoid flipping
            _cameraPitch = Math.Clamp(_cameraPitch, -1.5f, 1.5f);
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
            float cosYaw = MathF.Cos(_cameraYaw);
            float sinYaw = MathF.Sin(_cameraYaw);
            float cosPitch = MathF.Cos(_cameraPitch);
            float sinPitch = MathF.Sin(_cameraPitch);

            var right = new Vector3(cosYaw, 0, -sinYaw);
            var up = new Vector3(sinYaw * sinPitch, cosPitch, cosYaw * sinPitch);

            _cameraPan += right * (-deltaX * panScale) + up * (deltaY * panScale);
        }

        /// <summary>Reset camera to default view.</summary>
        public void ResetCamera()
        {
            _cameraYaw = 0f;
            _cameraPitch = 0.3f;
            _cameraDistance = 1f;
            _cameraPan = Vector3.Zero;
        }

        public void Render()
        {
            if (_context == null || _renderTargetView == null) return;

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

                    float cosPitch = MathF.Cos(_cameraPitch);
                    float sinPitch = MathF.Sin(_cameraPitch);
                    float cosYaw = MathF.Cos(_cameraYaw);
                    float sinYaw = MathF.Sin(_cameraYaw);

                    var eyeOffset = new Vector3(
                        sinYaw * cosPitch,
                        sinPitch,
                        cosYaw * cosPitch
                    ) * camDist;

                    var target = _cameraPan;
                    var eye = target + eyeOffset;

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
                    
                    var wvp = Matrix4x4.Transpose(centerOffset * view * proj);
                    _context.UpdateSubresource(wvp, _constantBuffer);

                    _context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                    _context.IASetInputLayout(_inputLayout);
                    
                    _context.VSSetShader(_vertexShader);
                    _context.VSSetConstantBuffer(0, _constantBuffer);
                    _context.PSSetShader(_pixelShader);
                    _context.PSSetConstantBuffer(1, _psConstantBuffer);

                    // Render each model slot
                    foreach (var model in _models.Values)
                    {
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

        public void Dispose()
        {
            foreach (var model in _models.Values)
            {
                model.Dispose();
            }
            _models.Clear();

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
