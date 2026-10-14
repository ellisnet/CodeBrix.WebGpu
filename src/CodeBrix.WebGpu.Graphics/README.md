# CodeBrix.WebGpu.Graphics

A small, friendly graphics layer over WebGPU for .NET: one context object that owns (or shares) the GPU device, typed buffers and textures, WGSL shaders with readable compile errors, pipeline builders that derive vertex layouts from your structs, a batched 2D sprite and shape renderer, a lit 3D mesh renderer with cameras, depth and instancing, image files and animated images as textures, glTF 2.0 models loaded onto the GPU with physically based shading and image-based lighting (HDR environments, a procedural sky, a skybox and tone mapping), and presentation to any texture view, a headless render target or a window you created with any toolkit. CodeBrix.WebGpu.Graphics is provided as a .NET library and associated `CodeBrix.WebGpu.Graphics.MitLicenseForever` NuGet package.

CodeBrix.WebGpu.Graphics supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.WebGpu.Graphics.MitLicenseForever
```

Note that the NuGet package ID and the namespace are different - there is no package named plain `CodeBrix.WebGpu.Graphics`:

* NuGet package ID: `CodeBrix.WebGpu.Graphics.MitLicenseForever`
* Assembly and primary namespace: `CodeBrix.WebGpu.Graphics` - i.e. `using CodeBrix.WebGpu.Graphics;`

XML documentation (IntelliSense) ships alongside the assembly.

CodeBrix.WebGpu.Graphics depends on CodeBrix.WebGpu.Native (`CodeBrix.WebGpu.Native.MitLicenseForever`, the WebGPU binding with its native libraries for every supported platform), CodeBrix.Imaging (`CodeBrix.Imaging.ApacheLicenseForever`, fully managed image decoding and encoding, Apache-2.0) and CodeBrix.Graphics3D.Gltf2 (`CodeBrix.Graphics3D.Gltf2.MitLicenseForever`, the glTF 2.0 reader), all installed automatically. It uses `System.Numerics` for all maths (`Vector2/3/4`, `Matrix3x2`, `Matrix4x4`, `Quaternion`) and creates no windows itself.

## CodeBrix.WebGpu.Graphics supports:

* **One context** - `GraphicsContext` creates the instance, adapter, device and queue on the platform's own GPU API (Vulkan on Linux and Android, Direct3D 12 or Vulkan on Windows, Metal on macOS), with backend and power-preference options; or it wraps a device another library created - in one call for any owner of the CodeBrix.WebGpu.Native `IWebGpuDeviceSource` contract, such as a CodeBrix.WebGpu.Skia `WebGpuSkiaDevice` - so both draw on the same GPU device
* **Errors you can see** - validation errors are caught where objects are created and thrown as exceptions that carry WebGPU's own message; anything else is queued and raised through `UncapturedError`, and device loss through `DeviceLost`
* **Lifetimes handled** - every resource is tracked by its context; disposing the context releases whatever is still alive, newest first
* **Resources** - vertex, index, uniform and storage buffers with typed uploads from spans of structs and readback; 2D textures in RGBA8, BGRA8, depth and other formats, uploaded from raw bytes or RGBA pixels, with optional mipmaps and readback; sampler presets; bind-group layouts and bind groups from short entry descriptions
* **WGSL shaders** with compile diagnostics (`ShaderCompilationException`)
* **Pipeline builders** - render and compute pipelines with sensible defaults, blend, depth and culling presets, automatic or explicit layouts, and vertex layouts derived from blittable structs (attributes optional) or described fluently
* **2D drawing** - `SpriteBatch`: textured sprites (whole textures or atlas regions, rotated and scaled), rectangles, lines, circles, triangles and quads, a transform stack, colours and blend presets - one draw call per run of the same texture, and shapes never break a run
* **3D drawing** - meshes with positions, normals, texture coordinates and colours; generated cubes, boxes, planes and spheres; perspective and orthographic cameras with an orbit controller; a lit material (Blinn-Phong with a directional light and ambient term, optional base-colour texture with a texture-coordinate transform, opaque / alpha-tested / blended alpha modes, unlit, single- or double-sided); depth testing; per-instance model matrices, with consecutive draws of one mesh merged into one instanced draw
* **Images to textures** - `CreateTextureFromFile` / `FromStream` / `FromEncoded` / `FromImage` decode PNG, JPEG, GIF, BMP, TGA, TIFF, PBM and still WebP with CodeBrix.Imaging and upload straight from the decoded pixel memory; sRGB (colour) or linear (data) textures, GPU-generated mipmaps (`GenerateMipmaps`, sRGB-correct), premultiplied alpha; texture arrays with per-layer views; readback to an Imaging image and saving (`ToImage`, `SaveAsPng`, `SaveAs` by extension) for textures and headless targets
* **Animated textures** - `AnimatedTexture` from an animated GIF (frame delays honoured) or any list of frames: every frame in its own texture-array layer (or streamed through a small ring when too large), loop / ping-pong / once playback, frame lookup by elapsed time without allocation, drawn with `SpriteBatch`
* **glTF 2.0 models** - `LoadModel` reads .glb and .gltf (external or embedded buffers and images) into a GPU-ready `Model`: every primitive in one shared vertex and index buffer, materials captured completely as metallic-roughness parameters (texture transforms, alpha modes, double-sided, unlit), shared textures and samplers, node hierarchy, skins, morph targets and decoded animation clips; `ModelInstance` holds each placement's pose (node transforms, joint palettes, morph weights), and `ModelRenderer` draws many instances with one instanced draw per primitive and no per-frame allocation (mirroring node or instance transforms flip the front-face winding as glTF requires)
* **Model animation** - every instance plays the model's shared clips with its own `AnimationPlayer`: play by name or index, loop / ping-pong / once (holding the last frame), speed, pause, seek and normalized time, cross-fades, weighted and layered blending of several clips (hemisphere-corrected rotations, morph-weight channels too), a "clip finished" callback, STEP / LINEAR (slerp) / CUBICSPLINE interpolation with a key cursor per channel; the pose drives node matrices, GPU skinning and morph targets; updating allocates nothing, and one update helper serves thousands of instances
* **Physically based model shading** - glTF metallic-roughness (GGX, Smith, Schlick) with base colour, metallic-roughness, normal, occlusion and emissive maps, per-texture KHR_texture_transform and texture set, vertex colours, unlit materials, alpha mask and back-to-front sorted blending, double-sided materials; up to eight directional lights, image-based ambient light (or a cheap hemispheric gradient) and exposure; GPU skinning and GPU morph targets per instance; correct colour output on sRGB and UNORM targets alike
* **Image-based lighting** - `EnvironmentMap`s baked on the GPU from an equirectangular HDR panorama (a Radiance `.hdr` file or stream - decoded by the built-in `RadianceHdrDecoder` - an in-memory `FloatImage`, or any image CodeBrix.Imaging decodes) or from a procedural sky (zenith / horizon / ground colours, haze, sun glow and an optional sun disc): a radiance cube with mips, a diffuse irradiance cube and a GGX-prefiltered specular cube, with a split-sum BRDF lookup table shared per context; models are lit by the procedural default sky out of the box, or by any environment with an intensity and a rotation about the vertical axis; energy-conserving (multiple-scattering) reflections for dielectrics and metals
* **Sky backgrounds and HDR output** - a `SkyboxRenderer` draws the environment (sharp or blurred) behind the scene; render into an RGBA16Float target and finish with a `ToneMapper` (ACES fitted, Khronos PBR Neutral, Reinhard, Narkowicz ACES or clamp, with exposure) for real dynamic range, or keep drawing straight into the window for simple scenes
* **Presentation** - render into any texture view (a render texture of your own, a wrapped texture of another library) or straight into a shared `IWebGpuTextureTarget` such as a CodeBrix.WebGpu.Skia target (one `BeginFrame(target, depth)` call opens and closes the target's WebGPU pass); a headless render target read back as bytes, RGBA pixels or depth values; and a window surface created from native handles (X11 through Xlib or XCB, Wayland, Win32, a Metal layer, an Android native window), configured from its capabilities, acquired, presented and resized
* **Adapters, features and limits** - list the adapters with their capabilities, choose one, and request the features and limits a device needs (or everything the adapter offers)
* **Queries and GPU timing** - occlusion, timestamp and pipeline-statistics queries resolved into buffers, and a `GpuTimer` that turns pass and command timestamps into milliseconds
* **Indirect and multi-draws** - typed `DrawIndirectArgs` / `DrawIndexedIndirectArgs` / `DispatchIndirectArgs` buffers (fillable from compute shaders), indirect draws and dispatches, multi-draws and GPU-counted multi-draws
* **Render bundles** - a `RenderBundleBuilder` with the same calls as a render pass, validated before it is finished, replayed by passes every frame
* **Buffers and textures in depth** - mapping (synchronous or task-based) with spans over the mapped memory, mapped-at-creation buffers, buffer/texture/region copies, clears, in-frame uploads, region reads and writes, introspection and early destruction
* **Immediates (push constants)**, blend constants, stencil references, explicit pipeline layouts, debug groups and markers, SPIR-V, GLSL and trusted WGSL shaders
* **Command buffers and synchronisation** - finish frames and submit them together, wait for one submission, or await the queue with a `Task`; invalid commands throw instead of reaching the GPU
* **Interop** - wrap any raw WebGPU handle, read the Vulkan / Direct3D 12 / Metal objects behind a device or texture, build a context over an application's own Vulkan or Metal device, and wrap native images as textures

## Sample Code

### A lit, spinning cube rendered headless

```csharp
using System.Numerics;
using CodeBrix.WebGpu.Graphics;

using GraphicsContext gpu = GraphicsContext.Create();
using HeadlessRenderTarget target = gpu.CreateHeadlessTarget(640, 480);       // colour + depth
using Mesh cube = gpu.CreateMesh(MeshShapes.Cube(2f));
using MeshRenderer meshes = new MeshRenderer(gpu);
LitMaterial material = meshes.CreateMaterial(Color4.FromHex("#E07A3F"));
Camera camera = new Camera { Position = new Vector3(4, 3, 5) };

using (GraphicsFrame frame = gpu.BeginFrame())
{
    using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.CornflowerBlue))
    {
        meshes.Begin(pass, camera, new SceneLighting());
        meshes.Draw(cube, material, Matrix4x4.CreateRotationY(0.5f));
        meshes.End();
    }

    frame.Submit();
}

byte[] rgba = target.ReadRgba();   // 640 x 480 x 4 bytes, red first
```

### Sprites and shapes in one draw

```csharp
using SpriteBatch sprites = new SpriteBatch(gpu);
using GpuTexture atlas = gpu.CreateTextureFromRgba(width, height, rgbaPixels);

// inside a render pass:
sprites.Begin(pass);
sprites.Draw(atlas, new RectangleF(10, 10, 64, 64), new RectangleF(0, 0, 32, 32), Color4.White);  // an atlas cell
sprites.FillRectangle(new RectangleF(100, 10, 80, 20), Color4.Red.WithAlpha(0.5f));
sprites.DrawLine(new Vector2(10, 100), new Vector2(200, 140), Color4.Yellow, 3);
sprites.FillCircle(new Vector2(260, 60), 24, Color4.Green);
sprites.End();                                                         // sprites.LastDrawCallCount == 1
```

### Your own pipeline, with a vertex layout derived from a struct

```csharp
[StructLayout(LayoutKind.Sequential)]
struct ColoredVertex
{
    public Vector2 Position;                                     // @location(0) Float32x2
    [VertexElement(1, VertexFormat.Unorm8x4)] public uint Color; // explicit format
}

using GpuShader shader = gpu.CreateShader(wgsl, "colored");        // throws ShaderCompilationException with diagnostics
using GpuRenderPipeline pipeline = gpu.CreateRenderPipeline(shader)
    .WithVertexBuffer<ColoredVertex>()
    .WithColorTarget(TextureFormat.RGBA8Unorm, BlendPreset.AlphaBlend)
    .Build();
```

### A window you created

```csharp
// handles from your toolkit: an X11 display and window, a Wayland display and surface, a Win32 HWND, a CAMetalLayer...
using WindowSurface surface = gpu.CreateWindowSurface(NativeWindowSource.FromXlib(display, windowId));
surface.Configure(pixelWidth, pixelHeight);                    // format from the surface's capabilities, vsync, depth

while (running)
{
    if (surface.TryAcquire(out RenderTargetView view) != SurfaceAcquireResult.Ready) continue;
    using (GraphicsFrame frame = gpu.BeginFrame())
    {
        using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black)) { /* draw */ }
        frame.Submit();
    }

    surface.Present();
}
```

### Timing a pass and drawing indirectly

```csharp
using GraphicsContext gpu = GraphicsContext.Create(new GraphicsContextOptions { RequiredFeatures = { FeatureName.TimestampQuery } });
using GpuTimer timer = gpu.CreateTimer(1);
using GpuBuffer draws = gpu.CreateIndirectBuffer<DrawIndirectArgs>(new[] { new DrawIndirectArgs(vertexCount: 3) });

using (GraphicsFrame frame = gpu.BeginFrame())
{
    RenderPassOptions options = new RenderPassOptions { Timestamps = timer.Pass(0), Label = "scene" };
    using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black, 1f, in options))
    {
        pass.SetPipeline(pipeline);
        pass.DrawIndirect(draws);                                 // arguments read by the GPU
    }

    timer.Resolve(frame);
    frame.Submit();
}

double sceneMilliseconds = timer.ReadMilliseconds()[0];
```

### A render bundle recorded once, replayed every frame

```csharp
RenderBundleBuilder builder = gpu.CreateRenderBundle(pipeline);
builder.SetPipeline(pipeline);
builder.SetVertexBuffer(0, vertices);
builder.Draw(vertexCount);
using GpuRenderBundle bundle = builder.Finish("static scenery");    // validated first

// every frame, inside a pass:
pass.ExecuteBundle(bundle);
```

### Textures from image files, and an animated GIF

```csharp
using GpuTexture photo = gpu.CreateTextureFromFile("photo.jpg");                       // sRGB, GPU mipmaps
using GpuTexture normals = gpu.CreateTextureFromFile("normal.png", TextureLoadOptions.Data); // linear
using AnimatedTexture spinner = gpu.CreateAnimatedTexture("spinner.gif");

// inside a sprite batch, every frame:
batch.Draw(spinner, elapsed, new RectangleF(10, 10, 64, 64), Color4.White);

target.SaveAsPng("frame.png");                                                         // a headless target
```

### A glTF model, many instances

```csharp
using Model tree = gpu.LoadModel("tree.glb");               // geometry, materials, textures, skins, clips
using ModelRenderer models = gpu.CreateModelRenderer();
ModelInstance[] forest = new ModelInstance[100];
for (int i = 0; i < forest.Length; i++)
    forest[i] = tree.CreateInstance(Matrix4x4.CreateTranslation(i % 10 * 3, 0, i / 10 * 3));

SceneLighting lighting = new SceneLighting();               // key light + the procedural default sky
lighting.AdditionalLights.Add(new DirectionalLight(new Vector3(1, -0.3f, 0.5f), Color4.White, 0.4f));

models.Begin(pass, camera, lighting);                       // sRGB or UNORM target: same picture
foreach (ModelInstance instance in forest) models.Draw(instance);
models.End();                                               // one instanced draw per primitive
```

Animation plays per instance (the clips are shared); the renderer skins and morphs on the GPU:

```csharp
using Model archer = gpu.LoadModel("character-archer.glb");
ModelInstance[] crowd = new ModelInstance[500];
for (int i = 0; i < crowd.Length; i++)
{
    crowd[i] = archer.CreateInstance(Matrix4x4.CreateTranslation(i % 25, 0, i / 25));
    crowd[i].Animation.Play(i % 2 == 0 ? "walk" : "idle", AnimationLoopMode.Loop, speed: 1f, startTime: i * 0.1f);
}

AnimationState attack = crowd[0].Animation.CrossFade("attack-melee-right", 0.2f, AnimationLoopMode.Once);
crowd[0].Animation.ClipFinished = state => state.Player.CrossFade("walk", 0.2f);
crowd[1].Animation.Blend("emote-yes", weight: 0.5f, layer: 1);    // a second layer over the base clip

// every frame:
AnimationPlayer.UpdateAll(crowd, deltaSeconds);                    // no allocation
models.Begin(pass, camera, lighting);
foreach (ModelInstance instance in crowd) models.Draw(instance);
models.End();
```

Posing by hand works the same way (a clip that animates the same node overwrites it on the next update):

```csharp
ModelInstance hero = archer.CreateInstance();
int arm = archer.FindNode("arm-left");
NodeTransform rest = hero.GetLocalTransform(arm);
hero.SetLocalTransform(arm, new NodeTransform(rest.Translation, rest.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), rest.Scale));
// many nodes at once - the pose counts as changed when the edit ends:
using (ModelPoseEdit pose = hero.BeginPoseEdit())
    pose.LocalTransforms[archer.FindNode("head")].Scale = new Vector3(1.2f);
// a model with morph targets: instance.SetMorphWeight(node, target, weight);
```

Supported glTF: triangles (lists, strips and fans; 8-, 16- and 32-bit indices), POSITION, NORMAL, TANGENT, TEXCOORD_0/1, COLOR_0, JOINTS_0/WEIGHTS_0, sparse accessors, morph targets, skins, node animation (step, linear, cubic spline), metallic-roughness materials with every texture slot, KHR_texture_transform, KHR_materials_unlit and KHR_materials_emissive_strength; clearcoat, specular, IOR, transmission and other material extensions are captured and listed as approximated. Not supported: FBX, OBJ, DAE and other formats; KTX2/Basis, DDS and EXR textures; KHR_draco_mesh_compression and other geometry compression; cameras, lights and KHR_animation_pointer. The model renderer shades every metallic-roughness slot; IOR and specular set the dielectric reflectance, clearcoat is approximated, transmission is drawn as an opaque surface. No shadows.

### Image-based lighting, a sky background and HDR output

```csharp
using EnvironmentMap hall = gpu.CreateEnvironment("small_cathedral_1k.hdr");   // baked once on the GPU
using SkyboxRenderer sky = gpu.CreateSkyboxRenderer();
using ToneMapper toneMapper = gpu.CreateToneMapper();                     // ACES fitted by default
toneMapper.Operator = ToneMapOperator.KhronosPbrNeutral;
using HeadlessRenderTarget hdr = gpu.CreateHeadlessTarget(w, h, TextureFormat.RGBA16Float);

SceneLighting lighting = new SceneLighting { Environment = hall, LightIntensity = 0f, EnvironmentRotation = 0.6f };
using (GraphicsFrame frame = gpu.BeginFrame())
{
    using (RenderPass pass = frame.BeginRenderPass(hdr.View, Color4.Black))
    {
        models.Begin(pass, camera, lighting);
        models.Draw(potInstance);
        models.End();
        sky.Draw(pass, camera, lighting);                                  // after the opaque models: fills the rest
    }

    toneMapper.Apply(frame, hdr.Color, windowView);                         // linear HDR -> display
    frame.Submit();
}
```

Without an HDRI the renderer lights models with `gpu.DefaultEnvironment`, a procedural sky whose sun matches the default key light; make your own with `gpu.CreateProceduralSky(new ProceduralSkyOptions { SunDirection = ..., Turbidity = 4f })`. Simple scenes can skip the HDR target and draw models and the skybox straight into an sRGB or UNORM window target; use the RGBA16Float target plus `ToneMapper` when lights, the sun or bright windows exceed 1. OpenEXR files are not supported (convert them to `.hdr`).

### Sharing the device with CodeBrix.WebGpu.Skia

Neither package references the other: both build on CodeBrix.WebGpu.Native, whose `IWebGpuDeviceSource` and `IWebGpuTextureTarget` contracts a `WebGpuSkiaDevice` and a `WebGpuSkiaTarget` implement - so sharing is one call each.

```csharp
using WebGpuSkiaDevice skia = WebGpuSkiaDevice.Create();
GRContext grContext = skia.CreateGrContext();
using GraphicsContext gpu = GraphicsContext.Wrap(skia);                  // shares the device
using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(skia, grContext, 800, 600);
using GpuTexture depth = gpu.CreateDepthTexture(800, 600);

using (GraphicsFrame frame = gpu.BeginFrame(target, depth))              // opens the target's WebGPU pass
{
    using (RenderPass pass = frame.BeginRenderPass(Color4.Black)) { /* draw */ }
    frame.Submit();                                                       // ends it: the target submits
}

target.Surface.Canvas.DrawText(...);                                      // SkiaSharp draws on the same pixels
```

`RenderTargetView.From(target, depth)` and `gpu.WrapTexture(target)` give the target's view and a sampleable wrapper of its texture; use the texture only inside a frame from `BeginFrame(target)`.

## Documentation

The NuGet package includes `AGENT-README.txt`, a complete API reference and usage guide written for AI coding agents - point your agent at that file when it is writing code against this library.

Additional sample code and usage examples are available in the `CodeBrix.WebGpu.Graphics.Tests` project:
https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/tests/CodeBrix.WebGpu.Graphics.Tests

and a complete application - animated glTF models with physically based shading, sky lighting and tone mapping, a 2D HUD, a SkiaSharp overlay, a live window or headless PNG frames - in `samples/WebGpuModelViewer`:
https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/samples/WebGpuModelViewer

## License

CodeBrix.WebGpu.Graphics is licensed under the MIT License - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/THIRD-PARTY-NOTICES.txt).
