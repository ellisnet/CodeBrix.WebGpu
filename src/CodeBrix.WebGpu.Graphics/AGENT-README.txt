================================================================================
AGENT-README: CodeBrix.WebGpu.Graphics
A Guide for AI Coding Agents — CONSUMING the CodeBrix.WebGpu.Graphics.MitLicenseForever NuGet package
================================================================================

OVERVIEW
========

CodeBrix.WebGpu.Graphics is a small graphics layer over WebGPU. It sits on top
of CodeBrix.WebGpu.Native (the raw, generated WebGPU binding) and wraps the
parts every renderer needs so an application can draw without touching a raw
binding call:

  * a CONTEXT (`GraphicsContext`) that owns - or shares - the instance,
    adapter, device and queue, reports errors, and tracks the lifetime of
    every resource it creates;
  * RESOURCES: buffers, textures, samplers, WGSL shaders, bind-group layouts
    and bind groups;
  * PIPELINE BUILDERS for render and compute pipelines, with vertex layouts
    derived from blittable structs;
  * FRAMES and PASSES (`GraphicsFrame`, `RenderPass`, `ComputePass`);
  * a batched 2D renderer (`SpriteBatch`) and a lit 3D renderer
    (`MeshRenderer`, `Mesh`, `LitMaterial`, `Camera`, `OrbitCamera`);
  * IMAGES: image files decoded by CodeBrix.Imaging into textures (sRGB or
    linear, GPU mipmaps), readback and saving, texture arrays, and animated
    images (`AnimatedTexture`);
  * MODELS: glTF 2.0 files loaded onto the GPU (`Model`), placed any number
    of times (`ModelInstance`) and drawn in batches (`ModelRenderer`);
  * ENVIRONMENTS: image-based lighting from HDR panoramas (Radiance .hdr,
    JPEG XL) or a procedural sky (`EnvironmentMap`, baked on the GPU), a sky
    background (`SkyboxRenderer`) and an HDR path with tone mapping
    (`ToneMapper`);
  * PRESENTATION: any texture view (`RenderTargetView`), a headless target
    (`HeadlessRenderTarget`) and a window surface over native window handles
    (`WindowSurface`, `NativeWindowSource`). The library creates no windows;
  * the FULL WebGPU feature set in the same style: adapter choice, features
    and limits (`GraphicsCapabilities`), queries and GPU timing
    (`GpuQuerySet`, `GpuTimer`), indirect and multi-draws, render bundles
    (`RenderBundleBuilder`, `GpuRenderBundle`), buffer mapping, texture
    copies, clears and uploads, immediates (push constants), debug groups,
    SPIR-V / GLSL / trusted shaders, explicit pipeline layouts, finished
    command buffers submitted together, task-returning waits, wrappers for
    every raw handle type, native Vulkan / Direct3D 12 / Metal interop and
    engine diagnostics (see ADVANCED API REFERENCE).

Every raw handle stays reachable (`Handle` properties, `GraphicsContext.Device`,
`RenderPass.Handle`, `GraphicsFrame.Encoder`), so raw CodeBrix.WebGpu.Native
calls can be mixed in where the wrapper stops.

The package depends on CodeBrix.WebGpu.Native, CodeBrix.Imaging (image
decoding/encoding, Apache-2.0) and CodeBrix.Graphics3D.Gltf2 (glTF reading).
It does not reference SkiaSharp or CodeBrix.WebGpu.Skia; it shares a device
and a texture with them through the sharing contracts of CodeBrix.WebGpu.Native
(see SHARING A DEVICE WITH CODEBRIX.WEBGPU.SKIA).


INSTALLATION
============

  Package id:   CodeBrix.WebGpu.Graphics.MitLicenseForever
  Install:      dotnet add package CodeBrix.WebGpu.Graphics.MitLicenseForever
  Namespace:    CodeBrix.WebGpu.Graphics
  Target:       net10.0 (any OS the native package supports: Windows x64/ARM64,
                macOS Intel/Apple Silicon, Linux x64/ARM64/RISC-V 64,
                Android ARM64/x64)
  Brings:       CodeBrix.WebGpu.Native.MitLicenseForever (with the WebGPU
                natives for every platform),
                CodeBrix.Imaging.ApacheLicenseForever (fully managed, Apache-2.0)
                and CodeBrix.Graphics3D.Gltf2.MitLicenseForever. Nothing else.
  csproj:       nothing special for the wrapper API. Add
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks> only if you also
                make raw CodeBrix.WebGpu.Native calls with pointers.
  Maths:        System.Numerics only - Vector2/3/4, Matrix3x2, Matrix4x4,
                Quaternion - plus System.Drawing.RectangleF for 2D rectangles.

  Name clash to know about: inside a namespace that starts with
  `CodeBrix.WebGpu`, the identifier `WebGpu` resolves to the NAMESPACE, not the
  static binding class. Write `Native.WebGpu.Foo(...)` there.
  More clashes: `using CodeBrix.Imaging;` together with `using System.Drawing;`
  makes RectangleF / PointF / Color ambiguous (alias the one you mean:
  `using RectangleF = System.Drawing.RectangleF;`), and
  `CodeBrix.Graphics3D.Gltf2.Schema2` has its own Mesh, Material, Image, Texture
  and Buffer types - you rarely need it (LoadModel hides it); alias it if you do.


KEY NAMESPACES / USINGS
=======================

  using CodeBrix.WebGpu.Graphics;   // everything in this package
  using CodeBrix.WebGpu.Native;     // enums and handle structs the API uses:
                                    // TextureFormat, BufferUsage, TextureUsage,
                                    // ShaderStage, VertexFormat, PresentMode,
                                    // Device, Queue, TextureView, CommandEncoder ...
  using System.Numerics;            // vectors and matrices
  using System.Drawing;             // RectangleF for the sprite batch
  using CodeBrix.Imaging;           // only when you pass or receive Imaging images
  using CodeBrix.Imaging.PixelFormats;  // Rgba32 (Image<Rgba32> from ToImage)


CORE API REFERENCE
==================

GraphicsContext : IDisposable, IWebGpuDeviceSource   (not thread-safe; one thread)
-------------------------------------------------------------------------------
  static Create() / Create(GraphicsContextOptions)   -> throws GraphicsException
  static TryCreate(options, out context, out reason) -> bool, never throws
  static Wrap(Device, Queue, Instance = default, Adapter = default)
        Shares a device another library created. Takes its OWN reference on
        each handle (dispose order does not matter) and installs no callbacks.
        Pass the instance to create window surfaces; pass the adapter so the
        surface can read its capabilities.
  static Wrap(IWebGpuDeviceSource)   the same in one call, from a device
        owner such as WebGpuSkiaDevice (see SHARING A DEVICE ...)
  Instance, Adapter, Device, Queue      raw handles
  Backend (BackendType), AdapterName, Limits, IsWrapped, IsDisposed
  HasUncapturedErrorHandler             (IWebGpuDeviceSource)
  IsLost, LostReason
  event Action<GraphicsError> UncapturedError   raised from Poll / Submit
  event Action<string> DeviceLost               raised once, from Poll / Submit
  TakeErrors()          -> GraphicsError[] (queued errors nobody listened to)
  Poll(bool wait = false), WaitIdle()
  LiveResourceCount, GetLiveResources()  (oldest first)
  BeginFrame()                      frame with its own encoder (submits it)
  BeginFrame(CommandEncoder)        frame on someone else's encoder
  BeginFrame(IWebGpuTextureTarget, GpuTexture depth = null)
                                    frame inside a shared texture target's
                                    pass; Submit ends the pass
  HasUncapturedErrorHandler         false = uncaptured errors are fatal
  Dispose()     releases every tracked resource still alive (newest first),
                then queue, device, adapter, instance (only its references
                for a wrapped device)

  Resource factories (all tracked; all validate inside an error scope and
  throw GraphicsException carrying WebGPU's validation message):
    CreateBuffer(ulong size, BufferUsage, label)      size rounded up to 4;
                CopySrc|CopyDst added unless a Map usage is given
    CreateBuffer<T>(ReadOnlySpan<T> data, BufferUsage, label)
    CreateVertexBuffer<T>(ReadOnlySpan<T>), CreateIndexBuffer(ushort/uint span)
    CreateUniformBuffer(ulong size)                   rounded up to 16
    CreateUniformBufferFrom<T>(in T value)            one struct, size rounded to 16
    CreateStorageBuffer<T>(ReadOnlySpan<T>), CreateStorageBuffer(ulong size)
    CreateTexture2D(w, h, TextureFormat, TextureUsage, mipLevelCount = 1, label)
    CreateTextureFromRgba(w, h, ReadOnlySpan<byte> rgba, mipmaps = false, srgb = false)
    CreateRenderTexture(w, h, format = RGBA8Unorm)    render + sample + copy
    CreateDepthTexture(w, h, format = Depth32Float)
    WrapTexture(Texture, TextureView, TextureFormat, w, h)  another owner's texture
    WrapTexture(IWebGpuTextureTarget, label)  a shared texture target's texture
    CreateSampler(SamplerDescription = LinearClamp)
    CreateShader(string wgsl, label)                  throws ShaderCompilationException
    TryCreateShader(wgsl, label, out GpuShader, out string diagnostics)
    CreateBindGroupLayout([label,] params BindingLayoutEntry[])
    CreateBindGroup(layout, [label,] params BindingEntry[])
    CreateRenderPipeline(GpuShader) -> RenderPipelineBuilder
    CreateComputePipeline(GpuShader) -> ComputePipelineBuilder
    CreateMesh(MeshData) / CreateMesh(ReadOnlySpan<MeshVertex>, ReadOnlySpan<uint>)
    CreateSpriteBatch(), CreateMeshRenderer()
    CreateHeadlessTarget(w, h, format = RGBA8Unorm, withDepth = true, depthFormat = Depth32Float)
    CreateWindowSurface(NativeWindowSource)

GraphicsContextOptions
  Backend (GraphicsBackend: Default, Vulkan, Direct3D12, Metal, OpenGL)
  PowerPreference (default HighPerformance), ForceFallbackAdapter
  CompatibleWindow (NativeWindowSource: ask for an adapter that can present to
                    it; pass the SAME object to CreateWindowSurface)
  Label
  AdapterSelector (Func<GraphicsAdapterInfo, bool>: the first enumerated
                   adapter it accepts is used instead of the power request)
  RequiredFeatures (IList<FeatureName>), RequiredNativeFeatures
                   (IList<NativeFeature>), RequestAllAdapterFeatures
                   (every adapter feature except the experimental ones)
  RequiredLimits (Limits?: start from GraphicsLimits.Undefined()),
  RequestAdapterLimits (the adapter's own limits, e.g. MaxImmediateSize)
  InstanceFlags (InstanceFlag; default Empty)

GraphicsResource (abstract; every Gpu*, Mesh, LitMaterial, SpriteBatch,
MeshRenderer, HeadlessRenderTarget and WindowSurface derives from it)
  Context, Label, IsDisposed, Dispose()  (safe to call twice)

GpuBuffer
  Handle, Size, Usage
  Write<T>(ReadOnlySpan<T> / T[] data, ulong byteOffset = 0)   queue write
  WriteValue<T>(in T value, ulong byteOffset = 0)
  Read<T>(Span<T> destination, ulong byteOffset = 0)  waits for the GPU
  Read<T>(int count = -1) -> T[]

GpuTexture
  Handle, View (default view), Width, Height, Format, MipLevelCount, Usage,
  IsWrapped, AsRenderTarget()
  Write(ReadOnlySpan<byte>)     level 0 in the texture's byte order; for RGBA8
                                or BGRA8 with mips, the other levels are
                                generated on the CPU (2 x 2 box filter)
  Write<T>(ReadOnlySpan<T>)     same, from packed pixels (for example uint)
  WriteRgba(ReadOnlySpan<byte>) RGBA input, swapped to BGRA when needed
  WriteMipLevel(level, bytes)
  ReadPixels(Span<byte>, mipLevel = 0) / ReadPixels() / ReadRgba()
                                needs CopySrc; colour formats and Depth32Float
  static FullMipChainLength(w, h)
  (images, layer views and saving: see IMAGES AND TEXTURES below)

GpuSampler (Handle, AddressMode, MagFilter, MinFilter, IsComparison)
SamplerDescription: AddressMode, AddressModeU / AddressModeV (null = use
  AddressMode), MagFilter, MinFilter, MipmapFilter, MaxLod (default 32; 0 =
  top level only), Compare, MaxAnisotropy, Label; presets LinearClamp,
  LinearRepeat, NearestClamp, NearestRepeat
GpuShader (Handle, Source)
GpuBindGroupLayout (Handle, Entries), GpuBindGroup (Handle, Layout)
BindingLayoutEntry factories:
  Uniform(binding, ShaderStage, hasDynamicOffset = false, minBindingSize = 0)
  Storage(binding, ShaderStage, readOnly = false, hasDynamicOffset = false)
  Texture(binding, ShaderStage, sampleType = Float, viewDimension = 2D, multisampled = false)
  Sampler(binding, ShaderStage, type = Filtering)
  StorageTexture(binding, ShaderStage, TextureFormat, access = WriteOnly, viewDimension = 2D)
BindingEntry factories:
  ForBuffer(binding, GpuBuffer, offset = 0, size = whole)   (for a dynamic
            offset binding pass the size of ONE element)
  ForTexture(binding, GpuTexture), ForTextureView(binding, TextureView),
  ForSampler(binding, GpuSampler)

Pipelines
---------
RenderPipelineBuilder (defaults: entry points vs_main / fs_main, triangle
list, counter-clockwise front faces, no culling, no depth, 1 sample,
automatic layout derived from the shader)
  WithVertexEntry(name, shader = null), WithFragmentEntry(name, shader = null),
  WithoutFragment()
  WithVertexBuffer(VertexLayout) / WithVertexBuffer<T>(stepMode, firstLocation)
  WithColorTarget(TextureFormat, BlendPreset = Opaque, ColorWriteMask = All)
  WithDepth(TextureFormat, DepthPreset = ReadWrite, bias = 0, slopeScale = 0)
  WithCulling(CullPreset, FrontFace = CCW), WithTopology(PrimitiveTopology)
  WithSampleCount(n), WithBindGroupLayouts(params GpuBindGroupLayout[])
  WithLabel(text), Build() -> GpuRenderPipeline
GpuRenderPipeline: Handle, ColorFormats, DepthFormat, GetBindGroupLayout(i)
ComputePipelineBuilder: WithEntry(name = "main"), WithBindGroupLayouts,
  WithLabel, Build() -> GpuComputePipeline (Handle, GetBindGroupLayout(i))
BlendPreset: Opaque, AlphaBlend (straight alpha), PremultipliedAlpha,
  Additive, Multiply
DepthPreset: None (declares the format, compare Always, no writes),
  ReadWrite (Less), ReadWriteLessEqual, ReadOnly (LessEqual, no writes)
CullPreset: None, Back, Front

Vertex layouts (pure data, no GPU)
----------------------------------
VertexLayout.FromStruct<T>(stepMode = Vertex, firstLocation = 0)
  stride = sizeof(T); attributes from the fields in memory order.
  Inferred formats: float Float32, Vector2 Float32x2, Vector3 Float32x3,
  Vector4/Quaternion Float32x4, int Sint32, uint Uint32, Matrix4x4 = FOUR
  Float32x4 attributes on consecutive locations.
  [VertexElement(location)] / [VertexElement(location, VertexFormat)] on a
  field: when ANY field is marked, unmarked fields are skipped (padding).
  firstLocation is added to every location (instance buffers continue after
  the vertex buffer's locations).
VertexLayout.Describe(stepMode, firstLocation).Add(format).Add(format, location)
  .Skip(bytes).Build(stride = 0)
VertexLayout: ArrayStride, StepMode, Elements (VertexElement: Location,
  Format, Offset), NextLocation
VertexFormats.SizeOf(VertexFormat), TextureFormats.BytesPerPixel / IsDepth /
  IsBgra8 / IsRgba8Family

Matrices: a Matrix4x4 uploaded as-is (uniform or vertex attribute rows) is
read by WGSL as its transpose, so `m * v` in WGSL equals
`Vector4.Transform(v, m)` in C#. Build world * view * projection in C# with
System.Numerics as usual and multiply on the LEFT in WGSL.

Frames and passes
-----------------
GraphicsFrame : IDisposable
  Context, Encoder, OwnsEncoder, Id (unique), IsSubmitted
  TextureTarget, Target (frames from BeginFrame(IWebGpuTextureTarget, depth))
  BeginRenderPass(RenderTargetView, Color4? clearColor, float? clearDepth = 1)
        clearColor null = keep (load) the colour; clearDepth null = load depth
  BeginRenderPass(Color4? clearColor, float? clearDepth = 1)
        into frame.Target (throws for a frame without a texture target)
  BeginComputePass()
  CopyBuffer(source, sourceOffset, destination, destinationOffset, size)
  Submit()   owned encoder: finish + submit; borrowed: only closes the frame;
             texture target: ends the target's pass (the target submits).
             Then raises the context's queued events. The finished commands
             are validated first: invalid ones throw GraphicsException (with
             WebGPU's message) and are never submitted.
  Dispose()  abandons an unsubmitted owned encoder
  (more frame commands: see ADVANCED API REFERENCE)
RenderPass : IDisposable  (Frame, Target, Handle, IsEnded)
  SetPipeline, SetBindGroup(index, group [, dynamicOffsets]),
  SetVertexBuffer(slot, buffer, offset, size), SetIndexBuffer(buffer,
  IndexFormat, offset, size), Draw, DrawIndexed, SetViewport,
  SetScissorRect, End() / Dispose()
ComputePass : IDisposable  SetPipeline, SetBindGroup, Dispatch(x, y, z), End()

2D: SpriteBatch : GraphicsResource
----------------------------------
  new SpriteBatch(context, initialQuadCapacity = 2048)   (or CreateSpriteBatch)
  Begin(RenderPass, Matrix3x2? transform = null, BlendPreset = AlphaBlend)
        pixel coordinates: origin top-left of the pass target, y down
  Draw(texture, RectangleF destination, Color4 tint)
  Draw(texture, RectangleF destination, RectangleF sourceTexels, Color4 tint)
  Draw(texture, Vector2 position, RectangleF source, Color4 tint,
       float rotation, Vector2 origin, Vector2 scale)
  FillRectangle, DrawRectangle(rect, color, thickness), DrawLine(a, b, color,
  thickness), FillTriangle, FillQuad, FillCircle(center, radius, color,
  segments = auto), DrawCircle(center, radius, color, thickness, segments)
  PushTransform(Matrix3x2) / PopTransform(), Transform
  UseNearestFiltering (default false)
  End()  uploads and records ONE DrawIndexed per run of the same texture;
         shapes never break a run (they reuse the current texture with a
         "flat colour" weight). LastDrawCallCount, LastQuadCount.
  Forget(GpuTexture)  drop a cached bind group early

3D
--
MeshVertex (48 bytes): Position, Normal, TexCoord, Color (Vector4)
MeshData(MeshVertex[], uint[] indices): GetBounds, Transformed(Matrix4x4),
  static Combine(parts)
MeshShapes: Cube(size, color?), Box(Vector3 size, color?), Plane(width,
  depth, color?, uvRepeat), Sphere(radius, segments, rings, color?)
  (counter-clockwise front faces, outward normals)
Mesh : GraphicsResource  VertexBuffer, IndexBuffer (32-bit), VertexCount,
  IndexCount, BoundsMin, BoundsMax
Camera: Position, Target, Up, Projection (Perspective / Orthographic),
  FieldOfView (radians, vertical), OrthographicHeight, NearPlane, FarPlane,
  ViewMatrix, Forward, GetProjectionMatrix(aspect),
  GetViewProjectionMatrix(aspect), WorldToScreen(world, w, h)
  Right-handed, y up, depth range 0..1.
OrbitCamera: Target, Yaw, Pitch (clamped), Distance (>= MinDistance),
  Position, Rotate(dYaw, dPitch), Zoom(factor), Apply(Camera),
  static OrbitPosition(target, distance, yaw, pitch)
SceneLighting: LightDirection (the direction the key light TRAVELS),
  LightColor, LightIntensity (0 = off), AdditionalLights (List of
  DirectionalLight(direction, color, intensity); model renderer only; up to
  SceneLighting.MaxDirectionalLights = 8 lights in total), Exposure (model
  renderer and skybox: scales everything they output).
  Model ambient: Ambient (AmbientLighting.Environment = DEFAULT: image-based
  light from Environment, or gpu.DefaultEnvironment (a procedural sky) when
  Environment is null; AmbientLighting.Hemisphere: the AmbientColor /
  AmbientGroundColor gradient around AmbientUp, no environment textures),
  Environment (EnvironmentMap, not owned), EnvironmentIntensity (1),
  EnvironmentRotation (radians about +Y; a positive angle turns the
  panorama's centre from -Z towards -X; per frame is free).
  AmbientColor (mesh renderer: flat ambient; model renderer: sky colour of
  the hemispheric ambient - Hemisphere mode only), AmbientGroundColor
  (Color4?, null = same as AmbientColor), AmbientUp (+Y).
  Units (model renderer): intensity 1 makes a white matte surface facing the
  light show white; an ambient grey g (or a uniform environment of radiance
  g) makes it show g.
MeshRenderer : GraphicsResource
  new MeshRenderer(context)  (or CreateMeshRenderer)
  CreateMaterial(Color4 baseColor, GpuTexture texture = null,
                 bool doubleSided = false, string label = null,
                 GpuSampler sampler = null)  -> LitMaterial
  Begin(RenderPass, Camera, SceneLighting = null)
  Draw(Mesh, LitMaterial, Matrix4x4 world)
  DrawInstanced(Mesh, LitMaterial, ReadOnlySpan<Matrix4x4> worlds)
  End()  opaque draws first, then translucent ones (alpha-blended, depth
         read-only); consecutive draws of the same mesh + material become one
         instanced DrawIndexed (split where the winding changes: a mirroring
         world matrix - negative determinant - draws with clockwise front
         faces). LastDrawCallCount, LastInstanceCount.
  Depth testing happens when the pass target has a depth view.
LitMaterial : GraphicsResource  BaseColor, Shininess, SpecularStrength,
  Unlit, DoubleSided, BaseColorTexture, Sampler,
  AlphaMode (MaterialAlphaMode: Auto (default: blend when BaseColor alpha < 1),
             Opaque (alpha written as 1), Mask (discard below AlphaCutoff,
             default 0.5), Blend (always blended)),
  TextureTransform (Matrix3x2 applied to the texture coordinate:
             Vector2.Transform(uv, m); default identity),
  IsTranslucent (Blend, or Auto with alpha < 1)
  Shading: Blinn-Phong - albedo * (ambient + diffuse * light) + specular.
Color4 (straight alpha floats): R, G, B, A, named colours, FromBytes,
  FromHex("#RRGGBB[AA]"), WithAlpha, Lerp, ToVector4, ToPackedRgba, *, ==

Images and textures (decoded by CodeBrix.Imaging)
-------------------------------------------------
GraphicsContext
  CreateTextureFromFile(path, TextureLoadOptions = null)
  CreateTextureFromStream(stream, options = null)
  CreateTextureFromEncoded(ReadOnlySpan<byte> fileBytes, options = null)
  CreateTextureFromImage(CodeBrix.Imaging.Image, options = null)
        format detected from the content: PNG, JPEG, JPEG XL, GIF, BMP, TGA,
        TIFF, PBM, still WebP (an animation gives its first frame). JPEG XL
        is decoded to sRGB: wide-gamut and linear-light files are converted,
        HDR (PQ / HLG) files are tone mapped for an SDR display, float
        values above 1 clip (for HDR light use CreateEnvironment, which keeps
        the range). Pixels become RGBA8 and are uploaded
        straight from the decoded memory (an Image<Rgba32> you pass uploads
        without a copy; other pixel types are converted). Your image is never
        modified or disposed. Undecodable input -> InvalidDataException.
        Usage: TextureBinding | CopyDst | CopySrc (+ RenderAttachment with
        mipmaps) | options.ExtraUsage.
  GenerateMipmaps(GpuTexture)   fills levels 1+ of every layer on the GPU
        (needs TextureBinding + RenderAttachment and a renderable, filterable
        colour format; sRGB formats filter in linear light); submits its own
        frame
  CreateTextureArray(w, h, layers, format, usage, mipLevelCount = 1, label)
        default View is a 2D-array view when layers > 1
TextureLoadOptions (static Default, static Data)
  Mipmaps (true), ColorSpace (TextureColorSpace.Srgb -> RGBA8UnormSrgb:
  sampling returns linear values; Linear -> RGBA8Unorm: normal maps,
  metallic-roughness, masks, or colours you sample "as stored"),
  PremultiplyAlpha (false; multiplies the stored 8-bit values), ExtraUsage,
  Label (default: the file name)
GpuTexture
  ToImage(mipLevel = 0, layer = 0) -> Image<Rgba32> (RGBA8/BGRA8 textures,
        sRGB included - the stored bytes; needs CopySrc; dispose the image)
  SaveAsPng(path), SaveAs(path) (format from the extension: .png .jpg .jpeg
        .jxl .bmp .gif .tga .tif .tiff .webp .pbm; .jxl is lossy at Imaging's
        default distance - for lossless: using var image = texture.ToImage();
        image.SaveAsJxl(path, new JxlEncoder { Lossless = true });),
        SaveAs(Stream, IImageFormat)
  CreateLayerView(layer, label = null) -> GpuTexture: a 2D view of one array
        layer (all mips) usable anywhere a 2D texture is (SpriteBatch,
        materials, BindingEntry.ForTexture); own reference; IsWrapped = true
  ViewLayer   the layer a layer view addresses: Write / WriteMipLevel /
        ReadPixels / ToImage use it; TextureRegion always addresses the whole
        texture
HeadlessRenderTarget: ToImage(), SaveAsPng(path), SaveAs(path)

Animated textures
-----------------
GraphicsContext
  CreateAnimatedTexture(path | Stream | Image, AnimatedTextureOptions = null)
        every frame of an animated GIF or JPEG XL (or any multi-frame Imaging
        image); GIF delays are honoured (0 or 1 hundredths -> 100 ms, as
        browsers do) and so are JPEG XL frame durations (ticks at the file's
        ticks-per-second rate; a zero duration -> DefaultFrameDuration).
        Animated WebP and APNG are not animated by CodeBrix.Imaging (see
        LIMITS)
  CreateAnimatedTexture(IReadOnlyList<Image> frames, options = null)
        equally sized frames (each image's first frame, copied)
AnimatedTextureOptions: LoopMode (AnimationLoopMode.Loop | PingPong | Once),
  ColorSpace (Srgb), Mipmaps (false), PremultiplyAlpha, DefaultFrameDuration
  (100 ms, for sources without timing), FrameDurations (TimeSpan[] override;
  a short array repeats its last value), MaxArrayBytes (256 MiB),
  Storage (null = choose; AnimatedTextureStorage.TextureArray | Streaming),
  StreamingSlots (3), Label
AnimatedTexture : GraphicsResource
  FrameCount, Width, Height, Format, Storage, ArrayTexture, LoopMode (settable),
  TotalDuration, GetFrameDuration(i)
  GetFrameIndex(TimeSpan elapsed)            no allocation
  GetFrameTexture(TimeSpan elapsed) -> GpuTexture (a 2D layer view owned by
        the animated texture; do not dispose it), GetFrameTextureAt(i)
  GetLayer(TimeSpan elapsed) -> layer of ArrayTexture (for your own shaders)
  Storage: TextureArray = one layer per frame, uploaded once (free to draw);
  chosen unless frames x w x h x 4 (x 4/3 with mips) > MaxArrayBytes or the
  frame count > the device's MaxTextureArrayLayers. Streaming = decoded frames
  kept in memory, the needed frame uploaded into the next of StreamingSlots
  ring layers when it is not resident (a queue write, no allocation; no
  mipmaps). Do not show more distinct frames than slots in one frame.
SpriteBatch.Draw(AnimatedTexture, TimeSpan elapsed, RectangleF dest, Color4 tint)
SpriteBatch.Draw(AnimatedTexture, TimeSpan elapsed, Vector2 position, Color4
  tint, float rotation, Vector2 origin, Vector2 scale)

Models (glTF 2.0, read by CodeBrix.Graphics3D.Gltf2)
---------------------------------------------------
GraphicsContext
  LoadModel(path, ModelLoadOptions = null)    .glb, or .gltf + its files
  LoadModel(Stream, baseDirectory = null, options = null)
        baseDirectory resolves a .gltf's external .bin / image files
  CreateModelRenderer()
  Errors: FileNotFoundException; InvalidDataException("The glTF model ...
  could not be read: <the reader's reason>"); GraphicsException from WebGPU.
ModelLoadOptions: LoadTextures (true), Mipmaps (true), ColorTexturesSrgb
  (true: base colour + emissive sRGB; data maps always linear),
  GenerateTangents (true), DecodeTexturesInParallel (true), Validation
  (ModelValidation.TryFix (default): a file that fails validation is read
  again WITHOUT validation and the reason becomes a Warning; Strict: fail;
  Skip: never validate), TextureCache (ModelTextureCache: external image
  files shared between models - e.g. a kit's colormap.png - decoded once;
  the cache owns them, dispose it after the models), Label
Model : GraphicsResource   immutable, shared by any number of instances
  Name, SourcePath, Warnings (what was skipped, approximated or not validated)
  VertexBuffer (ModelVertex, ALL primitives), IndexBuffer, IndexFormat
  (Uint16 when every primitive has <= 65536 vertices, else Uint32; 8-bit
  indices are widened), SkinBuffer (ModelSkinVertex, or null), VertexCount,
  IndexCount, TriangleCount, BoundsMin/BoundsMax/BoundsCenter (rest pose)
  Meshes (ModelMesh: Index, Name, Primitives, DefaultMorphWeights,
          MorphTargetCount)
  Materials (ModelMaterial), DefaultMaterial (Index -1)
  Nodes (ModelNode), RootNodes (the default scene, else the first)
  Skins (ModelSkin), Animations (ModelAnimation), Textures, Samplers
  CreateInstance() / CreateInstance(Matrix4x4 world), FindAnimation(name),
  FindNode(name)
ModelPrimitive: Material, FirstIndex, IndexCount, BaseVertex, VertexCount,
  SkinBaseVertex (-1 = not skinned), IsSkinned, BoundsMin/Max, HasVertexColors,
  HasTexCoord1, HasTangents, TangentsGenerated, NormalsGenerated, MorphTargets
  (ModelMorphTarget: PositionDeltas, NormalDeltas, TangentDeltas - one per
  vertex; the vertex buffer holds the shape WITH the mesh's default weights
  applied, so apply (weight - default) * delta), TriangleCount
  Draw it yourself: SetVertexBuffer(0, model.VertexBuffer);
  SetIndexBuffer(model.IndexBuffer, model.IndexFormat);
  DrawIndexed(p.IndexCount, n, p.FirstIndex, p.BaseVertex)
ModelVertex (64 bytes): Position (0), Normal (12), Tangent (24, w =
  handedness), TexCoord0 (40), TexCoord1 (48), ColorR/G/B/A (56, Unorm16x4;
  Color property as Vector4). ModelSkinVertex (24): Joint0..3 (Uint16x4),
  Weights (Vector4, normalised)
ModelMaterial: Index, Name, BaseColorFactor, BaseColorTexture, MetallicFactor,
  RoughnessFactor, MetallicRoughnessTexture, NormalTexture, NormalScale,
  OcclusionTexture, OcclusionStrength, EmissiveFactor, EmissiveStrength,
  EmissiveTexture, AlphaMode (MaterialAlphaMode Opaque/Mask/Blend),
  AlphaCutoff, DoubleSided, Unlit, IndexOfRefraction, ClearCoatFactor,
  ClearCoatRoughness, SpecularFactor, SpecularColor, TransmissionFactor,
  ApproximatedFeatures, IsTranslucent
ModelTextureSlot (struct): Texture (null when absent or undecodable),
  Sampler, TexCoord (0/1; the KHR_texture_transform texCoord override is
  applied), Transform (Matrix3x2, identity when none), HasTransform,
  TextureIndex, IsPresent
ModelNode: Index, Name, Parent (-1 root), Children, MeshIndex, SkinIndex,
  DefaultTransform (NodeTransform), HasFixedMatrix + LocalMatrix (a matrix that
  does not decompose into TRS), DefaultMorphWeights, InScene
NodeTransform (struct): Translation, Rotation, Scale, Identity, ToMatrix()
ModelSkin: Joints (node indices), InverseBindMatrices, Skeleton, JointCount
ModelAnimation: Index, Name, Duration (s; the last key time of any
  channel), Channels  (shared immutable clip data; play it per instance with
  ModelInstance.Animation - see "Animation playback")
ModelAnimationChannel: Node, Path (AnimationPath Translation/Rotation/Scale/
  Weights), Interpolation (AnimationInterpolation Step/Linear/CubicSpline),
  Times (s), Values (packed floats: ComponentCount per key, x3 for cubic
  spline = in-tangent, value, out-tangent), ComponentCount, KeyCount, EndTime,
  Sample(float time, Span<float> destination)  glTF evaluation, no allocation
ModelInstance   per-placement state; no GPU objects
  Model, World (Matrix4x4), Visible
  LocalTransforms -> ReadOnlySpan<NodeTransform> (read-only),
        GetLocalTransform(i), SetLocalTransform(i, in t)
  BeginPoseEdit() -> ModelPoseEdit (ref struct; dispose it - `using`):
        LocalTransforms (Span<NodeTransform>, writable), GetMorphWeights(node)
        (Span<float>, writable), Dispose() ends the edit and marks the pose
        changed. While an edit is open, NodeMatrices, GetNodeWorldMatrix,
        GetJointPalette, UpdateNodeMatrices, ModelRenderer.Draw and
        Animation.Update/Evaluate throw InvalidOperationException, so a write
        can never be missed. EndPoseEdit(), IsEditingPose.
  NodeMatrices -> ReadOnlySpan<Matrix4x4> node-to-model, recomputed when the
        pose changed; GetNodeWorldMatrix(i) = NodeMatrices[i] * World
  GetMorphWeights(node) -> ReadOnlySpan<float> (absolute weights; start at
        the node's default weights), SetMorphWeight(node, target, w),
        SetMorphWeights(node, ReadOnlySpan<float>), ResetPose(),
        UpdateNodeMatrices(),
  GetJointPalette(skin) -> ReadOnlySpan<Matrix4x4>: per joint
        InverseBindMatrices[j] * NodeMatrices[Joints[j]] (model space; times
        World for world space); cached until the pose changes, no allocation
  PoseVersion (increments on every pose change)
  Animation -> AnimationPlayer (created on first use), HasAnimationPlayer
ModelRenderer : GraphicsResource
  Begin(RenderPass, Camera, SceneLighting = null), Draw(ModelInstance), End()
  Instances of one model are batched: ONE instanced draw per primitive for
  all of them; opaque/mask first, blended after (depth read-only). No
  per-frame allocation once warm. LastDrawCallCount, LastInstanceCount,
  Forget(Model) (drop the renderer's materials for a disposed model).
  Shading: glTF 2.0 metallic-roughness (Lambert diffuse, GGX distribution,
  height-correlated Smith visibility, Schlick Fresnel), per material:
    base colour factor x texture (sRGB) x COLOR_0; metallic-roughness texture
    (B = metal, G = rough) x factors; normal map x scale (tangent frame from
    TANGENT or generated tangents; screen-space derivatives when a primitive
    has neither); occlusion x strength (applied to the ambient term);
    emissive factor x strength x texture; KHR_texture_transform and the
    texture-coordinate set per texture slot; KHR_materials_unlit (output =
    base colour x vertex colour); alpha OPAQUE (alpha written 1), MASK
    (discard below the cutoff) and BLEND (drawn after opaque draws, depth
    writes off, sorted back to front per instance and primitive by the view
    depth of the primitive's bounds centre); doubleSided (no culling, back
    faces shade with the flipped normal).
    KHR_materials_ior and KHR_materials_specular set the dielectric
    reflectance (factors only); KHR_materials_clearcoat adds an approximate
    coat lobe on the geometric normal; KHR_materials_transmission is not
    rendered (opaque dielectric). Other approximated extensions are ignored.
  Lighting: SceneLighting - up to 8 directional lights, Exposure, and an
    ambient term: image-based by default (diffuse = irradiance(N); specular
    = prefiltered(R, roughness) x (F0 x LUT.r + LUT.g), with multiple-
    scattering energy compensation - a white furnace stays white for any
    roughness or metalness; clear coat reflects the environment too) or the
    hemispheric gradient (Ambient = Hemisphere: diffuse from the normal's
    direction, specular from the reflection direction with an analytic
    environment-BRDF fit). Occlusion maps scale the ambient term. No shadows.
  Skinning (GPU): every skinned primitive of a node with a skin is skinned in
    the vertex shader from JOINTS_0/WEIGHTS_0 (Model.SkinBuffer) and the
    instance's joint palette (GetJointPalette); normals and tangents are
    skinned too; the skinned mesh node's own transform is ignored (glTF rule).
  Morph targets (GPU): position, normal and tangent deltas from a per-model
    storage buffer, weighted by the instance's GetMorphWeights(node) minus
    the mesh default weights already baked into the vertex buffer.
  Colour output: shading is linear. sRGB targets (RGBA8UnormSrgb,
    BGRA8UnormSrgb) encode in hardware; 8/10-bit UNORM targets (RGBA8Unorm,
    BGRA8Unorm, RGB10A2Unorm) get the same encoding from the shader, so both
    show the same picture; float targets (RGBA16Float ...) receive linear
    values. Blending on a UNORM target mixes encoded values.
  GPU data per End: one 80-byte record per instance plus its node matrices,
    joint palettes and morph weights, written to grow-only storage buffers
    read in the vertex shader (needs storage buffers in the vertex stage:
    Vulkan, Metal and Direct3D 12 devices have them). Pipelines are cached per
    (shader features, colour format, depth format); a model's materials,
    draw uniforms and morph deltas are created on its first draw (Forget
    drops them).
  Mirroring: a node-to-world matrix with a negative determinant (a mirroring
    node or instance transform; for skinned primitives the instance's World)
    draws with clockwise front faces, as glTF requires: a cached pipeline
    variant (no extra shader); instances whose World mirrors are batched after
    the others, so a batch costs at most one extra draw per primitive (more
    only when node-level mirroring alternates between instances). Normal-map
    bitangents follow the mirroring (vertex tangents and the derivative frame).

Animation playback (node animation; skinning and morph weights follow)
---------------------------------------------------------------------
Clips (Model.Animations) are shared, immutable model data; each instance has
its own AnimationPlayer (instance.Animation, created on first use).
AnimationPlayer
  Play(int clip | string name, AnimationLoopMode loopMode = Loop,
       float speed = 1, float startTime = 0, int layer = 0) -> AnimationState
       replaces every clip on that layer, weight 1
  CrossFade(clip | name, float duration, loopMode, speed, startTime, layer)
       -> the new state (weight 0 -> 1 over duration); the layer's other
       clips fade to 0 over the same time and stop
  Blend(clip | name, float weight, loopMode, speed, startTime, layer)
       -> adds a clip beside the others (weighted / layered blending)
  Update(float deltaSeconds)  advance playheads and fades, write the pose,
       raise ClipFinished. Allocation-free.
  Evaluate()  write the pose at the current playheads (after seeking, or
       right after Play - Play itself does not touch the pose)
  static UpdateAll(ReadOnlySpan<ModelInstance>, float deltaSeconds)  (skips
       instances that never used Animation; List: CollectionsMarshal.AsSpan)
  Stop() / Stop(int layer) / Stop(AnimationState)  the stopped clip's
       properties return to the rest pose
  Speed (scales all clips and fades), Paused, IsPlaying, StateCount,
  GetState(i), FindState(clipIndex), ClipFinished (Action<AnimationState>;
  called for each once-clip that reached its end in that Update; it may start
  other clips), Instance, Model, const MaxStates = 8
AnimationState  (pooled by the player and reused after it stops: keep a
  reference only while IsActive)
  Clip, ClipIndex, Name, Duration, IsActive, LoopMode, Speed (negative =
  backwards), Paused, Layer, Weight (setting it cancels a fade), IsFading,
  FadeTo(weight, duration, stopWhenZero = false), Time (playhead 0..Duration;
  set = seek: wraps for Loop/PingPong, clamps for Once), NormalizedTime,
  LoopCount, IsFinished (Once reached its end; holds the last frame),
  FinishedThisUpdate, LoopedThisUpdate, Stop()
AnimationLoopMode (shared with AnimatedTexture): Loop, PingPong (forward,
  then backward), Once (hold the last frame)
Interpolation (glTF 2.0 Appendix C): STEP holds each key until the next key
  time; LINEAR lerps, rotations slerp on the shorter arc; CUBICSPLINE is the
  Hermite spline with in/out tangents scaled by the key interval (rotations
  normalised). Before the first key = the first value; after the last key =
  the last value. Each playing state keeps a key cursor per channel (O(1)
  for forward playback, binary search after a jump).
Blending: per animated property (node translation, rotation, scale, morph
  weights) the states of one layer average by weight (rotations: weighted
  quaternion sum, hemisphere-corrected, normalised; weights above 1 in total
  are normalised). Layers apply in ascending order; each replaces what lies
  below (the rest pose, then lower layers) by its summed weight up to 1. So
  one clip fading in from 0 blends from the rest pose, a cross-fade keeps the
  weights summing to 1, and a higher layer overrides only the properties its
  clips animate. Properties no playing clip animates keep their values.
Cost (Release, this repo's timing test, CPU only): one archer clip update
  ~0.15-0.3 us per instance at 50-500 instances (sampling 7 channels and
  writing the pose); always cross-fading ~1 us per instance; node matrices +
  joint palettes are computed lazily by the renderer.

Environments, sky and tone mapping (image-based lighting)
---------------------------------------------------------
GraphicsContext
  CreateEnvironment(string path, EnvironmentOptions options = null)
        equirectangular (2:1) panorama: Radiance .hdr (linear HDR, detected
        by its #?RADIANCE / #?RGBE signature), JPEG XL (detected by its
        signature; float, linear-light, PQ, HLG and wide-gamut files are
        converted to linear sRGB with the HDR range kept - values above 1
        stay; 8/16-bit sRGB files give LDR light), or any other image
        CodeBrix.Imaging decodes (PNG, JPEG ...: taken as sRGB and linearised
        = LDR light).
        OpenEXR -> NotSupportedException; missing -> FileNotFoundException;
        undecodable -> InvalidDataException. Label = the file name.
  CreateEnvironment(Stream, options), CreateEnvironment(FloatImage, options)
  CreateEnvironment(CodeBrix.Imaging.Image, options)   Image<RgbaVector> /
        Image<HalfVector4> = linear floats; other pixel types = sRGB. GOTCHA:
        Image.Load<RgbaVector>("x.jxl") keeps the file's own transfer curve
        (Imaging writes float JPEG XL on the sRGB curve), so pass .jxl files
        by path or stream instead - those decode to linear light
  CreateProceduralSky(ProceduralSkyOptions sky = null, EnvironmentOptions options = null)
  DefaultEnvironment   procedural sky (ProceduralSkyOptions defaults, cube
        256), built on first use, owned by the context - do not dispose
  BrdfLut   128 x 128 RG16Float split-sum LUT (u = N.V, v = roughness;
        R = scale, G = bias of F0), rendered once per context, owned by it
  CreateSkyboxRenderer(), CreateToneMapper()
EnvironmentMap : GraphicsResource   (immutable; dispose when unused)
  Radiance (RGBA16Float cube view, full mips), Irradiance (cube view:
  irradiance / PI, so a white matte surface with normal n shows
  Irradiance(n)), Prefiltered (cube view; mip m = GGX roughness m /
  (PrefilteredMipLevels - 1)), Size, IrradianceSize, PrefilteredSize,
  PrefilteredMipLevels, IsProcedural. Cube layers +X, -X, +Y, -Y, +Z, -Z.
  Panorama mapping: image centre = -Z, top row = +Y, u grows towards +X.
EnvironmentOptions (all clamped / rounded to powers of two)
  CubeSize (512; 16..4096), IrradianceSize (32; 4..128),
  IrradianceSourceSize (32; the radiance mip integrated texel by texel),
  PrefilteredSize (0 = half the cube, max 256), PrefilteredMipLevels (6;
  smallest mip >= 4 x 4), SpecularSampleCount (128 GGX samples per texel,
  filtered importance sampling), MaxSourceWidth (8192: wider panoramas are
  halved on the CPU first), Label
ProceduralSkyOptions
  SunDirection (the direction sunlight TRAVELS, like LightDirection; the
  default equals SceneLighting's default key light), SunColor, SunIntensity
  (0: the disc adds light in directional-light units - 1 = a white matte
  surface facing it shows 1; leave 0 when a directional light plays the
  sun), SunAngularRadius (0.0175 rad), SunGlow (1), ZenithColor,
  HorizonColor, GroundColor (linear radiance), Turbidity (1 clear .. 10
  hazy; 2.5), Intensity (scales the sky, not SunIntensity), Up (+Y)
FloatImage(width, height[, float[] rgb])  linear RGB floats, row-major from
  the top: Width, Height, Pixels, GetPixel, SetPixel, static FromRgba(span, w, h)
RadianceHdrDecoder (static): DecodeFile(path), Decode(Stream),
  Decode(ReadOnlySpan<byte>) -> FloatImage; IsRadianceHdr(bytes). Flat,
  old-style and new-style RLE; "-Y h +X w" and "+Y h +X w"; EXPOSURE is not
  applied. Errors -> InvalidDataException.
SkyboxRenderer : GraphicsResource
  Draw(RenderPass, Camera, SceneLighting lighting = null)   the lighting's
        environment (or DefaultEnvironment) x EnvironmentIntensity, turned
        by EnvironmentRotation, x Exposure - the look the models are lit with
  Draw(RenderPass, Camera, EnvironmentMap, intensity = 1, rotation = 0, exposure = 1)
  Blur (0 sharp .. 1 = the roughest prefiltered mip), DrawCount
  A full-screen triangle at the far plane (inverse view-projection rays;
  perspective or orthographic); depth test LessEqual, no depth writes:
  draw it AFTER the opaque models in the same pass (cheapest) with depth
  cleared to 1; without a depth view it covers everything, so draw it first.
  Output like ModelRenderer: sRGB targets hardware, UNORM targets shader
  encoding, float targets linear. No allocation per draw once warm.
ToneMapper : GraphicsResource   (the HDR path's last pass)
  Operator (ToneMapOperator.AcesFitted (default; Hill's RRT+ODT fit),
  KhronosPbrNeutral (true base colours up to ~0.8; best for products),
  Reinhard, AcesNarkowicz, Clamp), Exposure (1; multiplies first)
  Apply(GraphicsFrame, GpuTexture hdrSource, RenderTargetView destination)
        its own pass, colour only; every pixel written; a source of another
        size is scaled (linear filter)
  Apply(RenderPass, GpuTexture hdrSource)   inside your pass (then draw a HUD)
  static Map(Vector3 linear, ToneMapOperator, exposure = 1) -> Vector3 0..1
        (the same curve on the CPU; encode to sRGB yourself for bytes)
  Source: a filterable float texture (RGBA16Float - e.g. the Color of a
  HeadlessRenderTarget created with TextureFormat.RGBA16Float).
When to use which path:
  LDR (default, simplest): draw models + skybox straight into the window /
    an sRGB or UNORM target; values above 1 clip. Fine for the default sky,
    product shots with moderate light, UI-like scenes.
  HDR: render into an RGBA16Float target (gpu.CreateHeadlessTarget(w, h,
    TextureFormat.RGBA16Float) or your own texture), then ToneMapper.Apply
    into the final target, then draw 2D overlays. Use it with HDRIs (bright
    windows, a sun), strong lights or emissive surfaces.
Build cost (this repo's timing test, Intel UHD iGPU, Vulkan): cube 512 or
  1024 with defaults ~30 ms; procedural sky 256 ~23 ms; BRDF LUT ~45 ms once
  per context (incl. pipeline creation); decoding a 1k .hdr ~5-10 ms.
  Per-frame model cost is the same with or without an environment (the
  frame bind group is cached per environment; rotation is a uniform).

glTF support (loading)
  Supported: .glb and .gltf (external, data-URI and GLB-embedded buffers and
  images); triangle lists, strips and fans; 8/16/32-bit indices and
  non-indexed primitives; POSITION, NORMAL (generated smooth when missing),
  TANGENT (generated for normal-mapped primitives without them: per-triangle
  UV derivatives averaged per vertex, Gram-Schmidt, MikkTSpace-style
  handedness - not bit-identical to MikkTSpace), TEXCOORD_0/1, COLOR_0
  (float or normalised), JOINTS_0/WEIGHTS_0; sparse accessors; morph targets
  (position/normal/tangent deltas, default weights); skins; node animation
  (translation/rotation/scale/weights; step, linear, cubic spline);
  metallic-roughness materials with every texture slot; KHR_texture_transform
  (incl. texCoord override); KHR_materials_unlit;
  KHR_materials_emissive_strength; KHR_materials_pbrSpecularGlossiness (as
  base colour, approximated); samplers (wrap per axis, nearest/linear,
  mipmap filters); textures shared by materials; PNG/JPEG/WebP/BMP/GIF images.
  Captured but approximated (listed in Warnings / ApproximatedFeatures):
  KHR_materials_clearcoat, _specular, _ior, _transmission, _sheen, _volume,
  _iridescence, _anisotropy, _diffuse_transmission.
  Skipped with a warning: points and lines; KHR_animation_pointer channels;
  TEXCOORD_2 and up (slots using them sample TEXCOORD_0); KTX2 / Basis / DDS
  images (the texture is left out).
  Not supported: FBX, OBJ, DAE, STL or any other format; EXR / HDR textures
  inside glTF files (HDR panoramas are environments, see above);
  KHR_draco_mesh_compression and EXT_meshopt_compression (a file requiring
  them fails with InvalidDataException); cameras and punctual lights
  (ignored); scenes other than the default one.

Presentation
------------
RenderTargetView (struct): ColorView, ColorFormat, Width, Height, DepthView,
  DepthFormat, HasDepth, AspectRatio, WithDepth(GpuTexture)
  new RenderTargetView(TextureView, TextureFormat, width, height) wraps ANY
  view - a texture of yours, another library's.
  RenderTargetView.From(IWebGpuTextureTarget, GpuTexture depth = null) - the
  view of a shared texture target (checks depth size and sample count).
HeadlessRenderTarget : GraphicsResource
  Color, Depth, Width, Height, Format, View, ReadPixels(...), ReadRgba(),
  ReadDepth() (Depth32Float)
NativeWindowSource: FromXlib(display, windowId), FromXcb(connection, window),
  FromWayland(display, surface), FromWin32(hinstance, hwnd),
  FromMetalLayer(layer), FromAndroid(nativeWindow); Kind, Display, Window,
  WindowId
WindowSurface : GraphicsResource
  SupportedFormats, SupportedPresentModes, IsConfigured, Format, PresentMode,
  Width, Height, DepthTexture, HasFrame
  Configure(w, h, PresentMode = Fifo, preferredFormat = Undefined,
            withDepth = true, depthTextureFormat = Depth32Float)
        format: the preferred one if supported, else BGRA8Unorm, else
        RGBA8Unorm, else the first supported; present mode falls back to Fifo
  Resize(w, h)   0 x 0 unconfigures (minimised); TryAcquire then skips
  TryAcquire(out RenderTargetView) -> Ready / Skipped (out of date or lost:
        reconfigured for you; timeout; minimised)
  Present()

Errors
------
GraphicsException (message carries WebGPU's validation text)
ShaderCompilationException : GraphicsException (Label, Diagnostics)
GraphicsError (Type, Message) - uncaptured errors


ADVANCED API REFERENCE
======================

Everything below is optional; it covers the rest of WebGPU in the same style.
Methods that need a device feature check it and throw
InvalidOperationException naming the feature when it is missing; ask for
features with GraphicsContextOptions.

Capabilities, adapters, limits
------------------------------
GraphicsContext
  Capabilities          GraphicsCapabilities of the DEVICE (cached)
  AdapterCapabilities   of the adapter (null without one)
  AdapterInfo           GraphicsAdapterInfo of the adapter (null without one)
  HasFeature(FeatureName / NativeFeature)          device
  AdapterHasFeature(FeatureName / NativeFeature)   adapter
  TimestampPeriod       nanoseconds per timestamp tick
  static EnumerateAdapters(GraphicsBackend = Default) -> IReadOnlyList<GraphicsAdapterInfo>
GraphicsCapabilities: Features (standard), NativeFeatures (engine), Limits,
  Has(FeatureName), Has(NativeFeature),
  static ExperimentalNativeFeatures (never enabled by this engine)
GraphicsAdapterInfo: Index, Name, Vendor, Architecture, Description, Backend,
  AdapterType, VendorId, DeviceId, Capabilities
GraphicsLimits.Undefined() -> Limits with every field "undefined"

  GraphicsContextOptions options = new GraphicsContextOptions
  {
      RequiredFeatures = { FeatureName.TimestampQuery },
      RequiredNativeFeatures = { NativeFeature.Immediates },
      RequestAdapterLimits = true,
  };

Queue and synchronisation
-------------------------
  GraphicsFrame.SubmissionIndex      set by Submit (0 for borrowed encoders)
  GraphicsFrame.Finish(label)        -> GpuCommandBuffer (validated, not submitted)
  GraphicsContext.Submit(params GpuCommandBuffer[]) / Submit(ReadOnlySpan<...>)
                                     -> submission index; each buffer runs once
  GraphicsContext.WaitForSubmission(index)   waits for that submission only
  GraphicsContext.WhenSubmittedWorkDoneAsync() -> Task (the device is polled
        on a background thread; continuations run on the thread pool)
  GraphicsContext.DestroyDevice()    frees the device now; IsLost becomes true
  GraphicsContext.GenerateReport()   -> GlobalReport (live engine objects)
  GraphicsContext.StartGraphicsDebuggerCapture() / StopGraphicsDebuggerCapture()

Buffer mapping and creation
---------------------------
  CreateMappedBuffer(size, usage)       starts mapped for writing
  CreateInitializedBuffer<T>(span, usage)  data written while mapped
  CreateIndirectBuffer<T>(span) / CreateIndirectBuffer(size)  Indirect + Storage
GpuBuffer
  MapState (tracked by the wrapper), MappedMode, MappedOffset, MappedSize
  Map(MapMode, offset = 0, size = 0 (rest))     waits; offset multiple of 8
  MapAsync(MapMode, offset, size) -> Task       polled in the background
  GetMappedRange(byteOffset, byteCount = -1) -> Span<byte> (native memory)
  GetMappedSpan<T>() / GetMappedSpan<T>(byteOffset, count = -1) -> Span<T>
  ReadMapped<T>(Span<T>, byteOffset), WriteMapped<T>(ReadOnlySpan<T>, byteOffset)
  Unmap()      spans become invalid
  Destroy()    frees GPU memory now and disposes the wrapper
  IsWrapped
  Write/Read throw InvalidOperationException while the buffer is mapped.

Textures: regions, copies, clears, uploads
------------------------------------------
TextureRegion(x, y, width, height, mipLevel = 0, arrayLayer = 0, layers = 1,
  aspect = All); TextureRegion.Whole(texture, mipLevel)
TextureCopyLayout: RowAlignment (256), TightBytesPerRow, AlignedBytesPerRow,
  BufferSize, PackRows, UnpackRows
GpuTexture
  Dimension, DepthOrArrayLayers, SampleCount (read from the native texture)
  WriteRegion(region, tightTexels)   queue write
  ReadRegion(region, Span<byte>)     waits for the GPU
  CopyTo(GpuTexture)                 GPU copy of every common mip level, submitted
  Destroy()
GraphicsFrame
  CopyBufferToTexture(buffer, offset, bytesPerRow, texture, region)
  CopyBufferToTexture(buffer, texture, mipLevel = 0, offset = 0)
  CopyTextureToBuffer(texture, region, buffer, offset, bytesPerRow)
  CopyTextureToBuffer(texture, buffer, mipLevel = 0, offset = 0)
  CopyTextureToTexture(source, region, destination, mip, x, y, layer = 0)
  CopyTextureToTexture(source, destination, mipLevel = 0)
  UploadTexture(texture, region, tightTexels)   staged in the frame, ordered
                                                with its commands
  ClearBuffer(buffer, offset = 0, size = 0 (rest))
  ClearTexture(texture, ...)     needs NativeFeature.ClearTexture

Pass options
------------
  BeginRenderPass(target, clearColor, clearDepth, in RenderPassOptions)
    RenderPassOptions (struct; reuse it): Label, Timestamps (PassTimestamps),
    OcclusionQuerySet, StencilClearValue, LoadStencil, DepthReadOnly,
    StencilReadOnly, ResolveTarget (multisample resolve), DiscardColor
  BeginComputePass(in ComputePassOptions): Label, Timestamps
  PassTimestamps(querySet, beginningIndex, endIndex); NotWritten

Queries and GPU timing
----------------------
  CreateOcclusionQuerySet(count), CreateTimestampQuerySet(count)
  CreateQuerySet(QueryType, count)
  CreatePipelineStatisticsQuerySet(count, [label,] params PipelineStatisticName[])
                       needs NativeFeature.PipelineStatisticsQuery; one 64-bit
                       value per statistic, ascending statistic order
  CreateQueryResolveBuffer(querySet)   QueryResolve buffer for every query
GpuQuerySet: Handle, Type, IsPipelineStatistics, Count, Statistics,
  ValuesPerQuery, ResolvedSize, IsWrapped
GraphicsFrame: ResolveQuerySet(set, first, count, buffer, offset = 0),
  WriteTimestamp(set, index)    needs NativeFeature.TimestampQueryInsideEncoders
RenderPass: BeginOcclusionQuery(index) / EndOcclusionQuery(),
  BeginPipelineStatisticsQuery(set, index) / EndPipelineStatisticsQuery(),
  WriteTimestamp(set, index)    needs NativeFeature.TimestampQueryInsidePasses
ComputePass: the same statistics and timestamp methods.

GpuTimer (CreateTimer(capacity = 16); needs FeatureName.TimestampQuery)
  Pass(slot) -> PassTimestamps  measure a whole pass
  WriteStart / WriteEnd(GraphicsFrame | RenderPass | ComputePass, slot)
  Resolve(frame)                once per frame, outside passes, then submit
  TryReadMilliseconds(Span<double>) (no allocation) / ReadMilliseconds()
  GetLastTicks(), QuerySet, Capacity

  using GpuTimer timer = gpu.CreateTimer(2);
  RenderPassOptions timed = new RenderPassOptions { Timestamps = timer.Pass(0) };
  using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black, 1f, in timed)) { ... }
  timer.Resolve(frame);
  frame.Submit();
  double[] ms = timer.ReadMilliseconds();

Indirect drawing
----------------
Structs with the GPU layout: DrawIndirectArgs (16 bytes),
DrawIndexedIndirectArgs (20), DispatchIndirectArgs (12); fill them from C# or
from a compute shader (indirect buffers are storage buffers too).
RenderPass
  DrawIndirect(buffer, offset = 0), DrawIndexedIndirect(buffer, offset = 0)
  MultiDrawIndirect(buffer, offset, count), MultiDrawIndexedIndirect(...)
  MultiDrawIndirectCount(buffer, offset, countBuffer, countOffset, maxCount)
  MultiDrawIndexedIndirectCount(...)   need NativeFeature.MultiDrawIndirectCount
ComputePass
  DispatchIndirect(buffer, offset = 0)
  SetBindGroup(index, group, ReadOnlySpan<uint> dynamicOffsets)

Render bundles
--------------
  CreateRenderBundle(RenderTargetView target, sampleCount = 1, label)
  CreateRenderBundle(GpuRenderPipeline pipeline, label)   formats of the pipeline
  CreateRenderBundle(ReadOnlySpan<TextureFormat> colors, depthFormat, sampleCount,
                     depthReadOnly, stencilReadOnly, label)
  WrapRenderBundleEncoder(encoder, colors, depthFormat, ...)
RenderBundleBuilder : IDisposable  (mirrors RenderPass)
  SetPipeline, SetBindGroup(index, group [, offsets]), SetVertexBuffer,
  SetIndexBuffer, Draw, DrawIndexed, DrawIndirect, DrawIndexedIndirect,
  SetImmediates(bytes | in T), PushDebugGroup / PopDebugGroup /
  InsertDebugMarker (accepted; the engine drops them inside bundles),
  CommandCount, IsFinished, Finish(label) -> GpuRenderBundle
  Finish validates the commands first (a rehearsal in a throwaway pass) and
  throws GraphicsException with WebGPU's message for an invalid bundle.
GpuRenderBundle: Handle, ColorFormats, DepthFormat, SampleCount, IsWrapped
RenderPass: ExecuteBundle(bundle), ExecuteBundles(params / ReadOnlySpan)
  (state set before is not inherited and is unset afterwards)

Immediates (push constants)
---------------------------
Need NativeFeature.Immediates AND a device MaxImmediateSize (RequestAdapterLimits
or RequiredLimits). WGSL: `var<immediate> data: MyStruct;`
  RenderPipelineBuilder / ComputePipelineBuilder:
    WithImmediateSize(bytes) / WithImmediates<T>()   (builds an explicit layout
    from WithBindGroupLayouts, none when not given), WithPipelineLayout(layout)
  CreatePipelineLayout(immediateSize, label, params GpuBindGroupLayout[])
    -> GpuPipelineLayout (Handle, BindGroupLayouts, ImmediateSize)
  RenderPass / ComputePass / RenderBundleBuilder:
    SetImmediates<T>(in T value, offset = 0), SetImmediates(ReadOnlySpan<byte>, offset)
    (offset and size multiples of 4)
  GpuRenderPipeline.ImmediateSize, SampleCount; GpuComputePipeline.ImmediateSize

More pipeline state
-------------------
  RenderPipelineBuilder.WithColorTarget(format, BlendState, writeMask)
  RenderPipelineBuilder.WithStencil(front, back, readMask, writeMask)
  RenderPass.SetBlendConstant(Color4), RenderPass.SetStencilReference(uint)

Debug groups
------------
GraphicsFrame, RenderPass, ComputePass: PushDebugGroup(label), PopDebugGroup(),
InsertDebugMarker(label), DebugGroup(label) -> DebugGroupScope (a struct:
`using (frame.DebugGroup("shadows")) { ... }`). Labels are encoded on the
stack; no per-call allocation.

More shaders
------------
  CreateShaderFromSpirV(ReadOnlySpan<uint>, label)    validated and translated
  CreateShaderFromSpirVPassthrough(words, label)      driver passthrough; the
        engine this package ships does not expose the passthrough feature, so
        it throws ShaderCompilationException with the engine's message
  CreateShaderTrusted(wgsl, ShaderRuntimeChecks, label)  choose runtime checks
  CreateShaderFromGlsl(glsl, ShaderStage, label, defines)  one stage, entry main
GpuShader.Language (ShaderLanguage: Wgsl, SpirV, SpirVPassthrough, Glsl,
  Unknown), IsWrapped

Wrapping raw handles
--------------------
Every wrapper takes its OWN reference (dispose order does not matter):
  WrapBuffer(Buffer)            size and usage read from the native buffer
  WrapTexture(Texture)          shape read from the native texture; own view
  WrapTexture(Texture, TextureView, format, w, h)
  WrapSampler, WrapShader, WrapBindGroupLayout, WrapBindGroup(group, layout),
  WrapPipelineLayout, WrapRenderPipeline(pipeline, colorFormats, depth,
  samples), WrapComputePipeline, WrapQuerySet (type and count read back),
  WrapRenderBundle, WrapCommandBuffer, WrapWindowSurface(Surface)
  GraphicsFrame.WrapRenderPass(encoder, target) / WrapComputePass(encoder):
        record into a pass someone else began; End() releases only the
        wrapper's reference (the owner ends the pass)
  GraphicsContext.BeginFrame(CommandEncoder) also takes its own reference.

Native device interop
---------------------
  TryGetVulkanInfo(out VulkanDeviceInfo)   instance, physical device, device,
        queue, family/index, API version, vkGetInstanceProcAddr, extensions
  TryGetD3D12Info(out D3D12DeviceInfo)     adapter, device, queue
  MetalDevice (IntPtr), MetalCommandQueue (always zero on this engine)
  static AdoptVulkanDevice(VulkanDeviceInfo, requiredFeatures) /
         TryAdoptVulkanDevice(...)     a context over the APPLICATION'S VkDevice
  static AdoptMetalDevice(IntPtr mtlDevice, ...) / TryAdoptMetalDevice(...)
  static TryAdoptD3D12Device(...)      always false on this engine (see LIMITS)
  IsAdopted
  CreateTextureFromVulkanImage / CreateTextureFromD3D12Resource /
  CreateTextureFromMetalTexture(handle, w, h, format, usage, mips, samples,
        [initialState,] label) -> GpuTexture with IsExternal = true
  GpuTexture: TryGetVulkanInfo(out VulkanTextureInfo), TryGetD3D12Info(out
        D3D12TextureInfo), MetalTexture
  GraphicsFrame.TransitionTexture(texture, CodebrixTextureState) -> bool

Diagnostics (static GraphicsDiagnostics)
----------------------------------------
  EngineVersion (Version), NativeApiVersion, BuildInfo (GraphicsBuildInfo:
  EngineVersion, CoreVersion, Backends, TargetTriple, Profile, ApiVersion),
  TimedWaitAnyMaxCount (always 0), LogLevel (get/set),
  SetLogCallback(Action<LogLevel, string> or null)  any thread; exceptions
  swallowed, TryGetNativeStructLayout(name, out size, out alignment)


THE FRAME MODEL
===============

  1. frame = context.BeginFrame()            (or BeginFrame(encoder))
  2. pass  = frame.BeginRenderPass(target, clearColor, clearDepth)
  3. record: raw pass calls, sprite batches, mesh renderers - any mix, any
     number of Begin/End pairs per pass and passes per frame
  4. pass.End() (or dispose)
  5. frame.Submit()

Queue writes (buffer and texture uploads, the batches' vertex and uniform
data) land before the NEXT submit, ahead of the commands recorded in the
frame. The sprite batch and mesh renderer therefore keep every Begin/End's
data in its own region of their buffers, reset when a frame with a new Id
starts. Use one GraphicsFrame per encoder, and submit a frame before starting
the next one.


SHARING A DEVICE WITH CODEBRIX.WEBGPU.SKIA
==========================================

The Graphics package has no SkiaSharp types and does not reference
CodeBrix.WebGpu.Skia (nor does Skia reference Graphics). Both build on
CodeBrix.WebGpu.Native, which defines two small sharing contracts:

  IWebGpuDeviceSource   Instance, Adapter, Device, Queue, Backend,
                        HasUncapturedErrorHandler
                        (WebGpuSkiaDevice and GraphicsContext implement it -
                        so a context can be passed wherever a device source is
                        taken, e.g. CodebrixDeviceHandoff for native modules)
  IWebGpuTextureTarget  Texture, TextureView, Format, Width, Height,
                        SampleCount, Usage, IsInWebGpuPass,
                        BeginWebGpuPass(), EndWebGpuPass()
                        (WebGpuSkiaTarget implements it)

Graphics accepts them - one call each:

  GraphicsContext.Wrap(IWebGpuDeviceSource)
        same as Wrap(Device, Queue, Instance, Adapter) with the source's
        handles: own references, dispose in either order, no callbacks;
        HasUncapturedErrorHandler follows the source
  context.BeginFrame(IWebGpuTextureTarget, GpuTexture depth = null)
        opens the target's WebGPU pass and records into its encoder;
        frame.Target = the target's view with the depth attached;
        frame.Submit() ends the target's pass (the target submits);
        disposing an unsubmitted frame ends it too
  frame.BeginRenderPass(Color4? clear, float? clearDepth = 1f)
        a pass into frame.Target (frames started on a texture target)
  RenderTargetView.From(IWebGpuTextureTarget, GpuTexture depth = null)
        the target's view (raw handles, no references taken)
  context.WrapTexture(IWebGpuTextureTarget, label)
        sample / copy the target's texture (own references)
  frame.TextureTarget, context.HasUncapturedErrorHandler

  WebGpuSkiaDevice skia = WebGpuSkiaDevice.Create();
  GRContext grContext = skia.CreateGrContext();
  GraphicsContext gpu = GraphicsContext.Wrap(skia);
  WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(skia, grContext, w, h);
  GpuTexture depth = gpu.CreateDepthTexture(w, h);

  using (GraphicsFrame frame = gpu.BeginFrame(target, depth))
  {
      using (RenderPass pass = frame.BeginRenderPass(Color4.Black)) { ... }
      frame.Submit();                 // ends the target's pass
  }
  // SkiaSharp may now draw on target.Surface.Canvas, on the same pixels.

Rules of the texture target:
  * EVERY use of the target's texture (render into it, copy it, sample it)
    happens inside one of its passes - i.e. inside a frame from
    BeginFrame(target). Between passes the texture belongs to SkiaSharp.
  * To draw the target onto a window surface (one full-screen sprite), wrap
    it once (gpu.WrapTexture(target)) and render into the surface's view
    inside a BeginFrame(target) frame:
        using (GraphicsFrame frame = gpu.BeginFrame(target))
        using (RenderPass pass = frame.BeginRenderPass(surfaceView, Color4.Black, null)) ...
  * A new size means a new target (and a new depth texture and wrapper).
  * The target owns, finishes and submits the encoder, so validating it is
    the target's job. WebGpuSkiaTarget validates before submitting: invalid
    commands make frame.Submit() throw the target's
    WebGpuSkiaValidationException (nothing submitted; the target takes the
    next frame). Another IWebGpuTextureTarget implementation may not check.

The Skia device installs the uncaptured-error callback for the shared device
(HasUncapturedErrorHandler is true for WebGpuSkiaDevice.Create): read errors
with skia.TakeUncapturedErrors(); the wrapped Graphics context receives none.
A device the Skia package ADOPTED reports false: uncaptured errors are fatal.

The raw-handle path still works: Wrap(Device, Queue, Instance, Adapter),
BeginFrame(target.BeginWebGpuPass()) ... target.EndWebGpuPass().


NATIVE MODULES
==============

Native code (Rust, C, C++) can work on the context's device and record into
a frame: CodeBrix.WebGpu.Native ships the C headers, a function table and two
versioned hand-off structs for it (its AGENT-README, NATIVE MODULES, has the
linking rules per operating system and the load-order rule).

  * The device: hand a module the context's raw handles (gpu.Instance,
    gpu.Adapter, gpu.Device, gpu.Queue - each `.Handle`), or, when the device
    came from an IWebGpuDeviceSource (GraphicsContext.Wrap(source)), fill
    the hand-off from that source: CodebrixDeviceHandoff.Fill(source, p).
    The context keeps owning the handles; a module that keeps one longer
    takes its own reference (*AddRef).
  * A frame: between passes, frame.Encoder is the encoder a module records
    into (no render or compute pass of yours may be open). On a texture
    target frame, CodebrixFrameHandoff.Fill(target, frame.Encoder, p)
    describes the target and that encoder in one struct.
  * Uncaptured errors: gpu.HasUncapturedErrorHandler false (adopted devices,
    sources without a handler) means a module's uncaptured error ends the
    process - the module wraps object creation in error scopes.
  * Validation happens where the frame is finished: frame.Submit() throws
    GraphicsException for invalid module commands too (texture target
    frames: the target's own check).


COMPLETE EXAMPLES
=================

Headless lit cube:

  using System.Numerics;
  using CodeBrix.WebGpu.Graphics;

  using GraphicsContext gpu = GraphicsContext.Create();
  using HeadlessRenderTarget target = gpu.CreateHeadlessTarget(256, 256);
  using Mesh cube = gpu.CreateMesh(MeshShapes.Cube(2f));
  using MeshRenderer meshes = new MeshRenderer(gpu);
  LitMaterial red = meshes.CreateMaterial(Color4.Red);
  Camera camera = new Camera { Position = new Vector3(3, 2, 4) };
  using (GraphicsFrame frame = gpu.BeginFrame())
  {
      using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
      {
          meshes.Begin(pass, camera, new SceneLighting());
          meshes.Draw(cube, red, Matrix4x4.Identity);
          meshes.End();
      }
      frame.Submit();
  }
  byte[] rgba = target.ReadRgba();

Many instances in one draw:

  Matrix4x4[] worlds = new Matrix4x4[1000];
  for (int i = 0; i < worlds.Length; i++)
      worlds[i] = Matrix4x4.CreateTranslation(i % 32, 0, i / 32);
  meshes.DrawInstanced(cube, red, worlds);       // between Begin and End

Animated glTF instances (each plays its own clip; the clips are shared):

  using Model archer = gpu.LoadModel("character-archer.glb");
  using ModelRenderer models = gpu.CreateModelRenderer();
  ModelInstance[] crowd = new ModelInstance[500];
  for (int i = 0; i < crowd.Length; i++)
  {
      crowd[i] = archer.CreateInstance(Matrix4x4.CreateTranslation(i % 25, 0, i / 25));
      crowd[i].Animation.Play(i % 2 == 0 ? "walk" : "idle", AnimationLoopMode.Loop, 1f, i * 0.1f);
  }
  crowd[0].Animation.ClipFinished = state => state.Player.Play("idle");   // cache delegates
  // later: crowd[0].Animation.CrossFade("attack-melee-right", 0.2f, AnimationLoopMode.Once);
  // per frame:
  AnimationPlayer.UpdateAll(crowd, deltaSeconds);
  models.Begin(pass, camera, lighting);
  foreach (ModelInstance archerInstance in crowd) models.Draw(archerInstance);
  models.End();                                // 2 instanced draws for all 500

Image-based lighting with a sky background, HDR path:

  using EnvironmentMap hall = gpu.CreateEnvironment("small_cathedral_1k.hdr");
  using SkyboxRenderer sky = gpu.CreateSkyboxRenderer();
  using ToneMapper toneMapper = gpu.CreateToneMapper();
  toneMapper.Operator = ToneMapOperator.KhronosPbrNeutral;
  using HeadlessRenderTarget hdr = gpu.CreateHeadlessTarget(w, h, TextureFormat.RGBA16Float);
  SceneLighting lighting = new SceneLighting { Environment = hall, LightIntensity = 0f };
  using (GraphicsFrame frame = gpu.BeginFrame())
  {
      using (RenderPass pass = frame.BeginRenderPass(hdr.View, Color4.Black))
      {
          models.Begin(pass, camera, lighting);
          models.Draw(instance);
          models.End();
          sky.Draw(pass, camera, lighting);          // after the opaque models
      }
      toneMapper.Apply(frame, hdr.Color, finalView); // then HUDs into finalView
      frame.Submit();
  }
  // no HDRI: leave Environment null - the procedural default sky lights the
  // models; your own sky: gpu.CreateProceduralSky(new ProceduralSkyOptions
  // { SunDirection = lighting.LightDirection, Turbidity = 4f })

Compute: double every element of a storage buffer:

  const string Wgsl = @"
  @group(0) @binding(0) var<storage, read_write> values: array<u32>;
  @compute @workgroup_size(64) fn main(@builtin(global_invocation_id) id: vec3<u32>) {
      if (id.x < arrayLength(&values)) { values[id.x] = values[id.x] * 2u; }
  }";
  using GpuShader shader = gpu.CreateShader(Wgsl);
  using GpuComputePipeline pipeline = gpu.CreateComputePipeline(shader).Build();
  using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
  using GpuBuffer data = gpu.CreateStorageBuffer<uint>(new uint[] { 1, 2, 3 });
  using GpuBindGroup group = gpu.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));
  using (GraphicsFrame frame = gpu.BeginFrame())
  {
      using (ComputePass pass = frame.BeginComputePass())
      {
          pass.SetPipeline(pipeline);
          pass.SetBindGroup(0, group);
          pass.Dispatch(1);
      }
      frame.Submit();
  }
  uint[] doubled = data.Read<uint>();            // 2, 4, 6

A window from any toolkit (X11 shown; Wayland, Win32, Metal, Android alike):

  NativeWindowSource source = NativeWindowSource.FromXlib(display, windowId);
  using GraphicsContext gpu = GraphicsContext.Create(new GraphicsContextOptions { CompatibleWindow = source });
  using WindowSurface surface = gpu.CreateWindowSurface(source);
  surface.Configure(pixelWidth, pixelHeight);
  // per frame:
  if (surface.TryAcquire(out RenderTargetView view) == SurfaceAcquireResult.Ready)
  {
      using (GraphicsFrame frame = gpu.BeginFrame())
      {
          using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black)) { ... }
          frame.Submit();
      }
      surface.Present();
  }
  // on a window size change: surface.Resize(newPixelWidth, newPixelHeight);

  On macOS give the surface a CAMetalLayer (a layer-backed view's Metal
  layer); on Windows the HWND and the module's HINSTANCE; on Wayland the
  wl_display and wl_surface; on Android the ANativeWindow.


PER-PLATFORM BACKENDS
=====================

  Linux, Android    Vulkan
  Windows           Direct3D 12 or Vulkan (GraphicsBackend.Default lets the
                    platform choose; ask for one explicitly with the option)
  macOS             Metal
  GraphicsBackend.OpenGL where the platform's native library carries it.


LIMITS
======

  * Creating or configuring a window surface with handles that do not belong
    together is FATAL in the native library (the process ends). The library
    checks sizes, formats and present modes against the surface's
    capabilities first; it cannot check the handles themselves.
  * A device whose owner installed no uncaptured-error callback ends the
    process on an uncaptured validation error (HasUncapturedErrorHandler is
    false). Devices created by GraphicsContext.Create and by
    WebGpuSkiaDevice.Create always have one; object creation through this
    library always runs inside an error scope.
  * A frame on a shared texture target (BeginFrame(IWebGpuTextureTarget)) or
    on any borrowed encoder is submitted by the encoder's owner, so only the
    owner can check it. WebGpuSkiaTarget does (frame.Submit() rethrows its
    WebGpuSkiaValidationException; nothing is submitted); an owner that
    submits unchecked ends the process on invalid commands.
  * GpuTexture.Write / CreateTextureFromRgba generate mipmaps on the CPU, for
    RGBA8 / BGRA8 only (a box filter on the stored bytes); image loading and
    GraphicsContext.GenerateMipmaps generate them on the GPU (sRGB-correct).
  * Images are decoded to 8 bits per channel (16-bit PNG/TIFF lose precision).
    Animated formats: GIF and JPEG XL. CodeBrix.Imaging decodes APNG as its
    first frame and refuses animated WebP (InvalidDataException).
  * MeshRenderer lighting is computed in the target's own colour space (no
    sRGB conversion unless you choose sRGB formats yourself). ModelRenderer
    shades in linear space and encodes for 8/10-bit UNORM targets itself.
  * Not thread-safe: one context per thread. Task-returning members
    (WhenSubmittedWorkDoneAsync, MapAsync) complete on the thread pool.
  * The native engine ends the process on some errors no error scope can
    catch; the library checks them first:
      - submitting invalid commands: GraphicsFrame.Submit / Finish validate
        and throw GraphicsException instead;
      - finishing an invalid render bundle: RenderBundleBuilder.Finish
        rehearses the commands in a throwaway pass and throws instead;
      - a bad mapped-range request: GpuBuffer checks state, alignment and
        range first;
      - creating a bundle encoder with impossible formats or sample counts:
        checked by CreateRenderBundle.
    Still fatal (cannot be checked): submitting work that uses a buffer while
    it is mapped, or a destroyed buffer or texture (Destroy) - finish such
    work, then destroy.
  * A context that adopted a native device (IsAdopted) has no uncaptured-error
    handler: object creation is still checked, but any other uncaptured error
    ends the process.
  * Not available on the engine this package ships (calling them in the raw
    binding ends the process), so the library offers no wrapper:
      - setting a label after creation (every *SetLabel function): labels go
        through the creation methods' label parameters;
      - shader compilation info (diagnostics come from the creation methods'
        ShaderCompilationException instead);
      - asynchronous pipeline creation (pipelines are built synchronously);
      - waiting on futures (WaitAny) and the device-lost future: use
        WhenSubmittedWorkDoneAsync, MapAsync, WaitForSubmission and the
        DeviceLost event;
      - the buffer map-state query and read/write of a mapped range by copy:
        GpuBuffer tracks MapState itself and offers ReadMapped / WriteMapped;
      - instance features and WGSL language-feature queries;
      - the device's adapter info (use AdapterInfo, read from the adapter);
      - the texture-binding view dimension query;
      - external textures (no creation path).
  * Destroying a query set early is not offered: on this engine a destroyed
    query set ends the process when it is released. Dispose it instead.
  * Adopting an existing Direct3D 12 device is impossible on this engine:
    TryAdoptD3D12Device always returns false with the reason. Create the
    context on Direct3D 12 and share it the other way (TryGetD3D12Info).
  * Experimental native features (GraphicsCapabilities.ExperimentalNativeFeatures)
    can never be enabled; requiring one fails the device request.
  * Wrapped textures created over native images start "undefined" on first
    use; the engine tracks initialisation per texture object, so a read of a
    texture object that was never written returns zeros.
  * Debug groups and markers inside render bundles are dropped by the engine.
  * The Metal command queue is not exposed by the engine (MetalCommandQueue
    is always zero).
  * Environments: OpenEXR is not read (convert to Radiance .hdr); panorama
    values above 65504 are clamped (RGBA16Float); building one takes tens
    of milliseconds of GPU work (build at load time, not per frame); the
    environment is static (no per-frame capture of the scene, no
    reflection probes or parallax correction). The ToneMapper samples its
    source with a filtering sampler, so RGBA32Float sources (not filterable
    without a device feature) are refused by WebGPU - use RGBA16Float.


COMMON PITFALLS TO AVOID
========================

  * Mixing up the two uniform-buffer factories: CreateUniformBuffer(size)
    takes a byte SIZE; CreateUniformBufferFrom<T>(in value) uploads a VALUE
    (CreateUniformBufferFrom(64) makes a 16-byte buffer holding the number 64).
  * Drawing with a sprite batch or mesh renderer outside Begin/End, or ending
    the pass before End().
  * Starting a second GraphicsFrame on the same borrowed encoder: use one
    frame per encoder.
  * Disposing a texture a batch still draws this frame (End throws).
  * Forgetting that the pass target's formats pick the pipeline: a pass with a
    depth view needs pipelines built WithDepth(sameFormat) - the batch and
    renderer do this for you; your own pipelines must too.
  * Uploading straight-alpha colours and blending with PremultipliedAlpha (or
    the reverse).
  * Treating RenderTargetView sizes as logical sizes: they are pixels (on a
    high-density display use the window's pixel size).
  * Reading back a Depth24Plus texture (not copyable) - use Depth32Float.
  * Expecting a device to have a feature it was not created with: features
    and limits (timestamps, immediates, MaxImmediateSize) must be requested in
    GraphicsContextOptions; the adapter merely supporting them is not enough.
  * Rows of buffer/texture copies that are not multiples of 256 bytes - use
    TextureCopyLayout.AlignedBytesPerRow, or UploadTexture / WriteRegion.
  * Using a span from GetMappedRange after Unmap.
  * Resolving queries into a buffer without BufferUsage.QueryResolve, or at
    an offset that is not a multiple of 256.
  * Building a render bundle per frame: bundles are recorded once (Finish
    validates by rehearsal) and executed every frame.
  * Drawing sRGB textures (image and model loading default to sRGB) with the
    SpriteBatch or MeshRenderer into a non-sRGB target: the picture looks too
    dark. Use an sRGB target format, or load with ColorSpace = Linear /
    ColorTexturesSrgb = false. (ModelRenderer handles both target kinds; do
    not combine it with ColorTexturesSrgb = false, which double-encodes.)
  * Expecting MeshRenderer brightness from ModelRenderer: the model renderer
    is physically based (energy-conserving highlights; ambient light from
    the default sky). Light intensity 1 = a white matte surface facing
    the light shows white; raise LightIntensity / EnvironmentIntensity /
    Exposure.
  * Setting AmbientColor / AmbientGroundColor for models and seeing no
    change: they apply only with Ambient = AmbientLighting.Hemisphere; the
    default ambient is the environment (SceneLighting.Environment or the
    default sky).
  * Expecting no ambient light for models by default: a fresh SceneLighting
    lights models with the default sky too. For direct light only, use
    Ambient = Hemisphere with both ambient colours black.
  * Disposing an EnvironmentMap that a SceneLighting still names (the next
    Begin / skybox Draw throws ObjectDisposedException), or disposing
    gpu.DefaultEnvironment / gpu.BrdfLut (context-owned).
  * Building environments per frame: bake once at load time; turn them
    with EnvironmentRotation and scale them with EnvironmentIntensity.
  * Drawing the skybox first into a pass WITH a depth view and then
    expecting it behind everything - it is (it never writes depth), but
    drawing it after the opaque models is cheaper; and clearing depth to a
    value other than 1 hides it.
  * Using the panorama itself as the light while ALSO keeping the key
    light on with an HDRI that already contains the sun: the sun is counted
    twice. With an HDRI, set LightIntensity = 0 (or match the light to the
    HDRI's sun and accept the double highlight).
  * Applying exposure twice on the HDR path: SceneLighting.Exposure (model
    and skybox output) and ToneMapper.Exposure multiply.
  * Drawing sprite-batch HUDs into the RGBA16Float target before tone
    mapping (they get tone mapped too); draw them into the final target.
  * Feeding an 8-bit PNG / JPEG panorama and expecting HDR light: LDR
    panoramas are linearised sRGB, so windows and the sun clip at 1.
  * Posing an instance by writing into a span you kept: LocalTransforms and
    GetMorphWeights are read-only; change the pose with SetLocalTransform /
    SetMorphWeight(s), or write inside `using (ModelPoseEdit pose =
    instance.BeginPoseEdit()) { ... }` - the edit must END before the
    instance is drawn or its matrices are read (those throw while it is open).
  * Drawing right after Play / CrossFade without Update or Evaluate: starting
    a clip does not touch the pose, so that frame shows the previous pose
    (the rest pose for a fresh instance). Call Evaluate() once, or Update
    before drawing as usual.
  * Setting a node by hand while a playing clip animates the same property:
    the next Update overwrites it. Hand-pose nodes the clips do not animate,
    or use a higher layer / Stop the clip. Stopping a clip returns the
    properties it animated to the rest pose.
  * Keeping an AnimationState after its clip stopped or faded out: the player
    reuses state objects (check IsActive).
  * Expecting a looping clip's last key at Time == Duration: a loop wraps to
    0 there; use AnimationLoopMode.Once to hold the last frame.
  * Expecting a mirroring instance (negative scale) to vanish or look
    inside-out: model and mesh renderers flip the front-face winding for it.
  * Comparing the clear colour read back from an sRGB target with the
    linear clear value: the clear colour is encoded too.
  * Disposing a ModelTextureCache before the models that use it, or
    disposing an AnimatedTexture's frame textures (they belong to it).
  * Loading a kit model by model without a ModelTextureCache: every model
    decodes and uploads the shared colormap again.


WHAT THIS PACKAGE DOES NOT DO
=============================

  No windows, input or event loops (bring any toolkit; pass its native
  handles). No asynchronous pipeline compilation, futures or WaitAny, and no
  wrappers for the engine functions listed under LIMITS as not available.
  No scene graph or editor. Models: glTF 2.0
  only (no FBX, OBJ, DAE or other formats, no EXR/HDR or KTX2/DDS textures, no
  mesh compression); animation is glTF node animation (translation,
  rotation, scale, morph weights) with blending and cross-fades - no
  KHR_animation_pointer channels, retargeting, root-motion extraction, inverse
  kinematics or animation state machines. Environments: Radiance .hdr,
  JPEG XL and LDR panoramas and the procedural sky only (no OpenEXR, cube-map files,
  KTX2 environments, reflection probes or real-time scene capture); no
  bloom, auto-exposure or other post effects beyond tone mapping. No text
  rendering (draw glyphs from an atlas texture with the sprite batch). No
  shadows.


WORKING EXAMPLES ON GITHUB
==========================

  https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/tests/CodeBrix.WebGpu.Graphics.Tests

    Context, errors, lifetimes    GraphicsContextTests.cs
    Buffers                       GpuBufferTests.cs
    Textures, mipmaps, depth      GpuTextureTests.cs, HeadlessRenderTargetTests.cs
    Samplers and bind groups      BindingTests.cs
    WGSL diagnostics              GpuShaderTests.cs
    Vertex layouts                VertexLayoutTests.cs
    Render and compute pipelines  PipelineTests.cs
    Sprite batch                  SpriteBatchTests.cs
    Lit meshes, instancing, depth MeshRendererTests.cs, MeshShapesTests.cs
    Cameras                       CameraTests.cs
    Window handles                NativeWindowSourceTests.cs
    Sharing with Skia             SkiaSharingTests.cs
    Adapters, features, limits    GraphicsCapabilitiesTests.cs
    Engine diagnostics, logging   GraphicsDiagnosticsTests.cs
    Queries                       GpuQuerySetTests.cs
    GPU timing                    GpuTimerTests.cs
    Render bundles                RenderBundleBuilderTests.cs
    Indirect draws, immediates,
      blend constant, stencil     RenderPassTests.cs
    Indirect dispatch, dynamic
      offsets, push constants     ComputePassTests.cs
    Copies, clears, uploads,
      command buffers, waits      GraphicsFrameTests.cs
    Buffer mapping                GpuBufferTests.cs
    Texture regions, native
      images                      GpuTextureTests.cs
    SPIR-V, GLSL, trusted WGSL    GpuShaderTests.cs
    Adoption, wrapping handles    GraphicsContextTests.cs
    Regions, layouts, structs     TextureRegionTests.cs
    Image files, GPU mipmaps,
      readback, saving, arrays    ImageTextureTests.cs
    Animated GIF / JPEG XL,
      frame lists                 AnimatedTextureTests.cs
    JPEG XL test files (in-test)  TestJxl.cs
    glTF models, instances,
      model renderer              ModelLoadingTests.cs
    PBR shading, skinning,
      morph targets               ModelPbrTests.cs, ModelSkinningTests.cs,
                                  ModelMorphTests.cs
    Animation playback,
      blending, pose edits        AnimationPlayerTests.cs,
                                  AnimationSamplingTests.cs
    Animated models on the GPU    ModelAnimationRenderTests.cs
    Mirrored transforms           ModelMirroringTests.cs
    .hdr decoding                 RadianceHdrDecoderTests.cs
    Environments, BRDF LUT,
      procedural sky              EnvironmentMapTests.cs
    Image-based lighting, HDR
      path vs LDR path            ModelEnvironmentTests.cs
    Skybox                        SkyboxRendererTests.cs
    Tone mapping                  ToneMapperTests.cs
    Alpha modes, texture
      transforms (LitMaterial)    MeshRendererTests.cs

  https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/samples/WebGpuModelViewer
    A complete application: LoadModel + ModelTextureCache, ModelInstance
    animation (Play / CrossFade / UpdateAll), the PBR ModelRenderer lit by a
    procedural sky and a sun, SkyboxRenderer, the HDR path with ToneMapper,
    a sprite-batch HUD, a SkiaSharp overlay on the same GPU texture through
    Wrap(skiaDevice) + BeginFrame(skiaTarget), a live window from native
    handles, and headless PNG frames (also on Android:
    samples/WebGpuModelViewer.Android).


QUICK REFERENCE CARD
====================

  using GraphicsContext gpu = GraphicsContext.Create();
  GpuBuffer vb = gpu.CreateVertexBuffer<MyVertex>(vertices);
  GpuTexture tex = gpu.CreateTextureFromRgba(w, h, rgba, mipmaps: true);
  GpuShader sh = gpu.CreateShader(wgsl);
  GpuRenderPipeline p = gpu.CreateRenderPipeline(sh)
      .WithVertexBuffer<MyVertex>().WithColorTarget(fmt, BlendPreset.AlphaBlend)
      .WithDepth(TextureFormat.Depth32Float).WithCulling(CullPreset.Back).Build();
  using GraphicsFrame f = gpu.BeginFrame();
  using (RenderPass pass = f.BeginRenderPass(target, Color4.Black)) { ... }
  f.Submit();
  SpriteBatch: Begin(pass) ... Draw / Fill* / Draw* ... End()
  MeshRenderer: Begin(pass, camera, lighting) ... Draw / DrawInstanced ... End()
  HeadlessRenderTarget: View, ReadRgba(), ReadDepth()
  WindowSurface: Configure, TryAcquire, Present, Resize
  Features: GraphicsContextOptions.RequiredFeatures / RequestAllAdapterFeatures
  Timing: CreateTimer -> Pass(slot) / Resolve(frame) / ReadMilliseconds()
  Indirect: CreateIndirectBuffer<DrawIndirectArgs> -> pass.DrawIndirect(buffer)
  Bundles: CreateRenderBundle(target) ... Finish() -> pass.ExecuteBundle(bundle)
  Mapping: Map / MapAsync -> GetMappedSpan<T>() -> Unmap
  Immediates: WithImmediates<T>() -> pass.SetImmediates(in value)
  Images: CreateTextureFromFile(path[, TextureLoadOptions.Data]) -> ToImage /
          SaveAsPng / SaveAs(path)
  Animated: CreateAnimatedTexture("a.gif" or "a.jxl") -> batch.Draw(anim, elapsed, rect, tint)
  Models: LoadModel("m.glb") -> CreateInstance(world) ->
          CreateModelRenderer(): Begin / Draw(instance) / End  (PBR, GPU
          skinning + morphs; SetLocalTransform / SetMorphWeight /
          BeginPoseEdit to pose)
  Environments: gpu.CreateEnvironment("x.hdr" or "x.jxl") -> lighting.Environment =
          env (default: gpu.DefaultEnvironment sky); EnvironmentRotation /
          EnvironmentIntensity; gpu.CreateSkyboxRenderer().Draw(pass, camera,
          lighting); HDR: RGBA16Float target -> gpu.CreateToneMapper().Apply(
          frame, hdr.Color, finalView)
  Animation: instance.Animation.Play("walk") / CrossFade("run", 0.25f) /
          Blend(clip, weight, layer: 1); per frame
          AnimationPlayer.UpdateAll(instances, dt) (or instance.Animation.Update(dt))


END OF AGENT-README
================================================================================
