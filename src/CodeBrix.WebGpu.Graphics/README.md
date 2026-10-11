# CodeBrix.WebGpu.Graphics

A small, friendly graphics layer over WebGPU for .NET: one context object that owns (or shares) the GPU device, typed buffers and textures, WGSL shaders with readable compile errors, pipeline builders that derive vertex layouts from your structs, a batched 2D sprite and shape renderer, a lit 3D mesh renderer with cameras, depth and instancing, and presentation to any texture view, a headless render target or a window you created with any toolkit. CodeBrix.WebGpu.Graphics is provided as a .NET library and associated `CodeBrix.WebGpu.Graphics.MitLicenseForever` NuGet package.

CodeBrix.WebGpu.Graphics supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.WebGpu.Graphics.MitLicenseForever
```

Note that the NuGet package ID and the namespace are different - there is no package named plain `CodeBrix.WebGpu.Graphics`:

* NuGet package ID: `CodeBrix.WebGpu.Graphics.MitLicenseForever`
* Assembly and primary namespace: `CodeBrix.WebGpu.Graphics` - i.e. `using CodeBrix.WebGpu.Graphics;`

XML documentation (IntelliSense) ships alongside the assembly.

CodeBrix.WebGpu.Graphics depends only on CodeBrix.WebGpu.Native (`CodeBrix.WebGpu.Native.MitLicenseForever`, the WebGPU binding with its native libraries for every supported platform), which is installed automatically. It uses `System.Numerics` for all maths (`Vector2/3/4`, `Matrix3x2`, `Matrix4x4`, `Quaternion`) and creates no windows itself.

## CodeBrix.WebGpu.Graphics supports:

* **One context** - `GraphicsContext` creates the instance, adapter, device and queue on the platform's own GPU API (Vulkan on Linux and Android, Direct3D 12 or Vulkan on Windows, Metal on macOS), with backend and power-preference options; or it wraps a device another library created - for example the one a CodeBrix.WebGpu.Skia `WebGpuSkiaDevice` owns - so both draw on the same GPU device
* **Errors you can see** - validation errors are caught where objects are created and thrown as exceptions that carry WebGPU's own message; anything else is queued and raised through `UncapturedError`, and device loss through `DeviceLost`
* **Lifetimes handled** - every resource is tracked by its context; disposing the context releases whatever is still alive, newest first
* **Resources** - vertex, index, uniform and storage buffers with typed uploads from spans of structs and readback; 2D textures in RGBA8, BGRA8, depth and other formats, uploaded from raw bytes or RGBA pixels, with optional mipmaps and readback; sampler presets; bind-group layouts and bind groups from short entry descriptions
* **WGSL shaders** with compile diagnostics (`ShaderCompilationException`)
* **Pipeline builders** - render and compute pipelines with sensible defaults, blend, depth and culling presets, automatic or explicit layouts, and vertex layouts derived from blittable structs (attributes optional) or described fluently
* **2D drawing** - `SpriteBatch`: textured sprites (whole textures or atlas regions, rotated and scaled), rectangles, lines, circles, triangles and quads, a transform stack, colours and blend presets - one draw call per run of the same texture, and shapes never break a run
* **3D drawing** - meshes with positions, normals, texture coordinates and colours; generated cubes, boxes, planes and spheres; perspective and orthographic cameras with an orbit controller; a lit material (Blinn-Phong with a directional light and ambient term, optional base-colour texture, translucency, single- or double-sided); depth testing; per-instance model matrices, with consecutive draws of one mesh merged into one instanced draw
* **Presentation** - render into any texture view (a texture of your own, a CodeBrix.WebGpu.Skia target, ...); a headless render target read back as bytes, RGBA pixels or depth values; and a window surface created from native handles (X11 through Xlib or XCB, Wayland, Win32, a Metal layer, an Android native window), configured from its capabilities, acquired, presented and resized

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

### Sharing the device with CodeBrix.WebGpu.Skia

```csharp
using WebGpuSkiaDevice skia = WebGpuSkiaDevice.Create();
using GraphicsContext gpu = GraphicsContext.Wrap(skia.Device, skia.Queue, skia.Instance, skia.Adapter);
using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(skia, skia.CreateGrContext(), 800, 600);

CommandEncoder encoder = target.BeginWebGpuPass();
using (GraphicsFrame frame = gpu.BeginFrame(encoder))             // records into the target's encoder
{
    RenderTargetView view = new RenderTargetView(target.TextureView, target.Format, target.Width, target.Height);
    using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black)) { /* draw */ }
    frame.Submit();                                              // the target submits
}

target.EndWebGpuPass();
target.Surface.Canvas.DrawText(...);                             // SkiaSharp draws on the same pixels
```

## Documentation

The NuGet package includes `AGENT-README.txt`, a complete API reference and usage guide written for AI coding agents - point your agent at that file when it is writing code against this library.

Additional sample code and usage examples are available in the `CodeBrix.WebGpu.Graphics.Tests` project:
https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/tests/CodeBrix.WebGpu.Graphics.Tests

and a complete application - animated 3D models, a 2D HUD, a SkiaSharp overlay, a live window or headless PNG frames - in `samples/WebGpuModelViewer`:
https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/samples/WebGpuModelViewer

## License

CodeBrix.WebGpu.Graphics is licensed under the MIT License - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/THIRD-PARTY-NOTICES.txt).
