using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.WebGpu.BindingGenerator.Model;
using CodeBrix.WebGpu.BindingGenerator.Rendering;
using CodeBrix.WebGpu.Native.Interop;
using SilverAssertions;
using Xunit;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// The native-module function table: built once from the exports of the library this binding loaded, in the order
/// and with the signatures of the generated binding, and usable for real work - calls through its C# function
/// pointers (version, build info, instance, adapter, device, a GPU buffer round trip) agree with the LibraryImport
/// calls.
/// </summary>
public sealed unsafe class CodebrixProcTableTests
{
    private static readonly string Root =
        BindingGenerator.BindingGenerator.FindRepositoryRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Cannot find CodeBrix.WebGpu.slnx");

    [Fact]
    public void the_header_describes_the_table()
    {
        //Act
        CodebrixProcTable* table = CodeBrixWebGpuNative.GetProcTable();

        //Assert
        table->Size.Should().Be((uint)sizeof(CodebrixProcTable));
        table->ApiVersion.Should().Be(Constants.ApiVersion);
        table->ApiVersion.Should().Be(WebGpu.CodebrixGetApiVersion());
        table->FunctionCount.Should().Be(CodebrixProcTable.EntryCount);
        table->Reserved.Should().Be(0u);
        table->SignatureHash.Should().Be(CodebrixProcTable.ExpectedSignatureHash);
        sizeof(CodebrixProcTable).Should().Be(CodebrixProcTable.HeaderSize + ((int)CodebrixProcTable.EntryCount * IntPtr.Size));
        CodebrixProcTable.Symbols.Count.Should().Be((int)CodebrixProcTable.EntryCount);
    }

    [Fact]
    public void every_entry_is_the_export_of_the_loaded_library()
    {
        //Arrange
        CodebrixProcTable* table = CodeBrixWebGpuNative.GetProcTable();
        IntPtr library = WgpuLibrary.Handle;
        nint* entries = (nint*)((byte*)table + CodebrixProcTable.HeaderSize);
        List<string> wrong = new List<string>();

        //Act
        for (int index = 0; index < CodebrixProcTable.Symbols.Count; index++)
        {
            if (entries[index] == 0 || entries[index] != NativeLibrary.GetExport(library, CodebrixProcTable.Symbols[index])) wrong.Add(CodebrixProcTable.Symbols[index]);
        }

        //Assert
        library.Should().NotBe(IntPtr.Zero);
        wrong.Should().BeEmpty();
        ((nint)table->GetVersion).Should().Be(NativeLibrary.GetExport(library, "wgpuGetVersion"));
    }

    [Fact]
    public void the_table_is_built_once_and_shared_by_every_thread()
    {
        //Arrange
        nint[] seen = new nint[32];

        //Act
        Parallel.For(0, seen.Length, index => seen[index] = (nint)CodeBrixWebGpuNative.GetProcTable());

        //Assert
        seen.Distinct().Should().HaveCount(1);
        seen[0].Should().Be((nint)CodeBrixWebGpuNative.GetProcTable());
    }

    [Fact]
    public void the_order_and_count_match_expected_exports()
    {
        //Arrange
        string[] exports = File.ReadAllLines(Path.Combine(Root, CSharpRenderer.ExpectedExportsPath))
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();

        //Assert
        CodebrixProcTable.Symbols.Should().Equal(exports);
        CodebrixProcTable.Symbols.Should().Equal(CodebrixProcTable.Symbols.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void every_entry_sits_at_its_slot_with_the_signature_of_the_generated_import()
    {
        //Arrange
        Dictionary<string, MethodInfo> imports = typeof(WebGpu).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => (Method: method, Import: method.GetCustomAttribute<LibraryImportAttribute>()))
            .Where(pair => pair.Import != null)
            .ToDictionary(pair => pair.Import.EntryPoint, pair => pair.Method, StringComparer.Ordinal);
        List<string> problems = new List<string>();

        //Act
        for (int index = 0; index < CodebrixProcTable.Symbols.Count; index++)
        {
            string symbol = CodebrixProcTable.Symbols[index];
            MethodInfo import = imports[symbol];
            FieldInfo field = typeof(CodebrixProcTable).GetField(import.Name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                problems.Add(symbol + ": no field " + import.Name);
                continue;
            }

            int offset = (int)Marshal.OffsetOf<CodebrixProcTable>(field.Name);
            if (offset != CodebrixProcTable.HeaderSize + (index * IntPtr.Size)) problems.Add(symbol + ": offset " + offset);

            Type pointer = field.GetModifiedFieldType();
            Type[] parameters = pointer.GetFunctionPointerParameterTypes().Select(p => p.UnderlyingSystemType).ToArray();
            Type[] expected = import.GetParameters().Select(p => p.ParameterType).ToArray();
            if (!pointer.IsFunctionPointer || !pointer.IsUnmanagedFunctionPointer) problems.Add(symbol + ": not an unmanaged function pointer");
            if (pointer.GetFunctionPointerReturnType().UnderlyingSystemType != import.ReturnType) problems.Add(symbol + ": return type");
            if (parameters.Length != expected.Length) problems.Add(symbol + ": parameter count");
            else
            {
                for (int parameter = 0; parameter < parameters.Length; parameter++)
                {
                    if (!SameType(parameters[parameter], expected[parameter])) problems.Add(symbol + ": parameter " + parameter + " " + parameters[parameter] + " vs " + expected[parameter]);
                }
            }
        }

        //Assert
        imports.Count.Should().Be((int)CodebrixProcTable.EntryCount);
        problems.Should().BeEmpty();
    }

    // A function-pointer parameter of a function-pointer type and of an import compare structurally.
    private static bool SameType(Type a, Type b) =>
        a.IsFunctionPointer || b.IsFunctionPointer ? a.IsFunctionPointer && b.IsFunctionPointer : a == b;

    [Fact]
    public void the_signature_hash_is_the_generators()
    {
        //Arrange
        ApiModel model = BindingGenerator.BindingGenerator.LoadModel(Root);
        ProcNotes notes = ProcNotes.Parse(File.ReadAllText(Path.Combine(Root, BindingGenerator.BindingGenerator.ProcNotesPath)), "notes");

        //Act
        ModuleInterop interop = new ModuleInterop(model, notes);

        //Assert
        interop.SignatureHash.Should().Be(CodebrixProcTable.ExpectedSignatureHash);
        interop.Procs.Count.Should().Be((int)CodebrixProcTable.EntryCount);
        interop.ProcTableHeaderSize.Should().Be(CodebrixProcTable.HeaderSize);
        interop.ProcTableLayout.Size.Should().Be(sizeof(CodebrixProcTable));
    }

    [Fact]
    public void the_generators_struct_layouts_match_the_managed_structs()
    {
        //Arrange
        ApiModel model = BindingGenerator.BindingGenerator.LoadModel(Root);
        LayoutCalculator calculator = new LayoutCalculator(model);
        List<string> problems = new List<string>();

        //Act
        foreach (StructLayoutInfo managed in CodeBrixWebGpuNative.ManagedStructLayouts())
        {
            TypeLayout computed = calculator.Struct(managed.CName);
            if (computed.Size != managed.Size || computed.Alignment != managed.Alignment)
            {
                problems.Add(managed.CName + ": generator " + computed.Size + "/" + computed.Alignment + ", managed " + managed.Size + "/" + managed.Alignment);
            }
        }

        //Assert
        problems.Should().BeEmpty();
    }

    [Fact]
    public void simple_calls_through_the_table_match_the_imports()
    {
        //Arrange
        CodebrixProcTable* table = CodeBrixWebGpuNative.GetProcTable();
        CodebrixBuildInfo viaTable = default;
        CodebrixBuildInfo viaImport = default;
        nuint size = 0;
        nuint alignment = 0;
        Status status;

        //Act
        table->CodebrixGetBuildInfo(&viaTable);
        WebGpu.CodebrixGetBuildInfo(&viaImport);
        fixed (byte* name = "WGPUBufferDescriptor"u8)
        {
            status = table->CodebrixGetStructLayout(WgpuString.FromPinned(name, 20), &size, &alignment);
        }

        //Assert
        table->GetVersion().Should().Be(WebGpu.GetVersion());
        table->CodebrixGetApiVersion().Should().Be(WebGpu.CodebrixGetApiVersion());
        viaTable.ApiVersion.Should().Be(viaImport.ApiVersion);
        WgpuString.ToManaged(viaTable.WgpuCoreVersion).Should().Be(WgpuString.ToManaged(viaImport.WgpuCoreVersion));
        WgpuString.ToManaged(viaTable.Backends).Should().Be(CodeBrixWebGpuNative.BuildInfo.Backends);
        status.Should().Be(Status.Success);
        ((int)size).Should().Be(sizeof(BufferDescriptor));
        table->CodebrixDeviceGetBackend(Device.Null).Should().Be(BackendType.Undefined);
    }

    [Fact]
    public void a_gpu_buffer_round_trip_runs_entirely_through_the_table()
    {
        //Arrange
        CodebrixProcTable* t = CodeBrixWebGpuNative.GetProcTable();
        InstanceExtras extras = new InstanceExtras { Backends = GpuSession.PlatformBackend };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor instanceDescriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        Instance instance = t->CreateInstance(&instanceDescriptor);
        Assert.SkipWhen(instance.IsNull, "wgpuCreateInstance returned null.");

        Request adapterRequest = default;
        RequestAdapterOptions options = default;
        t->InstanceRequestAdapter(instance, &options, new RequestAdapterCallbackInfo { Mode = CallbackMode.AllowProcessEvents, Callback = &OnAdapter, Userdata1 = &adapterRequest });
        Pump(t, instance, &adapterRequest.Called);
        Adapter adapter = new Adapter(adapterRequest.Handle);
        if (adapter.IsNull) t->InstanceRelease(instance);
        Assert.SkipWhen(adapter.IsNull, "No WebGPU adapter is available on this machine.");

        int uncapturedErrors = 0;
        DeviceDescriptor deviceDescriptor = default;
        deviceDescriptor.UncapturedErrorCallbackInfo.Callback = &OnUncapturedError;
        deviceDescriptor.UncapturedErrorCallbackInfo.Userdata1 = &uncapturedErrors;
        Request deviceRequest = default;
        t->AdapterRequestDevice(adapter, &deviceDescriptor, new RequestDeviceCallbackInfo { Mode = CallbackMode.AllowProcessEvents, Callback = &OnDevice, Userdata1 = &deviceRequest });
        Pump(t, instance, &deviceRequest.Called);
        Device device = new Device(deviceRequest.Handle);
        device.IsNull.Should().BeFalse();
        Queue queue = t->DeviceGetQueue(device);

        const int Size = 256;
        byte[] data = Enumerable.Range(0, Size).Select(value => (byte)(value * 7)).ToArray();
        BufferDescriptor sourceDescriptor = new BufferDescriptor { Usage = BufferUsage.CopySrc | BufferUsage.CopyDst, Size = Size };
        BufferDescriptor readbackDescriptor = new BufferDescriptor { Usage = BufferUsage.CopyDst | BufferUsage.MapRead, Size = Size };
        byte[] read = new byte[Size];

        //Act
        Buffer source = t->DeviceCreateBuffer(device, &sourceDescriptor);
        Buffer readback = t->DeviceCreateBuffer(device, &readbackDescriptor);
        fixed (byte* bytes = data) t->QueueWriteBuffer(queue, source, 0, bytes, Size);
        CommandEncoder encoder = t->DeviceCreateCommandEncoder(device, null);
        t->CommandEncoderCopyBufferToBuffer(encoder, source, 0, readback, 0, Size);
        CommandBuffer commands = t->CommandEncoderFinish(encoder, null);
        t->QueueSubmit(queue, 1, &commands);
        Request map = default;
        t->BufferMapAsync(readback, MapMode.Read, 0, Size, new BufferMapCallbackInfo { Mode = CallbackMode.AllowProcessEvents, Callback = &OnMapped, Userdata1 = &map });
        for (int attempt = 0; attempt < 1000 && Volatile.Read(ref map.Called) == 0; attempt++) t->DevicePoll(device, 1, null);
        byte* mapped = (byte*)t->BufferGetConstMappedRange(readback, 0, Size);
        new ReadOnlySpan<byte>(mapped, Size).CopyTo(read);
        t->BufferUnmap(readback);

        AdapterInfo infoViaTable = default;
        AdapterInfo infoViaImport = default;
        Status tableInfoStatus = t->AdapterGetInfo(adapter, &infoViaTable);
        WebGpu.AdapterGetInfo(adapter, &infoViaImport);
        BackendType backendViaTable = t->CodebrixDeviceGetBackend(device);
        BackendType backendViaImport = WebGpu.CodebrixDeviceGetBackend(device);
        (uint Vendor, uint Device, BackendType Backend) viaTable = (infoViaTable.VendorID, infoViaTable.DeviceID, infoViaTable.BackendType);
        (uint Vendor, uint Device, BackendType Backend) viaImport = (infoViaImport.VendorID, infoViaImport.DeviceID, infoViaImport.BackendType);
        t->AdapterInfoFreeMembers(infoViaTable);
        WebGpu.AdapterInfoFreeMembers(infoViaImport);

        t->CommandBufferRelease(commands);
        t->CommandEncoderRelease(encoder);
        t->BufferRelease(readback);
        t->BufferRelease(source);
        t->QueueRelease(queue);
        t->DeviceRelease(device);
        t->AdapterRelease(adapter);
        t->InstanceRelease(instance);

        //Assert
        ((RequestDeviceStatus)deviceRequest.Status).Should().Be(RequestDeviceStatus.Success);
        ((MapAsyncStatus)map.Status).Should().Be(MapAsyncStatus.Success);
        read.Should().Equal(data);
        tableInfoStatus.Should().Be(Status.Success);
        viaTable.Should().Be(viaImport);
        backendViaTable.Should().Be(backendViaImport);
        backendViaTable.Should().Be(GpuSession.ExpectedBackendType);
        uncapturedErrors.Should().Be(0);
    }

    private static void Pump(CodebrixProcTable* table, Instance instance, int* called)
    {
        for (int attempt = 0; attempt < 2000 && Volatile.Read(ref *called) == 0; attempt++)
        {
            table->InstanceProcessEvents(instance);
            if (Volatile.Read(ref *called) == 0) Thread.Sleep(1);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnAdapter(RequestAdapterStatus status, Adapter adapter, StringView message, void* userdata1, void* userdata2)
    {
        Request* request = (Request*)userdata1;
        request->Status = (uint)status;
        request->Handle = status == RequestAdapterStatus.Success ? adapter.Handle : IntPtr.Zero;
        Volatile.Write(ref request->Called, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDevice(RequestDeviceStatus status, Device device, StringView message, void* userdata1, void* userdata2)
    {
        Request* request = (Request*)userdata1;
        request->Status = (uint)status;
        request->Handle = status == RequestDeviceStatus.Success ? device.Handle : IntPtr.Zero;
        Volatile.Write(ref request->Called, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnMapped(MapAsyncStatus status, StringView message, void* userdata1, void* userdata2)
    {
        Request* request = (Request*)userdata1;
        request->Status = (uint)status;
        Volatile.Write(ref request->Called, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnUncapturedError(Device* device, ErrorType type, StringView message, void* userdata1, void* userdata2)
        => Interlocked.Increment(ref *(int*)userdata1);

    private struct Request
    {
        public int Called;
        public uint Status;
        public IntPtr Handle;
    }
}
