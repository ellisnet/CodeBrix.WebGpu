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
  * PRESENTATION: any texture view (`RenderTargetView`), a headless target
    (`HeadlessRenderTarget`) and a window surface over native window handles
    (`WindowSurface`, `NativeWindowSource`). The library creates no windows.

Every raw handle stays reachable (`Handle` properties, `GraphicsContext.Device`,
`RenderPass.Handle`, `GraphicsFrame.Encoder`), so raw CodeBrix.WebGpu.Native
calls can be mixed in where the wrapper stops.

The package depends ONLY on CodeBrix.WebGpu.Native. It does not reference
SkiaSharp or CodeBrix.WebGpu.Skia; it shares a device with them through
handles (see SHARING A DEVICE WITH CODEBRIX.WEBGPU.SKIA).


INSTALLATION
============

  Package id:   CodeBrix.WebGpu.Graphics.MitLicenseForever
  Install:      dotnet add package CodeBrix.WebGpu.Graphics.MitLicenseForever
  Namespace:    CodeBrix.WebGpu.Graphics
  Target:       net10.0 (any OS the native package supports: Windows x64/ARM64,
                macOS Intel/Apple Silicon, Linux x64/ARM64/RISC-V 64,
                Android ARM64/x64)
  Brings:       CodeBrix.WebGpu.Native.MitLicenseForever (with the WebGPU
                natives for every platform). Nothing else.
  csproj:       nothing special for the wrapper API. Add
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks> only if you also
                make raw CodeBrix.WebGpu.Native calls with pointers.
  Maths:        System.Numerics only - Vector2/3/4, Matrix3x2, Matrix4x4,
                Quaternion - plus System.Drawing.RectangleF for 2D rectangles.

  Name clash to know about: inside a namespace that starts with
  `CodeBrix.WebGpu`, the identifier `WebGpu` resolves to the NAMESPACE, not the
  static binding class. Write `Native.WebGpu.Foo(...)` there.


KEY NAMESPACES / USINGS
=======================

  using CodeBrix.WebGpu.Graphics;   // everything in this package
  using CodeBrix.WebGpu.Native;     // enums and handle structs the API uses:
                                    // TextureFormat, BufferUsage, TextureUsage,
                                    // ShaderStage, VertexFormat, PresentMode,
                                    // Device, Queue, TextureView, CommandEncoder ...
  using System.Numerics;            // vectors and matrices
  using System.Drawing;             // RectangleF for the sprite batch


CORE API REFERENCE
==================

GraphicsContext : IDisposable                  (not thread-safe; one thread)
-------------------------------------------------------------------------------
  static Create() / Create(GraphicsContextOptions)   -> throws GraphicsException
  static TryCreate(options, out context, out reason) -> bool, never throws
  static Wrap(Device, Queue, Instance = default, Adapter = default)
        Shares a device another library created. Takes its OWN reference on
        each handle (dispose order does not matter) and installs no callbacks.
        Pass the instance to create window surfaces; pass the adapter so the
        surface can read its capabilities.
  Instance, Adapter, Device, Queue      raw handles
  Backend (BackendType), AdapterName, Limits, IsWrapped, IsDisposed
  IsLost, LostReason
  event Action<GraphicsError> UncapturedError   raised from Poll / Submit
  event Action<string> DeviceLost               raised once, from Poll / Submit
  TakeErrors()          -> GraphicsError[] (queued errors nobody listened to)
  Poll(bool wait = false), WaitIdle()
  LiveResourceCount, GetLiveResources()  (oldest first)
  BeginFrame()                      frame with its own encoder (submits it)
  BeginFrame(CommandEncoder)        frame on someone else's encoder
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

GpuSampler (Handle, AddressMode, MagFilter, MinFilter, IsComparison)
SamplerDescription: AddressMode, MagFilter, MinFilter, MipmapFilter, Compare,
  MaxAnisotropy, Label; presets LinearClamp, LinearRepeat, NearestClamp,
  NearestRepeat
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
  BeginRenderPass(RenderTargetView, Color4? clearColor, float? clearDepth = 1)
        clearColor null = keep (load) the colour; clearDepth null = load depth
  BeginComputePass()
  CopyBuffer(source, sourceOffset, destination, destinationOffset, size)
  Submit()   owned encoder: finish + submit; borrowed: only closes the frame.
             Then raises the context's queued events.
  Dispose()  abandons an unsubmitted owned encoder
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
SceneLighting: LightDirection (the direction light TRAVELS), LightColor,
  LightIntensity, AmbientColor
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
         instanced DrawIndexed. LastDrawCallCount, LastInstanceCount.
  Depth testing happens when the pass target has a depth view.
LitMaterial : GraphicsResource  BaseColor, Shininess, SpecularStrength,
  Unlit, DoubleSided, BaseColorTexture, Sampler, IsTranslucent (alpha < 1)
  Shading: Blinn-Phong - albedo * (ambient + diffuse * light) + specular.
Color4 (straight alpha floats): R, G, B, A, named colours, FromBytes,
  FromHex("#RRGGBB[AA]"), WithAlpha, Lerp, ToVector4, ToPackedRgba, *, ==

Presentation
------------
RenderTargetView (struct): ColorView, ColorFormat, Width, Height, DepthView,
  DepthFormat, HasDepth, AspectRatio, WithDepth(GpuTexture)
  new RenderTargetView(TextureView, TextureFormat, width, height) wraps ANY
  view - a texture of yours, another library's.
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

The Graphics package has no SkiaSharp types. Share by handles:

  WebGpuSkiaDevice skia = WebGpuSkiaDevice.Create();
  GraphicsContext gpu = GraphicsContext.Wrap(skia.Device, skia.Queue, skia.Instance, skia.Adapter);
  WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(skia, skia.CreateGrContext(), w, h);

  CommandEncoder encoder = target.BeginWebGpuPass();
  using (GraphicsFrame frame = gpu.BeginFrame(encoder))
  {
      RenderTargetView view = new RenderTargetView(target.TextureView, target.Format, target.Width, target.Height);
      using (RenderPass pass = frame.BeginRenderPass(view.WithDepth(depth), Color4.Black)) { ... }
      frame.Submit();                 // closes the frame; the target submits
  }
  target.EndWebGpuPass();
  // SkiaSharp may now draw on target.Surface.Canvas, on the same pixels.

To sample the Skia target's texture from Graphics (for example to draw it
onto a window surface as one full-screen sprite), wrap it once:
  GpuTexture skiaTexture = gpu.WrapTexture(target.Texture, target.TextureView, target.Format, w, h);
and record the drawing inside another BeginWebGpuPass / EndWebGpuPass pair,
which first hands Skia's pending work to the GPU.

The Skia device installs the uncaptured-error callback for the shared device:
read errors with skia.TakeUncapturedErrors(); the wrapped Graphics context
receives none.


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
    process on an uncaptured validation error. Devices created by
    GraphicsContext.Create and by WebGpuSkiaDevice.Create always have one;
    object creation through this library always runs inside an error scope.
  * Mipmaps are generated on the CPU, for RGBA8 / BGRA8 textures only.
  * Lighting is computed in the target's own colour space (no sRGB
    conversion unless you choose sRGB formats yourself).
  * Not thread-safe: one context per thread.


COMMON MISTAKES TO AVOID
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


WHAT THIS PACKAGE DOES NOT DO
=============================

  No windows, input or event loops (bring any toolkit; pass its native
  handles). No scene graph or editor. No model loading or asset pipeline
  (convert your meshes into MeshData). No text rendering (draw glyphs from an
  atlas texture with the sprite batch). No shadows, skinning or PBR
  materials.


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

  https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/samples/WebGpuModelViewer
    A complete application: animated glTF models, a sprite-batch HUD, a
    SkiaSharp overlay on the same GPU texture, a live window from native
    handles, and headless PNG frames.


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


END OF AGENT-README
================================================================================
