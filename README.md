# CodeBrix.WebGpu.Native

Thin, generated .NET bindings over a native WebGPU implementation that ships inside the package for every supported platform: every C function of the WebGPU API (and of the wgpu engine's native extensions) as a blittable `LibraryImport`, every C struct with its exact memory layout, and a small set of native-device extras for sharing one GPU device with other renderers.
CodeBrix.WebGpu.Native is provided as a .NET 10 library and associated `CodeBrix.WebGpu.Native.MitLicenseForever` NuGet package.

CodeBrix.WebGpu.Native supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.WebGpu.Native.MitLicenseForever
```

Note that the NuGet package ID and the namespace are different - there is no package named plain `CodeBrix.WebGpu.Native`:

* NuGet package ID: `CodeBrix.WebGpu.Native.MitLicenseForever`
* Assembly and primary namespace: `CodeBrix.WebGpu.Native` - i.e. `using CodeBrix.WebGpu.Native;`

XML documentation (IntelliSense) ships alongside the assembly.

CodeBrix.WebGpu.Native has no dependencies other than .NET. Code that uses it must allow unsafe code (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`).

## CodeBrix.WebGpu.Native supports:

* The complete WebGPU C API - instances, adapters, devices, queues, buffers, textures, samplers, shader modules (WGSL and SPIR-V), bind groups, compute and render pipelines, command encoding, queries and surfaces - one static method per C function on the `WebGpu` class
* The wgpu engine's native extensions: instance backend selection and validation flags, device polling by submission index, native features and limits, pipeline statistics and timestamp queries, multi-draw-indirect, GLSL shader sources, resource reports and logging
* Exact C layouts for every struct, enum and bitflag, generated from the API's machine-readable specification and checked against the native library (size and alignment of every struct) when the library loads
* Callbacks as `delegate* unmanaged` function pointers in the API's callback-info structs - no delegates, no marshalling, no hidden allocations
* UTF-8 string view helpers for passing text in and reading text out
* Native-device access for sharing a GPU device: the Vulkan instance, physical device, device and queue (with the enabled extensions) behind a WebGPU device, the Vulkan image behind a texture, the Direct3D 12 and Metal equivalents, wrapping an existing Vulkan, Direct3D 12 or Metal device or texture as WebGPU objects, and explicit texture state transitions
* Native libraries in the package for Windows x64 and ARM64, macOS Intel and Apple Silicon, Linux x64, ARM64 and RISC-V 64, and Android 13+ ARM64 and x64 - each on the platform's own GPU API: Vulkan on Linux and Android, Direct3D 12 and Vulkan on Windows, Metal on macOS

## Native assets

The package carries one native library per platform in the standard NuGet `runtimes/<rid>/native/` layout, each beside the licence text of the code it was built from. NuGet and the .NET runtime pick the right one; on Android it is packed into the APK. If the native cannot be found at run time, the exception names the runtime identifier and every path that was tried.

## Sample Code

### Load the library and list the adapters

```csharp
using System;
using CodeBrix.WebGpu.Native;

CodeBrixWebGpuNative.EnsureLoaded();   // loads the native and checks every struct layout
Console.WriteLine("wgpu " + CodeBrixWebGpuNative.LibraryVersion);

unsafe
{
    InstanceDescriptor descriptor = default;
    Instance instance = WebGpu.CreateInstance(&descriptor);

    nuint count = WebGpu.InstanceEnumerateAdapters(instance, null, null);
    Adapter[] adapters = new Adapter[(int)count];
    fixed (Adapter* first = adapters) WebGpu.InstanceEnumerateAdapters(instance, null, first);

    foreach (Adapter adapter in adapters)
    {
        AdapterInfo info = default;
        WebGpu.AdapterGetInfo(adapter, &info);
        Console.WriteLine(WgpuString.ToManaged(info.Device) + " (" + info.BackendType + ")");
        WebGpu.AdapterInfoFreeMembers(info);
        WebGpu.AdapterRelease(adapter);
    }

    WebGpu.InstanceRelease(instance);
}
```

### Request an adapter through a callback

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;

static unsafe class Gpu
{
    public static Adapter RequestAdapter(Instance instance)
    {
        nint result = 0;
        RequestAdapterOptions options = default;
        RequestAdapterCallbackInfo callback = new RequestAdapterCallbackInfo
        {
            Mode = CallbackMode.AllowProcessEvents,
            Callback = &OnAdapter,
            Userdata1 = &result,
        };

        WebGpu.InstanceRequestAdapter(instance, &options, callback);
        while (result == 0) WebGpu.InstanceProcessEvents(instance);   // callbacks run in here
        return new Adapter(result == -1 ? 0 : result);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAdapter(RequestAdapterStatus status, Adapter adapter, StringView message, void* userdata1, void* userdata2)
        => *(nint*)userdata1 = status == RequestAdapterStatus.Success ? adapter.Handle : -1;
}
```

## Documentation

The NuGet package includes `AGENT-README.txt`, a complete API reference and usage guide written for AI coding agents - point your agent at that file when it is writing code against this library.

Additional sample code and usage examples are available in the `CodeBrix.WebGpu.Native.Tests` project:
https://github.com/ellisnet/CodeBrix.WebGpu/tree/main/tests/CodeBrix.WebGpu.Native.Tests

## License

CodeBrix.WebGpu.Native is licensed under the MIT License - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.WebGpu/blob/main/THIRD-PARTY-NOTICES.txt).
