================================================================================
AGENT-README: CodeBrix.WebGpu.Native
A Guide for AI Coding Agents — CONSUMING the CodeBrix.WebGpu.Native.MitLicenseForever NuGet package
================================================================================

OVERVIEW
========

CodeBrix.WebGpu.Native is a THIN, GENERATED .NET binding over a native WebGPU
implementation (the wgpu engine) that ships inside the package, one native
library per runtime identifier. It targets .NET 10.

"Thin" is the design, not a shortcoming:

  * Every C function of the WebGPU C API, of the wgpu engine's native
    extensions and of the CodeBrix extras is ONE static method on the
    `WebGpu` class, a `LibraryImport` with exactly the C parameters.
  * Every C struct is a `[StructLayout(LayoutKind.Sequential)]` C# struct with
    exactly the C members in the C order. Enums, bitflags and constants carry
    the C values. Object handles are pointer-sized readonly structs.
  * Nothing is marshalled, nothing is hidden, nothing is allocated for you.
    Callbacks are `delegate* unmanaged[Cdecl]` function pointers. You write the
    same program you would write in C, in C#.
  * When the library loads, the size and alignment of EVERY generated struct
    is compared with what the native library reports. A mismatch refuses to
    load - a generated binding can never silently disagree with its native.

This is the first of three packages built from the same repository:
CodeBrix.WebGpu.Native (this package: the raw binding and the natives),
CodeBrix.WebGpu.Skia (shares one GPU device between WebGPU and a SkiaSharp
GRContext so WebGPU can draw straight into an SKSurface's texture), and
CodeBrix.WebGpu.Graphics (a high-level, high-performance graphics API). If you
want objects with Dispose, convenience overloads or a scene graph, those belong
in the higher packages - not here.

The binding is generated from the WebGPU C API's machine-readable YAML
specification, the engine's extension header, and a CodeBrix specification of
the extras; provenance is in THIRD-PARTY-NOTICES.txt.


INSTALLATION
============

  Package id:        CodeBrix.WebGpu.Native.MitLicenseForever
  Install:           dotnet add package CodeBrix.WebGpu.Native.MitLicenseForever
  Assembly:          CodeBrix.WebGpu.Native.dll
  Namespace:         CodeBrix.WebGpu.Native
  Target framework:  net10.0
  License:           MIT (the native libraries: see THIRD-PARTY-NOTICES.txt)
  Dependencies:      none besides .NET

The consuming project MUST allow unsafe code - almost every call takes a
pointer:

    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>

Native libraries (in the package under runtimes/<rid>/native/, each beside
LICENSE-WgpuNative.txt):

    win-x64, win-arm64          codebrix_wgpu.dll       Direct3D 12, Vulkan
    osx-x64, osx-arm64          libcodebrix_wgpu.dylib  Metal
    linux-x64, linux-arm64,     libcodebrix_wgpu.so     Vulkan, OpenGL
      linux-riscv64
    android-arm64, android-x64  libcodebrix_wgpu.so     Vulkan, OpenGL ES
                                (Android 13+, packed into the APK)

`CodeBrixWebGpuNative.BuildInfo.Backends` reports exactly what the loaded
library was built with. A GPU driver is still needed at run time (a Vulkan
loader + driver on Linux/Android/Windows-Vulkan, the system's Direct3D 12 or
Metal); without one, adapter requests simply find nothing.


KEY NAMESPACES / USINGS
=======================

    using CodeBrix.WebGpu.Native;            // everything public
    using System.Runtime.CompilerServices;   // CallConvCdecl (callbacks)
    using System.Runtime.InteropServices;    // UnmanagedCallersOnly, NativeMemory

`CodeBrix.WebGpu.Native` contains the generated types (WebGpu, Constants, the
enums/bitflags/structs/handles/callback-info structs) and the small
hand-written layer (CodeBrixWebGpuNative, WgpuString, WgpuUtf8String,
WgpuBuildInfo, StructLayoutInfo, StructLayoutCheck, WgpuException).

NAME CLASH: the handle type `Buffer` collides with `System.Buffer` when a file
has `using System;`. Inside your own code either alias it
(`using GpuBuffer = CodeBrix.WebGpu.Native.Buffer;`) or qualify it.


CORE API REFERENCE
==================

LOADING AND DIAGNOSTICS: static class CodeBrixWebGpuNative
-----------------------------------------------------------

    static void EnsureLoaded();
        Loads the native for this runtime identifier, refuses it unless its
        contract version equals Constants.ApiVersion, then checks every
        generated struct's size and alignment against the native. Call it
        once at start-up; cheap afterwards. Throws WgpuException (missing
        native - the message lists every path tried -, wrong contract version,
        or a layout mismatch listing the structs).
    static uint ExpectedApiVersion { get; }       // Constants.ApiVersion
    static uint ApiVersion { get; }               // what the native reports
    static string LoadedPath { get; }             // null before loading
    static string RuntimeIdentifier { get; }      // e.g. "linux-x64"
    static uint PackedLibraryVersion { get; }     // wgpuGetVersion(): 1 byte per part
    static Version LibraryVersion { get; }        // the same, unpacked
    static Version UnpackVersion(uint packed);
    static WgpuBuildInfo BuildInfo { get; }
    static IReadOnlyList<StructLayoutInfo> ManagedStructLayouts();
    static StructLayoutInfo? NativeStructLayout(string cName); // null if unknown
    static IReadOnlyList<StructLayoutCheck> CheckStructLayouts();
    static void VerifyStructLayouts();            // throws on any mismatch

    sealed record WgpuBuildInfo(uint ApiVersion, string WgpuNativeVersion,
        string WgpuCoreVersion, string Backends, string TargetTriple,
        string Profile);
    readonly record struct StructLayoutInfo(string CName, int Size, int Alignment)
        static StructLayoutInfo Of<T>(string cName) where T : unmanaged;
    sealed record StructLayoutCheck(StructLayoutInfo Managed, StructLayoutInfo? Native)
        bool Matches { get; }

    sealed class WgpuException : Exception    // load / contract / layout only

WebGPU errors are NOT exceptions - they come back exactly as in C: status
values, null handles, and the device's uncaptured-error callback.

The imports install the library resolver themselves (static constructor of
`WebGpu`), so a call before EnsureLoaded still finds the native - but only
EnsureLoaded checks the contract version and the layouts. Always call it.

THE BINDING: static unsafe partial class WebGpu
-----------------------------------------------

One method per C function: the C name without its `wgpu` prefix.

    wgpuCreateInstance                 -> WebGpu.CreateInstance
    wgpuDeviceCreateBuffer             -> WebGpu.DeviceCreateBuffer
    wgpuAdapterRelease                 -> WebGpu.AdapterRelease
    wgpuDevicePoll           (extension) -> WebGpu.DevicePoll
    wgpuCodebrixDeviceGetVulkanInfo (extra) -> WebGpu.CodebrixDeviceGetVulkanInfo

Parameters are the C parameters, in order, with C# names in camelCase. The C
documentation applies unchanged; the XML documentation of every member is
rendered from the specification's own text.

TYPE MAPPING (C -> C#)

    WGPUAdapter, WGPUDevice, ... (objects)  readonly struct Adapter, Device, ...
                                            { IntPtr Handle; IsNull; Null;
                                              Equals; ==; ToString }
    WGPUFoo (struct)                        struct Foo, same members, same order
    WGPUFoo (enum)                          enum Foo : uint, same values
    WGPUFoo (bitflag, WGPUFlags = uint64)   [Flags] enum Foo : ulong
    WGPUBool                                uint   (1 = true, 0 = false)
    size_t                                  nuint
    uint64_t / uint32_t / int32_t / float   ulong / uint / int / float
    T const * / T *                         T*
    void *                                  void*
    WGPUStringView                          StringView { byte* Data; nuint Length; }
    array member/argument                   nuint FooCount + T* Foo (count first)
    WGPUChainedStruct                       ChainedStruct { ChainedStruct* Next; SType SType; }
    extensible struct                       first field ChainedStruct* NextInChain
    extension struct                        first field ChainedStruct Chain, and
                                            const SType StructType (the tag to put
                                            in Chain.SType)
    WGPUFooCallbackInfo                     struct FooCallbackInfo { NextInChain;
                                            [Mode;] Callback; Userdata1; Userdata2 }
    WGPUFooCallback                         delegate* unmanaged[Cdecl]<..., void*, void*, void>
    WGPU_FOO constants                      Constants.Foo (Strlen and WholeMapSize are
                                            static readonly nuint; the rest const)

Enum members are the C entry names in PascalCase; a C entry name that starts
with a digit gets the enum's last word in front: TextureDimension.Dimension2D,
TextureViewDimension.Dimension2DArray.

Extended enums are merged: the wgpu extension tags of WGPUSType (InstanceExtras,
DeviceExtras, ...) are members of `SType`; NativeFeature, NativeLimits etc.
are separate types exactly as in the C header.

CHAINING AN EXTENSION STRUCT

    InstanceExtras extras = new InstanceExtras { Backends = InstanceBackend.Vulkan };
    extras.Chain.SType = InstanceExtras.StructType;
    InstanceDescriptor descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
    Instance instance = WebGpu.CreateInstance(&descriptor);

All structs in the chain must stay put during the call: locals (as above) or
pinned / unmanaged memory.

STRINGS: WgpuString and WgpuUtf8String

    static class WgpuString
        static StringView Null { get; }      // {null, Strlen}: "no value"
        static StringView Empty { get; }     // {null, 0}
        static bool IsNull(StringView view);
        static string ToManaged(StringView view);  // null view -> null
        static StringView FromPinned(byte* utf8, int length);
    sealed class WgpuUtf8String : IDisposable
        WgpuUtf8String(string value);        // unmanaged UTF-8 + NUL; null -> Null view
        StringView View { get; }
        int ByteLength { get; }

Text IN: a `fixed` UTF-8 literal is the cheapest:

    fixed (byte* entry = "main"u8)
        state.EntryPoint = WgpuString.FromPinned(entry, 4);

Text OUT (callback messages, AdapterInfo, ...): copy it with ToManaged inside
the callback / before the owning struct is freed. Views out of the API are not
guaranteed to be NUL-terminated.

THE ASYNC PATTERN (callback-info structs)

Every asynchronous function takes a FooCallbackInfo by value and returns a
Future. Fill the info with an [UnmanagedCallersOnly] Cdecl method and your user
data pointers, choose a CallbackMode, then pump:

    CallbackMode.AllowProcessEvents   callback runs inside
                                      WebGpu.InstanceProcessEvents(instance) or
                                      WebGpu.DevicePoll(device, wait, null)
    CallbackMode.AllowSpontaneous     may run on any thread at any time
    CallbackMode.WaitAnyOnly          needs InstanceWaitAny - NOT AVAILABLE,
                                      see COMMON PITFALLS

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnMapped(MapAsyncStatus status, StringView message, void* userdata1, void* userdata2)
        => *(int*)userdata1 = status == MapAsyncStatus.Success ? 1 : -1;

    int done = 0;
    BufferMapCallbackInfo info = new BufferMapCallbackInfo
    {
        Mode = CallbackMode.AllowProcessEvents, Callback = &OnMapped, Userdata1 = &done,
    };
    WebGpu.BufferMapAsync(buffer, MapMode.Read, 0, size, info);
    while (done == 0) WebGpu.DevicePoll(device, 1, null);   // 1 = wait

Adapter and device requests complete through WebGpu.InstanceProcessEvents.
To pass a managed object as user data, use GCHandle.ToIntPtr / FromIntPtr.
An exception must never escape an UnmanagedCallersOnly method.

ERRORS

Set DeviceDescriptor.UncapturedErrorCallbackInfo (Callback, Userdata1/2)
before requesting the device; validation and out-of-memory errors arrive there
with an ErrorType and a message. Error scopes (DevicePushErrorScope /
DevicePopErrorScope) work as in C.

THE CODEBRIX EXTRAS (native-device access)

All extras exist in every native on every platform (identical export set). A
function for a backend the platform or the device does not have returns
Status.Error (or a null handle) and writes nothing. Ask first:

    BackendType WebGpu.CodebrixDeviceGetBackend(Device device);
        // Vulkan, D3D12, Metal, OpenGL, OpenGLES; Undefined for a null device

Vulkan (Linux, Android, Windows-on-Vulkan):

    Status CodebrixDeviceGetVulkanInfo(Device, CodebrixVulkanDeviceInfo*);
        // VkInstance, VkPhysicalDevice, VkDevice, VkQueue, queue family/index,
        // instance API version, vkGetInstanceProcAddr, enabled instance and
        // device extensions (CodebrixName arrays). Owned by wgpu; valid while
        // the device lives.
    Status CodebrixTextureGetVulkanInfo(Texture, CodebrixVulkanTextureInfo*);
        // VkImage, VkFormat, sample count
    Texture CodebrixDeviceCreateTextureFromVulkanImage(Device, void* vkImage,
        TextureDescriptor*, CodebrixTextureState initialState);
    Status CodebrixInstanceAdoptVulkanDevice(Instance, CodebrixVulkanAdoptDescriptor*,
        Adapter* adapter, Device* device);
        // wraps an application-owned VkDevice as a wgpu adapter + device

Direct3D 12 (Windows): CodebrixDeviceGetD3d12Info, CodebrixTextureGetD3d12Info,
CodebrixDeviceCreateTextureFromD3d12Resource, CodebrixInstanceAdoptD3d12Device.

Metal (macOS): DeviceGetNativeMetalDevice, QueueGetNativeMetalCommandQueue,
TextureGetNativeMetalTexture (extension functions),
CodebrixDeviceCreateTextureFromMetalTexture, CodebrixInstanceAdoptMetalDevice.

Synchronisation with another renderer on the same device:

    Status CodebrixCommandEncoderTransitionTexture(CommandEncoder, Texture,
        CodebrixTextureState state);
        // records an explicit layout / resource-state transition of the whole
        // texture (ColorTarget, ShaderResource, CopySrc, CopyDst, Present, ...);
        // takes effect when the encoder's command buffer is submitted.

Contract/diagnostics: CodebrixGetApiVersion, CodebrixGetBuildInfo,
CodebrixGetStructLayout (all wrapped by CodeBrixWebGpuNative).


COMPLETE EXAMPLES
=================

Double 256 numbers on the GPU (compute) - the shape of every WebGPU program:

    using System;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Text;
    using CodeBrix.WebGpu.Native;
    using GpuBuffer = CodeBrix.WebGpu.Native.Buffer;

    unsafe
    {
        CodeBrixWebGpuNative.EnsureLoaded();
        InstanceDescriptor instanceDescriptor = default;
        Instance instance = WebGpu.CreateInstance(&instanceDescriptor);
        Adapter adapter = Gpu.RequestAdapter(instance);          // see below
        Device device = Gpu.RequestDevice(instance, adapter);    // see below
        Queue queue = WebGpu.DeviceGetQueue(device);

        byte[] wgsl = Encoding.UTF8.GetBytes(
            "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n" +
            "@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) id: vec3<u32>)" +
            " { data[id.x] = data[id.x] * 2u; }");
        ShaderModule module;
        fixed (byte* code = wgsl)
        {
            ShaderSourceWGSL source = new ShaderSourceWGSL { Code = WgpuString.FromPinned(code, wgsl.Length) };
            source.Chain.SType = ShaderSourceWGSL.StructType;
            ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &source.Chain };
            module = WebGpu.DeviceCreateShaderModule(device, &descriptor);
        }

        const ulong size = 256 * sizeof(uint);
        BufferDescriptor storageDescriptor = new BufferDescriptor
            { Usage = BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, Size = size };
        BufferDescriptor readDescriptor = new BufferDescriptor
            { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = size };
        GpuBuffer storage = WebGpu.DeviceCreateBuffer(device, &storageDescriptor);
        GpuBuffer readback = WebGpu.DeviceCreateBuffer(device, &readDescriptor);
        uint[] input = Enumerable.Range(0, 256).Select(i => (uint)i).ToArray();
        fixed (uint* data = input) WebGpu.QueueWriteBuffer(queue, storage, 0, data, (nuint)size);

        ComputePipeline pipeline;
        fixed (byte* entry = "main"u8)
        {
            ComputePipelineDescriptor descriptor = default;     // Layout null = automatic
            descriptor.Compute.Module = module;
            descriptor.Compute.EntryPoint = WgpuString.FromPinned(entry, 4);
            pipeline = WebGpu.DeviceCreateComputePipeline(device, &descriptor);
        }

        BindGroupLayout layout = WebGpu.ComputePipelineGetBindGroupLayout(pipeline, 0);
        BindGroupEntry entry0 = new BindGroupEntry { Binding = 0, Buffer = storage, Size = size };
        BindGroupDescriptor groupDescriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry0 };
        BindGroup group = WebGpu.DeviceCreateBindGroup(device, &groupDescriptor);

        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(device, null);
        ComputePassEncoder pass = WebGpu.CommandEncoderBeginComputePass(encoder, null);
        WebGpu.ComputePassEncoderSetPipeline(pass, pipeline);
        WebGpu.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
        WebGpu.ComputePassEncoderDispatchWorkgroups(pass, 4, 1, 1);
        WebGpu.ComputePassEncoderEnd(pass);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, storage, 0, readback, 0, size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(queue, 1, &commands);

        int done = 0;
        BufferMapCallbackInfo map = new BufferMapCallbackInfo
            { Mode = CallbackMode.AllowProcessEvents, Callback = &Gpu.OnMapped, Userdata1 = &done };
        WebGpu.BufferMapAsync(readback, MapMode.Read, 0, (nuint)size, map);
        while (done == 0) WebGpu.DevicePoll(device, 1, null);
        uint* result = (uint*)WebGpu.BufferGetConstMappedRange(readback, 0, (nuint)size);
        Console.WriteLine(result[255]);                          // 510
        WebGpu.BufferUnmap(readback);

        // Release everything you created, newest first (each Release drops one reference).
        WebGpu.CommandBufferRelease(commands);
        WebGpu.ComputePassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BindGroupRelease(group);
        WebGpu.BindGroupLayoutRelease(layout);
        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.BufferRelease(readback);
        WebGpu.BufferRelease(storage);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.QueueRelease(queue);
        WebGpu.DeviceRelease(device);
        WebGpu.AdapterRelease(adapter);
        WebGpu.InstanceRelease(instance);
    }

    static unsafe class Gpu
    {
        public static Adapter RequestAdapter(Instance instance)
        {
            nint result = 0;
            RequestAdapterOptions options = default;
            RequestAdapterCallbackInfo info = new RequestAdapterCallbackInfo
                { Mode = CallbackMode.AllowProcessEvents, Callback = &OnAdapter, Userdata1 = &result };
            WebGpu.InstanceRequestAdapter(instance, &options, info);
            while (result == 0) WebGpu.InstanceProcessEvents(instance);
            if (result == -1) throw new InvalidOperationException("No adapter.");
            return new Adapter(result);
        }

        public static Device RequestDevice(Instance instance, Adapter adapter)
        {
            nint result = 0;
            DeviceDescriptor descriptor = default;
            descriptor.UncapturedErrorCallbackInfo.Callback = &OnError;
            RequestDeviceCallbackInfo info = new RequestDeviceCallbackInfo
                { Mode = CallbackMode.AllowProcessEvents, Callback = &OnDevice, Userdata1 = &result };
            WebGpu.AdapterRequestDevice(adapter, &descriptor, info);
            while (result == 0) WebGpu.InstanceProcessEvents(instance);
            if (result == -1) throw new InvalidOperationException("No device.");
            return new Device(result);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnAdapter(RequestAdapterStatus status, Adapter adapter, StringView message, void* u1, void* u2)
            => *(nint*)u1 = status == RequestAdapterStatus.Success ? adapter.Handle : -1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnDevice(RequestDeviceStatus status, Device device, StringView message, void* u1, void* u2)
            => *(nint*)u1 = status == RequestDeviceStatus.Success ? device.Handle : -1;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void OnError(Device* device, ErrorType type, StringView message, void* u1, void* u2)
            => Console.Error.WriteLine(type + ": " + WgpuString.ToManaged(message));

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        public static void OnMapped(MapAsyncStatus status, StringView message, void* u1, void* u2)
            => *(int*)u1 = status == MapAsyncStatus.Success ? 1 : -1;
    }

The Vulkan objects behind a device (for handing to another Vulkan renderer):

    if (WebGpu.CodebrixDeviceGetBackend(device) == BackendType.Vulkan)
    {
        CodebrixVulkanDeviceInfo vk = default;
        if (WebGpu.CodebrixDeviceGetVulkanInfo(device, &vk) == Status.Success)
        {
            IntPtr vkDevice = (IntPtr)vk.Device;
            for (nuint i = 0; i < vk.EnabledDeviceExtensionCount; i++)
                Console.WriteLine(WgpuString.ToManaged(vk.EnabledDeviceExtensions[i].Name));
        }
    }


MINIMUM VIABLE PROJECT TEMPLATE
===============================

    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
        <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="CodeBrix.WebGpu.Native.MitLicenseForever" Version="*" />
      </ItemGroup>
    </Project>

    // Program.cs
    using System;
    using CodeBrix.WebGpu.Native;

    CodeBrixWebGpuNative.EnsureLoaded();
    Console.WriteLine(CodeBrixWebGpuNative.LibraryVersion + " " + CodeBrixWebGpuNative.BuildInfo.Backends);

(Pin a real version instead of "*" in anything you keep.)


PERFORMANCE TIPS
================

  * Calls are direct native calls with no marshalling; the cost is the WebGPU
    work itself. Keep descriptors on the stack (locals) - they are plain
    structs - and avoid per-call managed allocations in render loops.
  * Pass UTF-8 literals ("main"u8) with `fixed` instead of converting strings
    every frame; keep a WgpuUtf8String for text you reuse.
  * Submit work in few, large command buffers; use QueueSubmitForIndex +
    DevicePoll(device, 1, &index) to wait for one specific submission instead
    of the whole queue.
  * Map buffers with AllowProcessEvents and poll with wait = 0 from your frame
    loop rather than blocking with wait = 1.


COMMON PITFALLS TO AVOID
========================

  * NOT CALLING EnsureLoaded. Imports work without it, but only EnsureLoaded
    checks the contract version and every struct layout.
  * UNIMPLEMENTED FUNCTIONS ABORT THE PROCESS. The engine release this package
    wraps exports, but does not implement, these functions; calling one ends
    the process (it cannot be caught):
      GetProcAddress, InstanceWaitAny, DeviceCreateComputePipelineAsync,
      DeviceCreateRenderPipelineAsync, DeviceGetAdapterInfo,
      DeviceGetLostFuture, BufferGetMapState, BufferReadMappedRange,
      BufferWriteMappedRange, ShaderModuleGetCompilationInfo,
      InstanceGetWGSLLanguageFeatures, InstanceHasWGSLLanguageFeature,
      SupportedWGSLLanguageFeaturesFreeMembers, GetInstanceFeatures,
      HasInstanceFeature, SupportedInstanceFeaturesFreeMembers,
      TextureGetTextureBindingViewDimension, ExternalTextureAddRef,
      ExternalTextureRelease, and every *SetLabel function (set labels through
      the descriptors' Label instead).
    Use AdapterGetInfo (not DeviceGetAdapterInfo), synchronous pipeline
    creation, BufferGetConstMappedRange / BufferGetMappedRange, and the
    process-events callback pattern.
  * CallbackMode.WaitAnyOnly never fires (InstanceWaitAny is not available).
    Use AllowProcessEvents + InstanceProcessEvents / DevicePoll, or
    AllowSpontaneous.
  * Fatal native panics also abort the process: passing a null handle where a
    valid one is required ("invalid device"), or an invalid enum value, is not
    a recoverable error. Check IsNull on every handle you create.
  * QueueWriteBuffer / QueueWriteTexture are staged until the next QueueSubmit.
    Mapping the buffer without a submit in between reads the old contents.
  * Device loss is not delivered through DeviceLostCallbackInfo by this engine
    release: using a destroyed device reports a Validation error through the
    uncaptured-error callback instead.
  * DeviceCreateShaderModuleSpirV is SPIR-V PASSTHROUGH and needs a
    passthrough feature this release does not expose; it reports a validation
    error. Create SPIR-V modules through ShaderModuleDescriptor + a chained
    ShaderSourceSPIRV instead (validated and translated by the engine).
  * Struct and string memory must outlive the call: never pass the address of
    a field of a managed object without `fixed`; keep chained structs alive.
  * StringView length is in BYTES, not chars. Use FromPinned with the UTF-8
    byte count, or WgpuUtf8String.
  * WGPUBool is `uint`: pass 1/0, compare with 1/0.
  * Every Create* / Get* that returns an object handle gives you a reference;
    release it with the matching *Release. Struct results with API-allocated
    members (AdapterInfo, SupportedFeatures, ...) need their *FreeMembers.
  * A codebrix extra for another backend returns Status.Error - that is the
    documented answer, not a fault. Check CodebrixDeviceGetBackend first.
  * The callbacks must be static [UnmanagedCallersOnly(CallConvs =
    new[] { typeof(CallConvCdecl) })] methods whose parameters match the
    callback-info field's function pointer type exactly.


WHAT THIS PACKAGE DOES NOT DO
=============================

  * No object model, no IDisposable wrappers, no SafeHandles, no convenience
    overloads, no automatic lifetime management - by design (that is
    CodeBrix.WebGpu.Graphics).
  * No window or swap-chain creation of its own: surfaces are created from the
    platform's native window handles exactly as in C (SurfaceDescriptor + a
    chained SurfaceSource* struct).
  * No shader compilation tools beyond what the engine does at run time
    (WGSL, SPIR-V, GLSL through ShaderSourceGLSL).
  * No SkiaSharp integration (that is CodeBrix.WebGpu.Skia).
  * No support for platforms other than the nine runtime identifiers listed in
    INSTALLATION.


WORKING EXAMPLES ON GITHUB
==========================

  Loading, versions, build info, struct layouts:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/CodeBrixWebGpuNativeTests.cs
  Adapters, devices, callback pattern, compute, render-to-texture readback,
  WGSL/SPIR-V shader modules, uncaptured errors, submission-index polling:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/WebGpuTests.cs
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/GpuSession.cs
  The codebrix extras (Vulkan info, texture info, transitions, adopting a
  device into a second instance):
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/CodebrixExtrasTests.cs
  String views:
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/WgpuStringTests.cs


QUICK REFERENCE CARD
====================

  Load            CodeBrixWebGpuNative.EnsureLoaded()
  Version         CodeBrixWebGpuNative.LibraryVersion / .BuildInfo
  Call            WebGpu.<CNameWithoutWgpu>(...)        all unsafe, all raw
  Handle          Device d; d.IsNull; Device.Null; d.Handle (IntPtr)
  Bool            uint 1 / 0
  Array           FooCount (nuint) + Foo (T*)
  Chain           ext.Chain.SType = Ext.StructType; desc.NextInChain = &ext.Chain
  String in       fixed (byte* p = "x"u8) WgpuString.FromPinned(p, 1)
                  using var s = new WgpuUtf8String(text); s.View
  String out      WgpuString.ToManaged(view)
  Null string     WgpuString.Null
  Async           FooCallbackInfo { Mode = AllowProcessEvents, Callback = &Static,
                  Userdata1 = &state } ; pump InstanceProcessEvents / DevicePoll
  Errors          DeviceDescriptor.UncapturedErrorCallbackInfo.Callback
  Wait            ulong i = WebGpu.QueueSubmitForIndex(q, n, cmds);
                  WebGpu.DevicePoll(device, 1, &i)
  Backend         WebGpu.CodebrixDeviceGetBackend(device)
  Vulkan objects  WebGpu.CodebrixDeviceGetVulkanInfo(device, &info)
  Transition      WebGpu.CodebrixCommandEncoderTransitionTexture(enc, tex, state)
  Constants       Constants.WholeSize, Constants.Strlen, Constants.DepthSliceUndefined
  Free            *Release for handles, *FreeMembers for API-filled structs


================================================================================
END OF AGENT-README
================================================================================
