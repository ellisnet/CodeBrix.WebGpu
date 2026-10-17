# CodeBrix.WebGpu.Skia

WebGPU rendering straight into SkiaSharp surfaces: give it an `SKSurface`, an `SKCanvas`, a `GRContext` or nothing at all, and WebGPU draws into it the fastest way the inputs allow - with no copy at all when the surface lives on the same GPU device - and tells you which way it took. CodeBrix.WebGpu.Skia is provided as a .NET library and associated `CodeBrix.WebGpu.Skia.MitLicenseForever` NuGet package.

CodeBrix.WebGpu.Skia supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.WebGpu.Skia.MitLicenseForever
```

Note that the NuGet package ID and the namespace are different - there is no package named plain `CodeBrix.WebGpu.Skia`:

* NuGet package ID: `CodeBrix.WebGpu.Skia.MitLicenseForever`
* Assembly and primary namespace: `CodeBrix.WebGpu.Skia` - i.e. `using CodeBrix.WebGpu.Skia;`

XML documentation (IntelliSense) ships alongside the assembly.

CodeBrix.WebGpu.Skia depends on SkiaSharp and on CodeBrix.WebGpu.Native (`CodeBrix.WebGpu.Native.MitLicenseForever`, the WebGPU binding with its native libraries for every supported platform); both are installed automatically. It does not bring SkiaSharp's native libraries for Linux: add the SkiaSharp native-assets package for each platform your application runs on (for example `SkiaSharp.NativeAssets.Linux`), as every SkiaSharp application does. Code that records WebGPU commands must allow unsafe code (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`).

## CodeBrix.WebGpu.Skia supports:

* Three ways to get WebGPU pixels onto a Skia surface, chosen automatically and reported (`WebGpuSkiaTarget.Tier` and `TierReason`):
  * **Zero copy** - WebGPU renders directly into the texture behind the `SKSurface`; nothing is copied
  * **GPU copy** - the surface is on the same GPU device but its own texture cannot be shared: WebGPU renders into its own texture and SkiaSharp draws it onto the surface with one GPU-side draw, with no CPU readback
  * **Readback** - raster surfaces, surfaces on another device or on OpenGL, and canvases without a surface: the frame is copied to CPU memory and drawn onto the canvas
* Owning the GPU device: the library creates the WebGPU device on the platform's own GPU API - Vulkan on Linux and Android, Direct3D 12 (with Vulkan as an option) on Windows, Metal on macOS - and creates the SkiaSharp `GRContext` on that same device, so every surface the library creates on it is zero copy
* Adopting the application's device: an application that already has a Vulkan or Metal `GRContext` hands its backend context to the library, which wraps that native device for WebGPU - surfaces on the application's own context then reach the zero-copy and GPU-copy tiers
* Zero copy for surfaces the application created over its own GPU texture, when it tells the library which native texture backs the surface
* A small, explicit frame protocol - `BeginWebGpuPass` / `EndWebGpuPass` - that orders WebGPU and SkiaSharp work on the GPU and moves the texture between the two in both directions
* Headless rendering with no surface at all, read back as an `SKImage`, an `SKBitmap` or raw bytes (tight rows or rows of your own stride) - for tests, servers and screenshots
* Asynchronous, double-buffered read-back: `BeginReadPixels` issues a frame's copy and returns at once, `EndReadPixels` copies the pixels out later, so the next frame renders while the previous one is mapped; the number of staging buffers is configurable
* Fast copies out of mapped GPU memory, including on mobile GPUs whose read-back memory the CPU reads uncached
* Required device features, native engine features and limits for the owned device; a requirement the adapter cannot meet is never dropped - device creation reports each unmet feature or limit by name, with the adapter's value, so the caller can fall back
* The adapter's available features and limits next to the device's enabled ones, for owned and adopted devices, and device-loss reporting
* A readback counter, so an application (or a test) can prove a render loop never touches CPU memory
* Optional preservation of the surface's content in the copy tiers, so a WebGPU pass that loads instead of clearing sees what SkiaSharp drew
* Sharing through CodeBrix.WebGpu.Native's contracts: `WebGpuSkiaDevice` is an `IWebGpuDeviceSource` and `WebGpuSkiaTarget` an `IWebGpuTextureTarget`, so any library built on CodeBrix.WebGpu.Native that accepts them - for example CodeBrix.WebGpu.Graphics - shares the device and renders into the target in one call each, without either package referencing the other
* Reporting instead of crashing: a missing GPU API, adapter, device or format - or a format and usage the device rejects - makes the device or target unavailable with a reason string; a frame whose WebGPU commands fail validation is never submitted: `EndWebGpuPass` throws `WebGpuSkiaValidationException` with WebGPU's message and the target stays ready for the next frame

## Sample Code

### Zero copy: WebGPU and SkiaSharp drawing into the same pixels

```csharp
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia;
using SkiaSharp;

using WebGpuSkiaDevice gpu = WebGpuSkiaDevice.Create();
if (!gpu.IsAvailable) { Console.WriteLine(gpu.Reason); return; }

GRContext grContext = gpu.CreateGrContext();            // owned by gpu; do not dispose
using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu, grContext, 512, 512);
Console.WriteLine(target);                              // "ZeroCopy 512x512 RGBA8Unorm on Vulkan"

unsafe
{
    CommandEncoder encoder = target.BeginWebGpuPass();   // SkiaSharp's work is submitted first
    RenderPassColorAttachment attachment = new RenderPassColorAttachment
    {
        View = target.TextureView,
        DepthSlice = Constants.DepthSliceUndefined,
        LoadOp = LoadOp.Clear,
        StoreOp = StoreOp.Store,
        ClearValue = new Color { R = 0.1, G = 0.2, B = 0.4, A = 1 },
    };
    RenderPassDescriptor pass = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
    RenderPassEncoder renderPass = WebGpu.CommandEncoderBeginRenderPass(encoder, &pass);
    // ... set your pipeline and draw ...
    WebGpu.RenderPassEncoderEnd(renderPass);
    WebGpu.RenderPassEncoderRelease(renderPass);
    target.EndWebGpuPass();                              // submitted; the texture is SkiaSharp's again
}

using SKPaint paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
using SKFont font = new SKFont { Size = 24 };
target.Surface.Canvas.DrawText("drawn by SkiaSharp", 20, 40, SKTextAlign.Left, font, paint);
```

### Any surface you already have

```csharp
using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu, mySurface);   // or mySurface.Canvas
Console.WriteLine(target.Tier + ": " + target.TierReason);                  // why not a faster tier

CommandEncoder encoder = target.BeginWebGpuPass();
// ... record passes that render into target.TextureView ...
target.EndWebGpuPass();                                                    // the frame is on mySurface now
```

### Headless

```csharp
using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu, 256, 256);
CommandEncoder encoder = target.BeginWebGpuPass();
// ... render into target.TextureView ...
target.EndWebGpuPass();
using SKImage image = target.ReadImage();
```

### Required features and limits

```csharp
WebGpuSkiaDeviceOptions options = new WebGpuSkiaDeviceOptions();
options.RequiredFeatures.Add(FeatureName.TimestampQuery);
Limits limits = WebGpuSkiaLimits.Undefined();
limits.MaxTextureDimension2D = 16384;
options.RequiredLimits = limits;

using WebGpuSkiaDevice gpu = WebGpuSkiaDevice.Create(options);
if (!gpu.IsAvailable)
{
    foreach (WebGpuSkiaUnmetRequirement unmet in gpu.UnmetRequirements) Console.WriteLine(unmet.Message);
}
else
{
    Console.WriteLine(string.Join(", ", gpu.Features) + " enabled of " + string.Join(", ", gpu.AdapterFeatures));
}
```

### Asynchronous read-back

```csharp
byte[] pixels = new byte[target.Width * target.Height * 4];
WebGpuSkiaReadback previous = default;
for (int frame = 0; frame < frames; frame++)
{
    CommandEncoder encoder = target.BeginWebGpuPass();
    // ... render frame ...
    target.EndWebGpuPass();
    WebGpuSkiaReadback current = target.BeginReadPixels();        // returns at once
    if (previous.IsValid && target.EndReadPixels(previous, pixels)) Show(pixels);
    previous = current;
}

if (previous.IsValid) target.EndReadPixels(previous, pixels);
```

### Another WebGPU library on the same device and target

`WebGpuSkiaDevice` implements `IWebGpuDeviceSource` and `WebGpuSkiaTarget` implements `IWebGpuTextureTarget` (both defined in CodeBrix.WebGpu.Native), whose pass protocol is this package's `BeginWebGpuPass` / `EndWebGpuPass`. A library that accepts those contracts needs no reference to this one - with CodeBrix.WebGpu.Graphics, for example:

```csharp
using GraphicsContext graphics = GraphicsContext.Wrap(gpu);                 // the same device
using (GraphicsFrame frame = graphics.BeginFrame(target, depth))            // the target's WebGPU pass
{
    using (RenderPass pass = frame.BeginRenderPass(Color4.Black)) { /* draw */ }
    frame.Submit();                                                          // ends the pass
}

target.Surface.Canvas.DrawText(...);                                         // SkiaSharp on the same pixels
```

## Documentation

The NuGet package includes `AGENT-README.txt`, a complete API reference and usage guide written for AI coding agents - point your agent at that file when it is writing code against this library.

Additional sample code and usage examples are available in the `CodeBrix.WebGpu.Skia.Tests` project:
https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/tests/CodeBrix.WebGpu.Skia.Tests

## License

CodeBrix.WebGpu.Skia is licensed under the MIT License - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/THIRD-PARTY-NOTICES.txt).
