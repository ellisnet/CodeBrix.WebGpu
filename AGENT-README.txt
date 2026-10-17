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
CodeBrix.WebGpu.Graphics (a graphics layer: one context object owning or
sharing the device, tracked resources, pipeline builders, a 2D sprite batch,
3D meshes and glTF models with physically based shading, animation and
image-based lighting, headless targets and window surfaces). If you want
objects with Dispose, convenience overloads or ready-made renderers, those
belong in the higher packages - not here. CodeBrix.WebGpu.Skia is published
from this repository as CodeBrix.WebGpu.Skia.MitLicenseForever; its own guide
is src/CodeBrix.WebGpu.Skia/AGENT-README.txt (also inside that package).
CodeBrix.WebGpu.Graphics is published as
CodeBrix.WebGpu.Graphics.MitLicenseForever; its own guide is
src/CodeBrix.WebGpu.Graphics/AGENT-README.txt (also inside that package).

Besides the generated binding, the package carries two hand-written sharing
contracts (IWebGpuDeviceSource, IWebGpuTextureTarget - see SHARING
CONTRACTS) through which the higher packages, or your own libraries, share one
device and one texture without referencing each other.
It also ships the C ABI (headers, a generated function table, versioned
hand-off structs, a crate-free Rust file) for NATIVE MODULES: separately built
Rust / C / C++ code that works on the very same WebGPU objects.

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

C headers and a Rust file for native modules are in the package under
include/ (see NATIVE MODULES); NuGet copies nothing from there into an
application.

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
WgpuBuildInfo, StructLayoutInfo, StructLayoutCheck, WgpuException, the
sharing contracts and the native-module types CodebrixProcTable,
CodebrixDeviceHandoff, CodebrixFrameHandoff).

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
    static CodebrixProcTable* GetProcTable();     // function table for native
                                                  // modules (see NATIVE MODULES)
    static CodebrixProcStatus GetProcStatus(string symbol);
        // what the LOADED native knows about a function: Implemented,
        // AbortsUnimplemented, AbortsOnError, Unsupported,
        // NotAvailableOnBackend; Unknown for a symbol not in the table
    static int ProcOrdinal(string symbol);        // index in CodebrixProcTable.Symbols, or -1

    sealed record WgpuBuildInfo(uint ApiVersion, string WgpuNativeVersion,
        string WgpuCoreVersion, string Backends, string TargetTriple,
        string Profile, string Patches);
        // Patches: the native fix set - comma-separated names of the patches
        // the engine was built with; compare it (never the export set) to tell
        // a fixed native from an older one
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
before requesting the device; validation, out-of-memory and internal errors
arrive there with an ErrorType and a message. Error scopes (DevicePushErrorScope /
DevicePopErrorScope) work as in C, with all three filters (Validation,
OutOfMemory, Internal); popping an empty stack reports PopErrorScopeStatus.Error.
A DevicePoll wait that fails (a submission index this device never returned)
returns 0 and reports the reason through the same path (InstanceProcessEvents
reaches no device, so a polling failure there is only logged; use
WebGpu.PollDevice for a structured result).

THE CODEBRIX EXTRAS (native-device access)

All extras exist in every native on every platform (identical export set). A
function for a backend the platform or the device does not have returns
Status.Error (or a null handle) and writes nothing. Ask first:

    BackendType WebGpu.CodebrixDeviceGetBackend(Device device);
        // Vulkan, D3D12, Metal, OpenGL, OpenGLES; Undefined for a null device

Vulkan (Linux, Android, Windows-on-Vulkan):

    Status CodebrixDeviceGetVulkanInfo(Device, CodebrixVulkanDeviceInfo*);
        // VkInstance, VkPhysicalDevice, VkDevice, VkQueue, queue family/index,
        // InstanceApiVersion = the apiVersion vkCreateInstance was given (wgpu's
        // own instances: 1.0 when the loader is below 1.1, otherwise exactly
        // 1.3, also on a 1.4 loader; adopted devices: the descriptor's value -
        // what Skia's MaxAPIVersion and another adopt descriptor expect),
        // vkGetInstanceProcAddr, enabled instance and device extensions
        // (CodebrixName arrays). Owned by wgpu; valid while the device lives.
    Status CodebrixTextureGetVulkanInfo(Texture, CodebrixVulkanTextureInfo*);
        // VkImage, VkFormat, sample count
    Texture CodebrixDeviceCreateTextureFromVulkanImage(Device, void* vkImage,
        TextureDescriptor*, CodebrixTextureState initialState);
    Status CodebrixInstanceAdoptVulkanDevice(Instance, CodebrixVulkanAdoptDescriptor*,
        Adapter* adapter, Device* device);
        // wraps an application-owned VkDevice as a wgpu adapter + device
    sealed class VulkanAdoptRequirements : IDisposable
        static VulkanAdoptRequirements Query(CodebrixVulkanAdoptDescriptor*);
        IReadOnlyList<string> RequiredDeviceExtensions { get; }
        nint PhysicalDeviceFeatures2 { get; }   // VkPhysicalDeviceFeatures2* + pNext chain
        uint PhysicalDeviceApiVersion { get; }
        // BEFORE creating your own VkDevice for adoption: fill the adopt
        // descriptor's instance fields + RequiredFeatures, ask, then create the
        // device with those extensions and that chain in VkDeviceCreateInfo.pNext
        // (pEnabledFeatures NULL; add your own features, never fewer). The chain
        // is owned until Dispose. Raw: CodebrixVulkanGetAdoptRequirements +
        // CodebrixVulkanAdoptRequirementsFreeMembers.

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

Capabilities beyond AdapterGetFeatures / AdapterGetLimits (which stay the
source for WebGPU features and limits):

    CodebrixDownlevelCapabilities WebGpu.GetDownlevelCapabilities(Adapter);
        // Flags (CodebrixDownlevelFlags: ComputeShaders, FragmentWritableStorage,
        // IndirectExecution, ...), ShaderModel (Sm2 / Sm4 / Sm5),
        // WebgpuCompliant (1 = the full WebGPU baseline)
    CodebrixTextureFormatFeatures WebGpu.GetTextureFormatFeatures(Adapter, TextureFormat);
        // AllowedUsages (TextureUsage) + Flags (Filterable, MultisampleX2..X16,
        // MultisampleResolve, StorageReadOnly / WriteOnly / ReadWrite / Atomic,
        // Blendable); both throw WgpuException for a null adapter / unknown format
    (raw: CodebrixAdapterGetDownlevelCapabilities, CodebrixAdapterGetTextureFormatFeatures)

Bounded, NON-ABORTING poll:

    CodebrixPollResult WebGpu.PollDevice(Device, bool wait, TimeSpan timeout,
        ulong? submissionIndex = null);
        // QueueEmpty, WaitSucceeded, Poll (no-wait poll, work in flight),
        // Timeout, DeviceLost, InvalidSubmissionIndex (an index this device
        // never returned), Error. Never ends the process; callbacks of finished
        // work run during the call. timeout <= 0 waits without limit.
    (raw: Status CodebrixDevicePoll(Device, uint wait, ulong timeoutNs,
        ulong* submissionIndex, CodebrixPollResult* result))

Fault listeners (a host-owned fault dispatcher on a device someone else owns):

    sealed class WebGpuFaultListener : IDisposable
        static WebGpuFaultListener Attach(Device device);
        event Action<ErrorType, string> UncapturedError;
        event Action<DeviceLostReason, string> DeviceLost;
        ulong Id { get; }
        // Runs AFTER the device's own callbacks, never instead of them (with
        // the engine's default handler - which ends the process - just before
        // it). DeviceLost also fires once when PollDevice first sees the device
        // lost. Raised on the thread that found the fault with the device's
        // error sink locked: keep handlers short, never call WebGpu functions
        // on that device or dispose a listener inside them. Dispose = removed;
        // no handler runs afterwards.
    (raw: ulong CodebrixDeviceAddFaultListener(Device, CodebrixFaultCallbacks*)
        -> listener id, 0 on failure; Status CodebrixDeviceRemoveFaultListener(Device, ulong))

Contract/diagnostics: CodebrixGetApiVersion, CodebrixGetBuildInfo,
CodebrixGetStructLayout, CodebrixGetProcStatus (all wrapped by
CodeBrixWebGpuNative).


SHARING CONTRACTS (hand-written, not generated)
===============================================

Two small interfaces let libraries that build on this binding share one GPU
device and one texture WITHOUT referencing each other. The owner implements
them; a consumer accepts them. CodeBrix.WebGpu.Skia implements both
(WebGpuSkiaDevice, WebGpuSkiaTarget); CodeBrix.WebGpu.Graphics accepts both
(GraphicsContext.Wrap(source), GraphicsContext.BeginFrame(target, depth),
RenderTargetView.From(target, depth), GraphicsContext.WrapTexture(target)),
and its GraphicsContext is itself an IWebGpuDeviceSource. Implement them for
your own renderer to plug into the same consumers.

    interface IWebGpuDeviceSource
        Instance Instance { get; }     // may be null
        Adapter Adapter { get; }       // may be null
        Device Device { get; }         // null = no usable device
        Queue Queue { get; }           // the queue the owner submits to
        BackendType Backend { get; }   // Undefined without a device
        bool HasUncapturedErrorHandler { get; }
            // false = the engine's default handler: any uncaptured error ENDS
            // THE PROCESS (see LIMITS); check risky work in error scopes

    interface IWebGpuTextureTarget
        Texture Texture { get; }  TextureView TextureView { get; }
        TextureFormat Format { get; }  int Width { get; }  int Height { get; }
        int SampleCount { get; }       // pipelines and depth must match
        TextureUsage Usage { get; }    // always includes RenderAttachment
        bool IsInWebGpuPass { get; }
        CommandEncoder BeginWebGpuPass();  // null encoder = unavailable
        void EndWebGpuPass();

  Ownership: the source / target owns every handle; reading a property takes
  NO reference. Handles stay valid until the owner is disposed. A consumer
  that keeps one longer takes its own reference (*AddRef) and releases only
  that - then owner and consumer may be disposed in either order.

  The pass protocol: BeginWebGpuPass makes the texture ready for WebGPU and
  returns an encoder the TARGET owns. Record EVERY command that uses the
  texture (render into it, copy it, sample it) into that encoder, never
  finish / submit / release it yourself, and close your own passes before
  EndWebGpuPass, which finishes and submits it and hands the texture back to
  the other renderer. Between passes no WebGPU work may touch the texture.
  One thread for the device and everything shared on it. An implementation
  should finish the encoder in a validation error scope and, on an error,
  submit nothing, close the pass and throw (an invalid submission ends the
  process).


NATIVE MODULES (Rust / C / C++ code on the same WebGPU objects)
===============================================================

A native module is native code built separately (Rust, C, C++), wrapped in
its own .NET assembly, that works DIRECTLY on the instance, device, queue and
textures this package - or a library built on it (CodeBrix.WebGpu.Skia, your
own IWebGpuDeviceSource / IWebGpuTextureTarget) - owns. No copies, no second
device, no translation: every module call goes into the same codebrix_wgpu
library .NET calls.

THE ONE-LIBRARY-PER-PROCESS RULE. A WGPUDevice / WGPUTexture handle is a
pointer into the state of the ONE codebrix_wgpu library that created it; it
means nothing to any other copy. So the process loads codebrix_wgpu exactly
once - the one from this package. A module never bundles or statically links
wgpu (the Rust `wgpu` / `wgpu-core` crates, another wgpu-native build) or a
second codebrix_wgpu; its .NET package ships only the module binary and
depends on CodeBrix.WebGpu.Native.MitLicenseForever.

WHAT THE PACKAGE SHIPS FOR MODULES (include/ at the package root, e.g.
~/.nuget/packages/codebrix.webgpu.native.mitlicenseforever/<version>/include/;
NuGet copies none of it into applications):

    webgpu.h                 the WebGPU C API (BSD-3-Clause)
    wgpu.h                   the engine's extensions (MIT); includes webgpu.h
    codebrix_wgpu.h          the codebrix extras (WGPU_CODEBRIX_API_VERSION)
    codebrix_wgpu_procs.h    WGPUCodebrixProcTable + the hand-off structs
                             (generated; _Static_assert-checked layouts)
    codebrix_wgpu_procs.rs   the whole C ABI for Rust with NO crates: every
                             constant, handle, enum, bitflag, callback and
                             #[repr(C)] struct (C member names), the table,
                             the hand-off structs, layout asserts, and behind
                             `--cfg codebrix_wgpu_direct` an extern block of
                             every function (module `direct`)
    LICENSE-WgpuNative.txt   the licences of webgpu.h and wgpu.h
  The libraries to link against are the package's runtimes/<rid>/native/ files.
  Rust: #[allow(non_camel_case_types, non_snake_case, non_upper_case_globals,
  dead_code)] #[path = "codebrix_wgpu_procs.rs"] mod wgpu_ffi;

WHICH ROUTE
  1. DIRECT DYNAMIC LINKING (the usual choice). The module declares the
     functions of the headers (C: #include "codebrix_wgpu.h"; Rust: the
     `direct` module of codebrix_wgpu_procs.rs) and links to codebrix_wgpu.
     Zero extra hops. Needs the LOAD-ORDER RULE below.
  2. FUNCTION TABLE. CodeBrixWebGpuNative.GetProcTable() hands the module a
     WGPUCodebrixProcTable: one pointer per exported function (every function
     of webgpu.h, wgpu.h and codebrix_wgpu.h, in ordinal order of the C symbol)
     filled from the library .NET loaded. The module links to nothing, so it
     has no load-order or library-path dependency (plug-in hosts, modules
     loaded before or apart from .NET's own probing). Each call is one
     indirect call - the cost of a LibraryImport call.
  3. THE IDIOMATIC `wgpu` CRATE, for code that cannot use the C API. It has
     its own wgpu state, so it cannot take these handles - but it can adopt
     the SAME Vulkan / Metal / Direct3D 12 device through wgpu's from-hal
     APIs, from the codebrix extras (CodebrixDeviceGetVulkanInfo,
     CodebrixTextureGetVulkanInfo, DeviceGetNativeMetalDevice,
     TextureGetNativeMetalTexture, CodebrixDeviceGetD3d12Info,
     CodebrixTextureGetD3d12Info), and must keep to the same pass protocol
     (one thread, submissions serialised on one queue, texture states agreed
     with CodebrixCommandEncoderTransitionTexture). The one-library rule still
     applies to the codebrix_wgpu library itself (never a second copy).

ROUTE 1 - LINKING PER OPERATING SYSTEM
  Linux, Android   Link against the package's libcodebrix_wgpu.so for the
                   target (C: -L<pkg>/runtimes/<rid>/native -lcodebrix_wgpu;
                   Rust build.rs: cargo:rustc-link-search=native=<that dir>
                   and cargo:rustc-cfg=codebrix_wgpu_direct - the `direct`
                   module's #[link] names codebrix_wgpu). Every packaged .so has the SONAME
                   libcodebrix_wgpu.so, so the module records DT_NEEDED
                   libcodebrix_wgpu.so and the dynamic loader reuses the
                   library .NET already loaded - no rpath or LD_LIBRARY_PATH.
                   Android: the APK carries this package's one
                   libcodebrix_wgpu.so; the module's .so sits beside it.
  macOS            Link against the package's libcodebrix_wgpu.dylib. Both
                   dylibs have the install name @rpath/libcodebrix_wgpu.dylib,
                   which the module records; dyld reuses the loaded image with
                   that install name. Never let the module's rpath resolve to
                   a different copy of the file.
  Windows          Rust: #[link(name = "codebrix_wgpu", kind = "raw-dylib")]
                   (the `direct` module does this) - no import library is
                   needed and none is shipped. C / C++: generate an import
                   library from a .def of the functions you call, or use the
                   function table. The loader reuses the already-loaded
                   codebrix_wgpu.dll by its base name; never put another
                   codebrix_wgpu.dll beside the module.

THE LOAD-ORDER RULE (route 1). Call CodeBrixWebGpuNative.EnsureLoaded()
BEFORE the module's native library is loaded - in the module wrapper's static
constructor, which runs before its first LibraryImport call loads the module:

    public static unsafe partial class MyModule
    {
        static MyModule() => CodeBrixWebGpuNative.EnsureLoaded(); // codebrix_wgpu first

        [LibraryImport("my_module")] private static partial int my_module_attach(CodebrixDeviceHandoff* device);
        [LibraryImport("my_module")] private static partial void my_module_frame(CodebrixFrameHandoff* frame);

        public static int Attach(CodebrixDeviceHandoff* device) => my_module_attach(device);
        public static void Frame(CodebrixFrameHandoff* frame) => my_module_frame(frame);
    }

  Loaded first, the module would make the loader search for codebrix_wgpu on
  its own: the load fails, or - worse - a second copy is loaded and nothing
  works.

API VERSION CHECK. Before using anything the module compares
wgpuCodebrixGetApiVersion() (route 1) or the hand-off's / table's apiVersion
with the WGPU_CODEBRIX_API_VERSION it was built against and refuses a
mismatch. Through the table also: functionCount ==
WGPU_CODEBRIX_PROC_TABLE_FUNCTION_COUNT and signatureHash ==
WGPU_CODEBRIX_PROC_TABLE_SIGNATURE_HASH (FNV-1a 64 of every entry's C
signature - any change to the function set or a signature changes it), and
size >= sizeof(WGPUCodebrixProcTable). For a hand-off struct: version ==
WGPU_CODEBRIX_*_HANDOFF_VERSION and size >= sizeof the struct. Build the
module against the headers of the SAME package version it ships with.

PASSING HANDLES. Either pass raw handle values (Device.Handle and friends -
IntPtr / nint arguments, C WGPUDevice, Rust WGPUDevice) or the two hand-off
structs, one pointer each, filled into memory you provide (no per-frame
allocation):

    partial struct CodebrixDeviceHandoff   // C WGPUCodebrixDeviceHandoff, 64 bytes
        uint Size, Version, ApiVersion; BackendType Backend;
        uint Flags;       // FlagUncapturedErrorHandler (1): without it any
                          // uncaptured error ENDS THE PROCESS - use error scopes
        uint Reserved; CodebrixProcTable* Procs;
        Instance Instance; Adapter Adapter; Device Device; Queue Queue;
        const uint CurrentVersion, FlagUncapturedErrorHandler;
        static void Fill(IWebGpuDeviceSource, CodebrixDeviceHandoff*);  (+ ref overload)
        static CodebrixDeviceHandoff From(IWebGpuDeviceSource);
    partial struct CodebrixFrameHandoff    // C WGPUCodebrixFrameHandoff, 56 bytes
        uint Size, Version; TextureFormat Format; uint Width, Height,
        SampleCount; TextureUsage Usage; Texture Texture;
        TextureView TextureView; CommandEncoder CommandEncoder; // null = no pass
        const uint CurrentVersion;
        static void Fill(IWebGpuTextureTarget, CommandEncoder, CodebrixFrameHandoff*);  (+ ref overload)
        static CodebrixFrameHandoff From(IWebGpuTextureTarget, CommandEncoder);
        static CommandEncoder BeginWebGpuPass(IWebGpuTextureTarget, CodebrixFrameHandoff*);
            // opens the target's pass and fills the struct in one call
    partial struct CodebrixProcTable       // C WGPUCodebrixProcTable
        uint Size, ApiVersion, FunctionCount, Reserved; ulong SignatureHash;
        delegate* unmanaged[Cdecl]<...> <one field per function, named like
            the WebGpu method: DeviceCreateBuffer, QueueSubmit, ...>;
        const uint EntryCount; const ulong ExpectedSignatureHash; const int HeaderSize;
        static IReadOnlyList<string> Symbols;   // C symbols, table order
        static IReadOnlyList<CodebrixProcStatus> Statuses;   // static status, table order
        static IReadOnlyList<BackendType> RequiredBackends;  // Undefined = none
    static CodebrixProcTable* CodeBrixWebGpuNative.GetProcTable();
        // built once from the exports of the library .NET loaded (never a
        // second copy), process-lifetime native memory (never free it),
        // thread-safe

  Ownership: every handle is BORROWED. Device-level handles stay valid while
  the source keeps its device (until it is disposed); the frame's texture,
  view and encoder only for that pass. A module that keeps a handle longer
  calls its *AddRef (wgpuDeviceAddRef, wgpuTextureAddRef, ...) and later
  *Release - then owner and module may be disposed in either order.

  Per frame (no allocation):

    CodebrixFrameHandoff* frame = ...;   // allocated once (NativeMemory.Alloc)
    CommandEncoder encoder = CodebrixFrameHandoff.BeginWebGpuPass(target, frame);
    if (!encoder.IsNull)
    {
        MyModule.Frame(frame);          // the module records into frame->commandEncoder
        target.EndWebGpuPass();         // the target finishes + submits
    }

PASS AND THREADING RULES (both routes)
  * Record work that touches the shared texture ONLY between BeginWebGpuPass
    and EndWebGpuPass, ONLY into the encoder of that pass
    (frame->commandEncoder), on the thread that opened the pass - the thread
    that uses the device.
  * Close every render / compute pass you begin before returning. Never
    finish, submit or release the pass encoder (you may AddRef/Release your
    own reference).
  * Your own resources (buffers, textures, pipelines on the hand-off's
    device) may be created and written (wgpuQueueWriteBuffer) on that same
    thread at any time; never touch the shared texture outside a pass.
  * Validation: errors in recorded commands surface when the target finishes
    the encoder at EndWebGpuPass (WebGpuSkiaTarget then submits nothing and
    throws WebGpuSkiaValidationException). Errors no scope catches go to the
    device's uncaptured-error handler - or END THE PROCESS when the hand-off
    has no FlagUncapturedErrorHandler. Wrap object creation in
    wgpuDevicePushErrorScope / wgpuDevicePopErrorScope.
  * The calls under LIMITS end the process from a module exactly as from C#:
    the notes on the entries of codebrix_wgpu_procs.h / .rs and of
    CodebrixProcTable mark them ("ABORTS: ...").

CAPABILITY STATUS (export presence is not capability evidence). Every
function-table entry has a status: Implemented, AbortsUnimplemented (calling
it ends the process), AbortsOnError (ends the process on the inputs its note
names), Unsupported (always fails), NotAvailableOnBackend (needs Vulkan /
Direct3D 12 / Metal, which this native lacks). Ask the LOADED library -
wgpuCodebrixGetProcStatus(ordinal) (ordinal = table index for this
signatureHash; CodeBrixWebGpuNative.GetProcStatus(symbol) from .NET) - rather
than the header's static copy (WGPU_CODEBRIX_PROC_STATUS_TABLE /
WGPU_CODEBRIX_PROC_BACKEND_TABLE initializers in codebrix_wgpu_procs.h, the
same-named constants in codebrix_wgpu_procs.rs, CodebrixProcTable.Statuses),
which cannot know the backends. A semantic fix with the same table hash shows
up in wgpuCodebrixGetBuildInfo's `patches` (the native fix set).

FAULTS ON A SHARED DEVICE. A module (or a host) that needs to hear about
errors on a device it does not own adds a fault listener
(wgpuCodebrixDeviceAddFaultListener with a WGPUCodebrixFaultCallbacks of the
standard webgpu.h callback-info types; wgpuCodebrixDeviceRemoveFaultListener)
instead of replacing the owner's callbacks: listeners run after the owner's
own callback, on the thread that discovered the fault, with the error sink
locked - never call webgpu.h functions on that device or add/remove
listeners from inside one. For a bounded wait that cannot end the process use
wgpuCodebrixDevicePoll (timeout, optional submission index, structured
result).


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
  * Copy OUT of a mapped read-back buffer with
    CodeBrix.WebGpu.Native.Interop.FastMemory.Copy / CopyRows, never with a
    row loop, Span.CopyTo or Buffer.MemoryCopy. On some mobile GPUs the mapped
    memory is served uncached to the CPU and the runtime's copies run an order
    of magnitude slower than the C runtime memcpy FastMemory resolves (with a
    managed fallback when it cannot). CodeBrix.WebGpu.Skia and .Graphics
    already read back through it.


LIMITS
======

What the engine this package ships cannot do, and what to do instead. "Ends
the process" means a native abort: no exception, no error callback, nothing
an error scope can catch.

  * UNIMPLEMENTED FUNCTIONS end the process when called (they are exported
    only so the binding matches the C API):
      GetProcAddress, InstanceWaitAny, DeviceCreateComputePipelineAsync,
      DeviceCreateRenderPipelineAsync, DeviceGetAdapterInfo,
      DeviceGetLostFuture, BufferGetMapState, BufferReadMappedRange,
      BufferWriteMappedRange, ShaderModuleGetCompilationInfo,
      InstanceGetWGSLLanguageFeatures, InstanceHasWGSLLanguageFeature,
      SupportedWGSLLanguageFeaturesFreeMembers, GetInstanceFeatures,
      HasInstanceFeature, SupportedInstanceFeaturesFreeMembers,
      TextureGetTextureBindingViewDimension, ExternalTextureAddRef,
      ExternalTextureRelease, ExternalTextureSetLabel, and every *SetLabel
      function.
    Instead: labels through the descriptors' Label; AdapterGetInfo (not
    DeviceGetAdapterInfo); synchronous pipeline creation; BufferGetMappedRange
    / BufferGetConstMappedRange and track the map state yourself; shader
    diagnostics from an error scope around DeviceCreateShaderModule;
    AllowProcessEvents / AllowSpontaneous callbacks pumped with
    InstanceProcessEvents / DevicePoll (CallbackMode.WaitAnyOnly callbacks never run).
  * ERRORS THAT END THE PROCESS instead of reaching the error callback or an
    error scope - validate before calling:
      - QueueSubmit / QueueSubmitForIndex with an invalid command buffer
        (one whose CommandEncoderFinish reported a validation error - finish
        inside an error scope and do not submit on error), or with work that
        uses a buffer while it is mapped, or a destroyed buffer or texture
        (finish such work first, then unmap / destroy);
      - RenderBundleEncoderFinish on a bundle whose commands fail validation;
      - DeviceCreateRenderBundleEncoder with impossible colour / depth
        formats or sample counts;
      - BufferGetMappedRange / BufferGetConstMappedRange on a buffer that is
        not mapped (or mapped for the other mode), or with an offset / size
        that is misaligned (offset multiple of 8, size multiple of 4) or
        outside the mapped range;
      - InstanceCreateSurface with handles that do not belong together,
        SurfaceConfigure with a configuration the surface's capabilities do
        not allow, SurfaceGetCurrentTexture on an unconfigured surface.
  * QuerySetDestroy followed by the normal QuerySetRelease ends the process
    (the set is dropped twice). Never call QuerySetDestroy - release the set.
  * The DEFAULT UNCAPTURED-ERROR HANDLER ends the process. A device requested
    without DeviceDescriptor.UncapturedErrorCallbackInfo.Callback - and every
    device made by CodebrixInstanceAdoptVulkanDevice / AdoptMetalDevice,
    which cannot take one - aborts on the first validation error no error
    scope catches. Always set the callback on devices you request; on adopted
    devices, wrap object creation and other risky calls in error scopes.
  * Experimental native features (NativeFeature.RayQuery,
    NativeFeature.CooperativeMatrix) are never enabled by the engine: a
    device request that requires one fails, even when the adapter lists it.
  * Device loss is not delivered through DeviceLostCallbackInfo: using a
    destroyed device reports a Validation error through the uncaptured-error
    callback instead. WebGpu.PollDevice reports CodebrixPollResult.DeviceLost
    (and fault listeners' DeviceLost then fires once).
  * DeviceCreateShaderModuleSpirV (passthrough) needs a feature this engine
    does not expose; use ShaderModuleDescriptor + ShaderSourceSPIRV.
  * Debug groups and markers recorded into render bundles are dropped;
    QueueGetNativeMetalCommandQueue always returns null;
    CodebrixInstanceAdoptD3d12Device always returns Status.Error (create the
    device here and share it the other way with CodebrixDeviceGetD3d12Info).


COMMON PITFALLS TO AVOID
========================

  * NOT CALLING EnsureLoaded. Imports work without it, but only EnsureLoaded
    checks the contract version and every struct layout.
  * CALLING A FUNCTION THE ENGINE DOES NOT IMPLEMENT, or relying on an
    error scope for the errors listed under LIMITS: both end the process.
    Read LIMITS before using a function for the first time.
  * Fatal native panics also abort the process: passing a null handle where a
    valid one is required ("invalid device"), or an invalid enum value, is not
    a recoverable error. Check IsNull on every handle you create.
  * QueueWriteBuffer / QueueWriteTexture are staged until the next QueueSubmit.
    Mapping the buffer without a submit in between reads the old contents.
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
  * No SkiaSharp integration (that is CodeBrix.WebGpu.Skia - use it rather
    than wiring the codebrix extras to SkiaSharp yourself).
  * No support for platforms other than the runtime identifiers listed in
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
  Native modules (function table vs LibraryImport, hand-offs, a "module"
  rendering into a target through the table):
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/CodebrixProcTableTests.cs
    https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/tests/CodeBrix.WebGpu.Native.Tests/CodebrixHandoffTests.cs
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
  Share           IWebGpuDeviceSource / IWebGpuTextureTarget (SHARING CONTRACTS)
  Native module   include/*.h + codebrix_wgpu_procs.rs; EnsureLoaded() first
                  CodebrixDeviceHandoff.Fill(source, p) / GetProcTable()
                  CodebrixFrameHandoff.BeginWebGpuPass(target, p) ... End
  Avoid           everything under LIMITS (process-ending calls)
  Constants       Constants.WholeSize, Constants.Strlen, Constants.DepthSliceUndefined
  Free            *Release for handles, *FreeMembers for API-filled structs


================================================================================
END OF AGENT-README
================================================================================
