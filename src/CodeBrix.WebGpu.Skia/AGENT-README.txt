================================================================================
AGENT-README: CodeBrix.WebGpu.Skia
A Guide for AI Coding Agents — CONSUMING the CodeBrix.WebGpu.Skia.MitLicenseForever NuGet package
================================================================================

OVERVIEW
========

CodeBrix.WebGpu.Skia lets WebGPU code render into SkiaSharp surfaces. It sits
on top of CodeBrix.WebGpu.Native (the raw WebGPU binding: the static `WebGpu`
class, blittable structs, handle structs) and adds exactly two ideas:

  1. A DEVICE shared by WebGPU and SkiaSharp (`WebGpuSkiaDevice`). Either the
     library creates the GPU device and then a SkiaSharp `GRContext` on it
     (the OWNED route, primary), or it adopts the native Vulkan / Metal device
     behind the application's own `GRContext` (the ADOPTED route).
  2. A TARGET (`WebGpuSkiaTarget`): one WebGPU texture bound to one SkiaSharp
     surface (or canvas, or nothing), with a frame protocol that hands the
     texture back and forth, and a TIER that says how pixels travel:

       ZeroCopy   WebGPU renders into the very texture behind the SKSurface.
       GpuCopy    Same GPU device, but the surface's own texture cannot be
                  shared: WebGPU renders into its own texture and SkiaSharp
                  draws it onto the surface with one GPU-side draw. No CPU
                  readback.
       Readback   The frame is copied to a GPU buffer, mapped, and drawn onto
                  the canvas from CPU memory (raster surfaces, surfaces on
                  another device or OpenGL, canvases without a surface).
       Headless   No surface; read the texture back on request.
       Unavailable  Nothing works here; TierReason says why.

     The library picks the best tier the inputs allow and REPORTS it
     (`Tier`, `TierReason`), so an application can tell when its surface caps
     it at a copy tier and what to change.

Everything WebGPU (pipelines, buffers, shaders, render passes) is still the
raw CodeBrix.WebGpu.Native API - read that package's AGENT-README for it. This
package never wraps it. Another library built on CodeBrix.WebGpu.Native can
share the device and render into targets through the Native package's
sharing contracts (see SHARING WITH OTHER WEBGPU LIBRARIES).


INSTALLATION
============

  Package id:   CodeBrix.WebGpu.Skia.MitLicenseForever
  Install:      dotnet add package CodeBrix.WebGpu.Skia.MitLicenseForever
  Namespace:    CodeBrix.WebGpu.Skia
  Target:       net10.0 (any OS the native package supports: Windows x64/ARM64,
                macOS Intel/Apple Silicon, Linux x64/ARM64/RISC-V 64,
                Android ARM64/x64)
  Brings:       SkiaSharp and CodeBrix.WebGpu.Native.MitLicenseForever
                (with the WebGPU natives for every platform).
  You add:      the SkiaSharp native-assets package for Linux if you run there
                (SkiaSharp.NativeAssets.Linux) - SkiaSharp itself brings the
                Windows and macOS natives; on Android/iOS heads use the usual
                SkiaSharp workload packages.
  csproj:       <AllowUnsafeBlocks>true</AllowUnsafeBlocks> to record WebGPU
                commands (pointers to descriptor structs).

  Name clash to know about: inside a namespace that starts with
  `CodeBrix.WebGpu`, the identifier `WebGpu` resolves to the NAMESPACE
  CodeBrix.WebGpu, not the static class. Write `Native.WebGpu.Foo(...)` (or
  `CodeBrix.WebGpu.Native.WebGpu.Foo(...)`) there. Outside such namespaces
  `using CodeBrix.WebGpu.Native;` + `WebGpu.Foo(...)` works.


KEY NAMESPACES / USINGS
=======================

  using CodeBrix.WebGpu.Skia;     // WebGpuSkiaDevice, WebGpuSkiaTarget, enums, options
  using CodeBrix.WebGpu.Native;   // WebGpu, CommandEncoder, TextureView, structs ...
  using SkiaSharp;                // SKSurface, SKCanvas, GRContext ...


CORE API REFERENCE
==================

enum WebGpuSkiaBackend   None, Vulkan, Direct3D12, Metal
enum WebGpuSkiaDeviceRoute   Owned, Adopted
enum WebGpuSkiaTier      Unavailable(0) < Headless(1) < Readback(2) < GpuCopy(3) < ZeroCopy(4)

class WebGpuSkiaDeviceOptions
  PreferredBackend      WebGpuSkiaBackend   None = platform default
  AllowFallbackBackend  bool = true         try the platform's other backends
  PowerPreference       PowerPreference     passed to the adapter request
  ForceFallbackAdapter  bool                software adapter

class WebGpuSkiaDevice : IDisposable, IWebGpuDeviceSource
  static Create()                                    owned route, platform backend
  static Create(WebGpuSkiaDeviceOptions options)
  static AdoptVulkan(GRContext grContext, GRVkBackendContext backendContext,
                     IReadOnlyList<string> enabledInstanceExtensions,
                     IReadOnlyList<string> enabledDeviceExtensions,
                     uint instanceApiVersion = 0, uint queueIndex = 0)
  static AdoptMetal(GRContext grContext, GRMtlBackendContext backendContext)
  static AdoptDirect3D(GRContext grContext, GRD3DBackendContext backendContext)
                                                     ALWAYS unavailable with the
                                                     shipped natives (see
                                                     LIMITS); reports why
  bool IsAvailable; string Reason                    check before use
  WebGpuSkiaDeviceRoute Route; WebGpuSkiaBackend Backend; string AdapterName
  bool SharesQueueWithSkia     true: WebGPU and SkiaSharp submit to one native
                               queue (owned route; adopted Vulkan on the same
                               queue). false: every hand-over waits for the GPU
  Instance Instance; Adapter Adapter; Device Device; Queue Queue
                               raw handles for your WebGPU work; OWNED by the
                               device object - never release them
  GRContext GrContext          null until CreateGrContext (owned) / the app's
                               context (adopted)
  GRContext CreateGrContext()  owned route: creates (once) and returns the
                               SkiaSharp context ON THE WEBGPU DEVICE. The
                               device owns and disposes it. null +
                               GrContextReason when not possible
  string GrContextReason
  bool SharesDeviceWith(GRRecordingContext context)
  string[] TakeUncapturedErrors()   WebGPU validation errors (owned route)
  void Dispose()               disposes the owned GRContext, then the WebGPU
                               objects; every handle (Instance included) is
                               null afterwards. Dispose targets FIRST.

class WebGpuSkiaTargetOptions
  TextureFormat Format         Undefined = follow the surface (BGRA8Unorm for a
                               Bgra8888 surface, else RGBA8Unorm). Only
                               RGBA8Unorm and BGRA8Unorm are supported.
  TextureUsage ExtraUsage      added to RenderAttachment | TextureBinding |
                               CopySrc | CopyDst (e.g. StorageBinding). A
                               format/usage the device rejects (StorageBinding
                               on BGRA8Unorm without the BGRA8UnormStorage
                               feature) -> target Unavailable, TierReason
                               carries WebGPU's message
  bool PreserveSurfaceContent  copy tiers only: copy the surface into the
                               texture at every BeginWebGpuPass so a LoadOp.Load
                               pass sees SkiaSharp's pixels (ZeroCopy has this
                               for free). Default false.
  GRSurfaceOrigin Origin       TopLeft (WebGPU's own)

class WebGpuSkiaSurfaceBacking     the native texture behind an APP-created
                                   GPU surface (SkiaSharp cannot report it)
  IntPtr NativeTexture         VkImage / ID3D12Resource* / id<MTLTexture>
  TextureFormat Format         RGBA8Unorm or BGRA8Unorm
  TextureUsage Usage           the usages it was created with (as WebGPU
                               names them); must include RenderAttachment

class WebGpuSkiaTarget : IDisposable, IWebGpuTextureTarget
  static Create(WebGpuSkiaDevice device, SKSurface surface, options = null)
  static Create(WebGpuSkiaDevice device, SKSurface surface,
                WebGpuSkiaSurfaceBacking backing, options = null)
  static Create(WebGpuSkiaDevice device, SKCanvas canvas, options = null)
  static Create(WebGpuSkiaDevice device, GRContext context, int width,
                int height, options = null)          null context = headless
  static CreateHeadless(WebGpuSkiaDevice device, int width, int height,
                options = null)
  WebGpuSkiaDevice Device      the device it was created on
  WebGpuSkiaTier Tier; string TierReason; bool IsAvailable
  SKSurface Surface            the app's, or the target's own (owned by the
                               target when it created it)
  SKCanvas Canvas              Surface.Canvas, or the surface-less canvas
  int Width, Height; TextureFormat Format; SKColorType ColorType
  TextureUsage Usage
  Texture Texture; TextureView TextureView    render into these; owned by the
                               target - never release them
  long FrameCount              passes ended AND submitted (a rejected pass is
                               not counted)
  long ReadbackCount           GPU-to-CPU transfers this target performed
  bool IsInWebGpuPass
  string LastError             last non-fatal problem (failed map, rejected
                               pass's validation message), or null
  CommandEncoder BeginWebGpuPass()
  void EndWebGpuPass()         finishes the encoder inside a validation error
                               scope; invalid commands -> NOTHING submitted or
                               drawn, the pass is closed, the target stays
                               usable (Skia may draw, the next pass works) and
                               WebGpuSkiaValidationException is thrown
  bool ReadPixels(Span<byte> destination)   tight rows, Format byte order
  SKImage ReadImage();  SKBitmap ReadBitmap()
  string ToString()            e.g. "ZeroCopy 512x512 RGBA8Unorm on Vulkan"
  void Dispose()

class WebGpuSkiaValidationException : Exception
  string WebGpuMessage         WebGPU's own validation message (Message
                               contains it too). Thrown by EndWebGpuPass.


TIER SELECTION
==============

  Input                                               Tier
  -------------------------------------------------   ---------------------
  Create(device, device.CreateGrContext(), w, h)      ZeroCopy (surface is
                                                      created over the
                                                      WebGPU texture)
  Create(device, target.Surface) of such a target     ZeroCopy (same texture)
  Create(device, appSurface, backing)  app surface    ZeroCopy (the app's own
    on device.GrContext + its native texture           texture is wrapped)
  Create(device, appSurface)  surface on              GpuCopy
    device.GrContext (adopted: the app's context)
  Create(device, rasterSurface)                       Readback
  Create(device, surface on ANOTHER GRContext/GL)     Readback
  Create(device, canvas)                              the canvas's Surface
                                                      when it has one, else
                                                      Readback on the canvas
  Create(device, otherGrContext, w, h)                Readback (target owns a
                                                      GPU surface on it)
  Create(device, null, w, h) / CreateHeadless         Headless
  device unavailable / bad size / bad format          Unavailable + reason

Why an app's own surface on the shared context is GpuCopy, not ZeroCopy:
SkiaSharp has no API that returns the texture behind an SKSurface. Pass a
WebGpuSkiaSurfaceBacking (you created the texture, so you know it), or let
the library create the surface, to get ZeroCopy.


THE FRAME PROTOCOL
==================

  CommandEncoder encoder = target.BeginWebGpuPass();
      // 1. SkiaSharp's pending work is flushed and submitted (GPU order:
      //    SkiaSharp first). 2. The texture is handed to WebGPU.
  ... record render/compute/copy passes that write target.Texture /
      target.TextureView INTO `encoder` ...
  target.EndWebGpuPass();
      // The library finishes and submits `encoder`, hands the texture back
      // to SkiaSharp, and in the copy tiers draws the frame onto the canvas
      // (BlendMode.Src: replaces pixels, like a pass that clears).
  ... SkiaSharp draws on target.Surface.Canvas until the next Begin ...

Rules:
  * Record into the encoder you were given. Never finish, submit or release
    it; never submit other command buffers that touch the texture between
    Begin and End (they would run before the hand-over).
  * Queue writes (WebGpu.QueueWriteBuffer / QueueWriteTexture) are fine: they
    run at the start of the submit End makes.
  * ZeroCopy: between passes only DRAW into target.Surface. ReadPixels or
    Snapshot of the surface in the middle of a render loop moves the texture
    to a layout WebGPU cannot be told about (SkiaSharp exposes no way to set
    or read it back). Read with target.ReadPixels / ReadImage / ReadBitmap
    instead, or call SKSurface.ReadPixels only after the last frame.
  * One thread for a device, its GRContext and its targets (one native queue).
  * Dispose targets before their device. The device disposes the GRContext it
    created; an adopted GRContext stays yours.
  * Misuse throws: Begin twice, End without Begin, ReadPixels inside a pass ->
    InvalidOperationException; a disposed target -> ObjectDisposedException.
  * An unavailable target never throws for platform reasons: Begin returns a
    null encoder (check encoder.IsNull or target.IsAvailable), ReadPixels
    returns false, ReadImage/ReadBitmap return null.

What the library does on the GPU (for reviewers):
  ZeroCopy  Begin: GRContext.Flush + Submit; encoder gets two explicit
            transitions (texture -> copy-source -> colour-target) so the GPU
            orders SkiaSharp's writes before WebGPU's. End: transition to
            colour-target (the state SkiaSharp's surface was created in);
            submit.
  GpuCopy   Begin: flush + submit; transition to colour-target. End:
            transition to shader-read; submit; SkiaSharp draws a cached
            SKImage of the texture (told it is in shader-read) onto the
            surface.
  Readback  End: copy texture -> buffer in the same submit; map; draw onto
            the canvas straight from mapped memory (GPU surfaces are flushed
            before unmapping; surface-less canvases get a copy).
  Not sharing a queue (adopted Metal): Skia submits synchronously and WebGPU
  submits are followed by a device wait.


COMPLETE EXAMPLES
=================

1) OWNED ROUTE, ZERO COPY (the fast path)

  using CodeBrix.WebGpu.Native;
  using CodeBrix.WebGpu.Skia;
  using SkiaSharp;

  using WebGpuSkiaDevice gpu = WebGpuSkiaDevice.Create();
  if (!gpu.IsAvailable) throw new Exception(gpu.Reason);
  GRContext gr = gpu.CreateGrContext();
  if (gr == null) throw new Exception(gpu.GrContextReason);

  using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu, gr, 800, 600);
  // target.Tier == WebGpuSkiaTier.ZeroCopy

  unsafe void Frame()
  {
      CommandEncoder encoder = target.BeginWebGpuPass();
      RenderPassColorAttachment color = new RenderPassColorAttachment
      {
          View = target.TextureView,
          DepthSlice = Constants.DepthSliceUndefined,
          LoadOp = LoadOp.Load,          // keeps what SkiaSharp drew
          StoreOp = StoreOp.Store,
      };
      RenderPassDescriptor desc = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };
      RenderPassEncoder pass = WebGpu.CommandEncoderBeginRenderPass(encoder, &desc);
      WebGpu.RenderPassEncoderSetPipeline(pass, myPipeline);   // format = target.Format
      WebGpu.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
      WebGpu.RenderPassEncoderEnd(pass);
      WebGpu.RenderPassEncoderRelease(pass);
      target.EndWebGpuPass();

      target.Surface.Canvas.DrawCircle(400, 300, 50, myPaint); // on top
  }

  Show it: draw target.Surface onto your window surface only if that window
  surface is also on `gr`; otherwise read it (target.ReadImage) after the
  frame. For zero copy all the way to the screen, create the window's
  SKSurface on `gr` over the swap-chain image and use the backing overload.

2) ANY SURFACE (tier chosen for you)

  using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu, surface);
  Console.WriteLine(target.Tier + " - " + target.TierReason);
  CommandEncoder e = target.BeginWebGpuPass();
  ... record ...
  target.EndWebGpuPass();          // GpuCopy/Readback draw onto `surface` here

3) ADOPTED ROUTE, VULKAN (the app owns the GRContext)

  // The app created `vkBackend` (GRVkBackendContext) and `appGr` from it, and
  // knows which extensions it enabled when it created the instance/device.
  using WebGpuSkiaDevice gpu = WebGpuSkiaDevice.AdoptVulkan(appGr, vkBackend,
      instanceExtensions, deviceExtensions, instanceApiVersion: myApiVersion);
  if (!gpu.IsAvailable) Console.WriteLine(gpu.Reason);   // e.g. missing extensions
  using WebGpuSkiaTarget layer = WebGpuSkiaTarget.Create(gpu, appGr, 512, 512); // ZeroCopy
  using WebGpuSkiaTarget onApp = WebGpuSkiaTarget.Create(gpu, appSurface);      // GpuCopy

  The VkDevice must have been created with what the WebGPU engine needs (its
  usual device extensions and features, a graphics+compute queue);
  otherwise AdoptVulkan is unavailable with the reason. instanceApiVersion is
  the VkApplicationInfo.apiVersion the app used (0 = the loader's version).
  queueIndex is the index of backendContext.VkQueue inside its family (0 by
  default); when it matches, SharesQueueWithSkia is true.

4) ADOPTED ROUTE, METAL (macOS)

  using WebGpuSkiaDevice gpu = WebGpuSkiaDevice.AdoptMetal(appGr, mtlBackend);
  // WebGPU gets its own command queue on the app's MTLDevice:
  // SharesQueueWithSkia == false, every hand-over waits for the GPU.

5) APP-CREATED SURFACE OVER ITS OWN TEXTURE -> ZERO COPY

  SKSurface s = SKSurface.Create(gr, myBackendTexture, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);
  var backing = new WebGpuSkiaSurfaceBacking
  {
      NativeTexture = myVkImage,   // or ID3D12Resource* / id<MTLTexture>
      Format = TextureFormat.RGBA8Unorm,
      Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding
            | TextureUsage.CopySrc | TextureUsage.CopyDst,
  };
  using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu, s, backing);
  // ZeroCopy; the surface's content at creation is NOT preserved (the native
  // wraps the texture in an undefined state). Falls back to GpuCopy/Readback
  // with the reason when the texture cannot be wrapped.

6) HEADLESS (tests, servers, screenshots)

  using WebGpuSkiaTarget t = WebGpuSkiaTarget.CreateHeadless(gpu, 256, 256);
  CommandEncoder e = t.BeginWebGpuPass();
  ... render into t.TextureView ...
  t.EndWebGpuPass();
  using SKBitmap bmp = t.ReadBitmap();       // or ReadImage / ReadPixels(span)

7) PROVING A LOOP NEVER READS BACK

  long before = target.ReadbackCount;
  for (int i = 0; i < 100; i++) { Frame(); }
  Debug.Assert(target.ReadbackCount == before);   // ZeroCopy / GpuCopy


SHARING WITH OTHER WEBGPU LIBRARIES
===================================

CodeBrix.WebGpu.Native defines two contracts; this package implements them,
so a library that accepts them shares the device and draws into a target
without either package referencing the other:

  WebGpuSkiaDevice : IWebGpuDeviceSource
    Instance, Adapter, Device, Queue   the device's own handles (no
                                       references taken; consumers AddRef)
    Backend (BackendType)              Undefined when unavailable/disposed
    HasUncapturedErrorHandler          true for the OWNED route (errors go to
                                       TakeUncapturedErrors); false for an
                                       ADOPTED device (uncaptured errors are
                                       fatal) and when unavailable
  WebGpuSkiaTarget : IWebGpuTextureTarget
    Texture, TextureView, Format, Width, Height, Usage   as above
    SampleCount                        always 1
    IsInWebGpuPass, BeginWebGpuPass(), EndWebGpuPass()   THE FRAME PROTOCOL
                                       (EndWebGpuPass validates first; see
                                       LIMITS)

The interface's pass protocol IS the frame protocol: every WebGPU use of the
texture (render, copy, sample) goes into the encoder BeginWebGpuPass hands
out; between passes SkiaSharp owns the pixels (ZeroCopy: only DRAW on the
surface between passes).

One call each with CodeBrix.WebGpu.Graphics, for example (a separate
package; install it only if you want its API):

  using GraphicsContext graphics = GraphicsContext.Wrap(device);
  using (GraphicsFrame frame = graphics.BeginFrame(target, depthTexture))
  {
      using (RenderPass pass = frame.BeginRenderPass(Color4.Black)) { ... }
      frame.Submit();                 // calls target.EndWebGpuPass(); invalid
                                      // commands -> WebGpuSkiaValidationException
  }
  target.Surface.Canvas.DrawText(...);   // SkiaSharp on the same pixels

The consumer takes its own references on the handles, so it may be disposed
before or after the device and target. A target of another size is a new
target with new handles.


NATIVE MODULES
==============

Native code (Rust, C, C++) can render into a target too, on the same device:
CodeBrix.WebGpu.Native ships the C headers, a function table and two
versioned hand-off structs for it (its AGENT-README, NATIVE MODULES, has the
linking rules per operating system). The Skia types plug straight in:

  CodebrixDeviceHandoff.Fill(gpu, deviceHandoff);        // WebGpuSkiaDevice
  CommandEncoder e = CodebrixFrameHandoff.BeginWebGpuPass(target, frameHandoff);
  if (!e.IsNull)
  {
      MyModule.Frame(frameHandoff);   // records into frameHandoff->CommandEncoder
      target.EndWebGpuPass();         // validates + submits; may throw
  }

Every rule of THE FRAME PROTOCOL applies to the module's commands (record
only into the pass encoder, close its own passes, never finish or submit;
one thread). Flags carries FlagUncapturedErrorHandler for the OWNED route
only: on an ADOPTED device a module's uncaptured error ends the process.


PER-PLATFORM BACKENDS
=====================

  Platform        Owned route (Create)            Adopted route
  -------------   -----------------------------   ---------------------------
  Linux/Android   Vulkan -> ZeroCopy               AdoptVulkan -> ZeroCopy/GpuCopy
  Windows         Direct3D 12 (default), Vulkan    AdoptVulkan works;
                  (PreferredBackend = Vulkan)      AdoptDirect3D is unavailable
                  -> ZeroCopy on both              (use the owned route)
  macOS           Metal -> ZeroCopy                AdoptMetal -> ZeroCopy/GpuCopy
  Anything else / OpenGL GRContext / raster      Readback


LIMITS
======

  * Adopting an application's Direct3D 12 device is not possible with the
    shipped natives: AdoptDirect3D always reports unavailable. On Windows let
    the library own the device (Create + CreateGrContext) for zero copy, or
    adopt a Vulkan GRContext.
  * SkiaSharp cannot report the texture behind an SKSurface or SKImage, nor
    the Vulkan layout / D3D12 state it leaves a texture in. Hence the
    GpuCopy tier for app surfaces, the backing overload, and the ZeroCopy
    rule "only draw into the surface between passes".
  * SkiaSharp cannot list the extensions inside a GRVkExtensions object, so
    AdoptVulkan takes the extension lists as arguments.
  * SkiaSharp is told the device has NO optional Vulkan features enabled and
    is limited to the oldest Vulkan core version it supports on the owned route (correct and safe; SkiaSharp's
    optional features are performance extras).
  * Formats: RGBA8Unorm and BGRA8Unorm (premultiplied alpha), one mip level,
    one sample.
  * The WebGPU engine ends the process when invalid work is SUBMITTED. The
    library checks first: every texture/view/buffer it creates is created in
    a validation error scope (a rejected one makes the target Unavailable with
    WebGPU's message; nothing is submitted), and EndWebGpuPass finishes the
    pass's encoder in an error scope and throws WebGpuSkiaValidationException
    instead of submitting. This holds for adopted devices too, which have no
    uncaptured-error handler. The check costs one error-scope push/pop per
    pass (no GPU wait). Still fatal: work you submit YOURSELF (another
    encoder, QueueSubmit) and uncaptured errors on an ADOPTED device outside
    the library's calls.


COMMON PITFALLS TO AVOID
========================

  * Disposing gpu.GrContext yourself - the device owns it (owned route).
  * Releasing target.Texture / TextureView or the device's handles - owned.
  * Submitting your own command buffer that writes target.Texture instead of
    recording into the encoder from BeginWebGpuPass (it is not validated -
    an invalid one ends the process).
  * Ignoring WebGpuSkiaValidationException from EndWebGpuPass: the frame was
    NOT rendered (the surface keeps its previous pixels); fix the commands.
  * ExtraUsage = StorageBinding with Format = BGRA8Unorm: unavailable unless
    the device has the BGRA8UnormStorage feature - use RGBA8Unorm for
    storage writes.
  * Calling SKSurface.ReadPixels / Snapshot on a ZeroCopy surface inside the
    render loop (see THE FRAME PROTOCOL). Use target.ReadPixels.
  * A pipeline whose colour target format differs from target.Format.
  * Using the device from two threads.
  * Expecting a LoadOp.Load pass in the copy tiers to see SkiaSharp's pixels
    without PreserveSurfaceContent = true.
  * Forgetting the SkiaSharp Linux native assets package in the application
    (GRContext/SKSurface creation fails with a DllNotFoundException from
    SkiaSharp, not from this library).
  * Writing `WebGpu.Foo` inside a `CodeBrix.WebGpu.*` namespace (resolves to
    the namespace - use `Native.WebGpu.Foo`).


WHAT THIS PACKAGE DOES NOT DO
=============================

  * No WebGPU object model - pipelines, shaders and passes are the raw
    CodeBrix.WebGpu.Native API.
  * No window, swap chain or presentation.
  * No OpenGL interop (an OpenGL GRContext gets the Readback tier).
  * No SkiaSharp native assets.


WORKING EXAMPLES ON GITHUB
==========================

  Owned route, zero copy, pixel order WebGPU -> SkiaSharp -> WebGPU:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/ZeroCopyTests.cs
  GpuCopy and Readback tiers, PreserveSurfaceContent, surface-less canvas:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/CopyTierTests.cs
  Tier selection for every input kind:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/TierSelectionTests.cs
  Adopted route (Vulkan; Metal on macOS):
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/AdoptedDeviceTests.cs
  Zero copy over an application's own texture:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/SurfaceBackingTests.cs
  Headless, ReadPixels/ReadImage/ReadBitmap, protocol misuse:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/HeadlessTests.cs
  Devices, backends, reasons, uncaptured errors:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/WebGpuSkiaDeviceTests.cs
  Frames per second per tier:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/TimingTests.cs
  A scissored solid-colour WGSL pipeline recorded into a target's encoder:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Skia.Tests/WebGpuWork.cs


QUICK REFERENCE CARD
====================

  Device        var gpu = WebGpuSkiaDevice.Create();  gpu.IsAvailable / gpu.Reason
  Skia context  GRContext gr = gpu.CreateGrContext();   (owned by gpu)
  Adopt         WebGpuSkiaDevice.AdoptVulkan(gr, vkCtx, instExt, devExt)
                WebGpuSkiaDevice.AdoptMetal(gr, mtlCtx)
  Zero copy     WebGpuSkiaTarget.Create(gpu, gr, w, h)       -> target.Surface
  Any surface   WebGpuSkiaTarget.Create(gpu, surface | canvas)
  App texture   WebGpuSkiaTarget.Create(gpu, surface, new WebGpuSkiaSurfaceBacking {...})
  Headless      WebGpuSkiaTarget.CreateHeadless(gpu, w, h)
  Which tier    target.Tier, target.TierReason, target.ToString()
  Frame         var e = target.BeginWebGpuPass(); ...record into e...; target.EndWebGpuPass();
  Render into   target.TextureView (format target.Format)
  Read          target.ReadPixels(span) / ReadImage() / ReadBitmap()
  Count         target.ReadbackCount, target.FrameCount
  Errors        gpu.TakeUncapturedErrors(); EndWebGpuPass throws
                WebGpuSkiaValidationException (nothing submitted)
  Order         dispose targets, then the device


================================================================================
END OF AGENT-README
================================================================================
