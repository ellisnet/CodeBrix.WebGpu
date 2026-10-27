using System;
using System.Linq;
using System.Text;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>A <see cref="GpuSession" /> whose device enables <c>WGPUNativeFeature_PipelineCache</c> (Vulkan adapters).</summary>
public sealed class PipelineCacheFixture : IDisposable
{
    /// <summary>Creates the session.</summary>
    public PipelineCacheFixture()
    {
        Session = GpuSession.CreateWithFeatures((FeatureName)NativeFeature.PipelineCache);
    }

    /// <summary>The session.</summary>
    public GpuSession Session { get; }

    /// <summary>Releases the session.</summary>
    public void Dispose() => Session.Dispose();
}

/// <summary>
/// Pipeline caches (specs/codebrix.yml: <c>codebrix_pipeline_cache</c>; wgpu-native-tools/patches/0011): a cache
/// chained onto a compute or render pipeline descriptor through <see cref="CodebrixPipelineDescriptorExtras" /> is
/// filled by the driver, its data round-trips into a warm cache, foreign data follows <c>fallback</c>, and nothing
/// ends the process on a device or backend without the feature. Skips, with the reason, when the machine has no
/// adapter or the adapter offers no pipeline caches (anything but Vulkan).
/// </summary>
public unsafe class PipelineCacheTests : IClassFixture<GpuFixture>, IClassFixture<PipelineCacheFixture>
{
    private const string Doubling =
        "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n"
        + "@compute @workgroup_size(64)\n"
        + "fn main(@builtin(global_invocation_id) id: vec3<u32>) { data[id.x] = data[id.x] * 2u; }\n";

    private const string Triangle =
        "@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {\n"
        + "    let x = f32(i32(i) - 1);\n"
        + "    let y = f32(i32(i & 1u) * 2 - 1);\n"
        + "    return vec4<f32>(x, y, 0.0, 1.0);\n"
        + "}\n"
        + "@fragment fn fs() -> @location(0) vec4<f32> { return vec4<f32>(0.25, 0.5, 0.75, 1.0); }\n";

    private readonly GpuSession plain;
    private readonly GpuSession cached;

    public PipelineCacheTests(GpuFixture plainFixture, PipelineCacheFixture cacheFixture)
    {
        plain = plainFixture.Session;
        cached = cacheFixture.Session;
    }

    private void RequireCacheDevice() => Assert.SkipWhen(cached.SkipReason != null, cached.SkipReason ?? string.Empty);

    private static CodebrixPipelineCache CreateCache(Device device, byte[] data, bool fallback)
    {
        byte[] label = "PipelineCacheTests"u8.ToArray();
        fixed (byte* text = label)
        fixed (byte* bytes = data)
        {
            CodebrixPipelineCacheDescriptor descriptor = new CodebrixPipelineCacheDescriptor
            {
                Label = WgpuString.FromPinned(text, label.Length),
                Data = data == null ? null : bytes,
                DataSize = data == null ? 0 : (nuint)data.Length,
                Fallback = fallback ? 1u : 0u,
            };
            return WebGpu.CodebrixDeviceCreatePipelineCache(device, &descriptor);
        }
    }

    private static byte[] GetData(CodebrixPipelineCache cache)
    {
        nuint size = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);
        byte[] data = new byte[(int)size];
        fixed (byte* bytes = data)
        {
            nuint written = WebGpu.CodebrixPipelineCacheGetData(cache, bytes, size);
            written.Should().Be(size, "nothing compiled between the size query and the fill");
        }

        return data;
    }

    private static ComputePipeline CreateComputePipeline(GpuSession session, ShaderModule module, CodebrixPipelineCache cache)
    {
        byte[] entry = "main"u8.ToArray();
        fixed (byte* entryPoint = entry)
        {
            CodebrixPipelineDescriptorExtras extras = new CodebrixPipelineDescriptorExtras { PipelineCache = cache };
            extras.Chain.SType = CodebrixPipelineDescriptorExtras.StructType;
            ComputePipelineDescriptor descriptor = default;
            descriptor.NextInChain = &extras.Chain;
            descriptor.Compute.Module = module;
            descriptor.Compute.EntryPoint = WgpuString.FromPinned(entryPoint, entry.Length);
            return WebGpu.DeviceCreateComputePipeline(session.Device, &descriptor);
        }
    }

    private static RenderPipeline CreateRenderPipeline(GpuSession session, ShaderModule module, CodebrixPipelineCache cache)
    {
        byte[] vs = "vs"u8.ToArray();
        byte[] fs = "fs"u8.ToArray();
        fixed (byte* vertexEntry = vs)
        fixed (byte* fragmentEntry = fs)
        {
            ColorTargetState target = new ColorTargetState { Format = TextureFormat.RGBA8Unorm, WriteMask = ColorWriteMask.All };
            FragmentState fragment = new FragmentState
            {
                Module = module,
                EntryPoint = WgpuString.FromPinned(fragmentEntry, fs.Length),
                TargetCount = 1,
                Targets = &target,
            };
            CodebrixPipelineDescriptorExtras extras = new CodebrixPipelineDescriptorExtras { PipelineCache = cache };
            extras.Chain.SType = CodebrixPipelineDescriptorExtras.StructType;
            RenderPipelineDescriptor descriptor = default;
            descriptor.NextInChain = &extras.Chain;
            descriptor.Vertex.Module = module;
            descriptor.Vertex.EntryPoint = WgpuString.FromPinned(vertexEntry, vs.Length);
            descriptor.Primitive.Topology = PrimitiveTopology.TriangleList;
            descriptor.Multisample.Count = 1;
            descriptor.Multisample.Mask = uint.MaxValue;
            descriptor.Fragment = &fragment;
            return WebGpu.DeviceCreateRenderPipeline(session.Device, &descriptor);
        }
    }

    // Runs the doubling pipeline over 256 u32 and reads them back; true when every value doubled.
    private static bool RunDoubling(GpuSession session, ComputePipeline pipeline)
    {
        const int count = 256;
        const ulong size = count * sizeof(uint);
        uint[] input = Enumerable.Range(0, count).Select(i => (uint)(3 * i + 1)).ToArray();
        Buffer storage = session.CreateBuffer(BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, size);
        Buffer readback = session.CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, size);
        fixed (uint* values = input) WebGpu.QueueWriteBuffer(session.Queue, storage, 0, values, (nuint)size);
        BindGroupLayout layout = WebGpu.ComputePipelineGetBindGroupLayout(pipeline, 0);
        BindGroupEntry entry = new BindGroupEntry { Binding = 0, Buffer = storage, Size = size };
        BindGroupDescriptor groupDescriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry };
        BindGroup group = WebGpu.DeviceCreateBindGroup(session.Device, &groupDescriptor);

        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(session.Device, null);
        ComputePassEncoder pass = WebGpu.CommandEncoderBeginComputePass(encoder, null);
        WebGpu.ComputePassEncoderSetPipeline(pass, pipeline);
        WebGpu.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
        WebGpu.ComputePassEncoderDispatchWorkgroups(pass, count / 64, 1, 1);
        WebGpu.ComputePassEncoderEnd(pass);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, storage, 0, readback, 0, size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(session.Queue, 1, &commands);
        MapAsyncStatus mapped = GpuSession.MapRead(session.Device, readback, (nuint)size);
        bool exact = mapped == MapAsyncStatus.Success
            && new ReadOnlySpan<uint>(WebGpu.BufferGetConstMappedRange(readback, 0, (nuint)size), count).ToArray()
                .SequenceEqual(input.Select(value => value * 2));
        if (mapped == MapAsyncStatus.Success) WebGpu.BufferUnmap(readback);

        WebGpu.CommandBufferRelease(commands);
        WebGpu.ComputePassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BindGroupRelease(group);
        WebGpu.BindGroupLayoutRelease(layout);
        WebGpu.BufferRelease(readback);
        WebGpu.BufferRelease(storage);
        return exact;
    }

    [Fact]
    public void create_without_the_feature_is_a_validation_error_and_never_ends_the_process()
    {
        //Arrange
        Assert.SkipWhen(plain.SkipReason != null, plain.SkipReason ?? string.Empty);
        WebGpu.DevicePushErrorScope(plain.Device, ErrorFilter.Validation);

        //Act
        CodebrixPipelineCache cache = CreateCache(plain.Device, null, fallback: true);
        (PopErrorScopeStatus status, ErrorType type, string message) = plain.PopErrorScope();
        nuint size = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);

        //Assert
        cache.IsNull.Should().BeFalse("creation returns an (invalid) object, as every WebGPU create call does");
        status.Should().Be(PopErrorScopeStatus.Success);
        type.Should().Be(ErrorType.Validation);
        message.Should().Contain("PipelineCache");
        size.Should().Be((nuint)0);
        WebGpu.CodebrixPipelineCacheAddRef(cache);
        WebGpu.CodebrixPipelineCacheRelease(cache);
        WebGpu.CodebrixPipelineCacheRelease(cache);
    }

    [Fact]
    public void null_handles_and_a_null_descriptor_are_harmless()
    {
        //Arrange
        CodeBrixWebGpuNative.EnsureLoaded();
        StringView key = default;

        //Act
        nuint size = WebGpu.CodebrixPipelineCacheGetData(CodebrixPipelineCache.Null, null, 0);
        WebGpu.CodebrixPipelineCacheAddRef(CodebrixPipelineCache.Null);
        WebGpu.CodebrixPipelineCacheRelease(CodebrixPipelineCache.Null);
        CodebrixPipelineCache created = WebGpu.CodebrixDeviceCreatePipelineCache(Device.Null, null);
        Status keyStatus = WebGpu.CodebrixAdapterGetPipelineCacheKey(Adapter.Null, &key);

        //Assert
        size.Should().Be((nuint)0);
        created.IsNull.Should().BeTrue();
        keyStatus.Should().Be(Status.Error);
    }

    [Fact]
    public void the_adapter_key_is_rust_wgpus_pipeline_cache_key()
    {
        //Arrange
        Assert.SkipWhen(plain.SkipReason != null, plain.SkipReason ?? string.Empty);
        AdapterInfo info = default;
        WebGpu.AdapterGetInfo(plain.Adapter, &info);
        StringView key = default;

        //Act
        Status status = WebGpu.CodebrixAdapterGetPipelineCacheKey(plain.Adapter, &key);

        //Assert
        if (info.BackendType == BackendType.Vulkan)
        {
            status.Should().Be(Status.Success);
            GpuSession.Text(key).Should().Be("wgpu_pipeline_cache_vulkan_" + info.VendorID + "_" + info.DeviceID);
        }
        else
        {
            status.Should().Be(Status.Error, "only Vulkan adapters have pipeline caches");
        }

        WebGpu.AdapterInfoFreeMembers(info);
    }

    [Fact]
    public void a_compute_pipeline_compiled_through_a_cache_fills_it()
    {
        //Arrange
        RequireCacheDevice();
        cached.ClearUncapturedErrors();
        CodebrixPipelineCache cache = CreateCache(cached.Device, null, fallback: true);
        nuint emptySize = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);
        ShaderModule module = cached.CreateWgslModule(Doubling);

        //Act
        ComputePipeline pipeline = CreateComputePipeline(cached, module, cache);
        bool exact = RunDoubling(cached, pipeline);
        nuint filledSize = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);

        //Assert
        exact.Should().BeTrue();
        filledSize.Should().BeGreaterThan(emptySize, "the driver adds the compiled pipeline to the chained cache (patches/0011)");
        cached.UncapturedErrors.Should().BeEmpty();

        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.CodebrixPipelineCacheRelease(cache);
    }

    [Fact]
    public void a_render_pipeline_compiled_through_a_cache_fills_it()
    {
        //Arrange
        RequireCacheDevice();
        cached.ClearUncapturedErrors();
        CodebrixPipelineCache cache = CreateCache(cached.Device, null, fallback: true);
        nuint emptySize = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);
        ShaderModule module = cached.CreateWgslModule(Triangle);
        WebGpu.DevicePushErrorScope(cached.Device, ErrorFilter.Validation);

        //Act
        RenderPipeline pipeline = CreateRenderPipeline(cached, module, cache);
        (PopErrorScopeStatus status, ErrorType type, string message) = cached.PopErrorScope();
        nuint filledSize = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);

        //Assert
        status.Should().Be(PopErrorScopeStatus.Success);
        type.Should().Be(ErrorType.NoError, message);
        filledSize.Should().BeGreaterThan(emptySize, "the driver adds the compiled pipeline to the chained cache (patches/0011)");
        cached.UncapturedErrors.Should().BeEmpty();

        WebGpu.RenderPipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.CodebrixPipelineCacheRelease(cache);
    }

    [Fact]
    public void saved_data_starts_a_warm_cache_without_fallback()
    {
        //Arrange
        RequireCacheDevice();
        cached.ClearUncapturedErrors();
        CodebrixPipelineCache cold = CreateCache(cached.Device, null, fallback: true);
        ShaderModule module = cached.CreateWgslModule(Doubling);
        ComputePipeline first = CreateComputePipeline(cached, module, cold);
        byte[] saved = GetData(cold);
        WebGpu.DevicePushErrorScope(cached.Device, ErrorFilter.Validation);

        //Act
        CodebrixPipelineCache warm = CreateCache(cached.Device, saved, fallback: false);
        (PopErrorScopeStatus status, ErrorType type, string message) = cached.PopErrorScope();
        ComputePipeline second = CreateComputePipeline(cached, module, warm);
        bool exact = RunDoubling(cached, second);
        byte[] warmData = GetData(warm);

        //Assert
        status.Should().Be(PopErrorScopeStatus.Success);
        type.Should().Be(ErrorType.NoError, message);
        exact.Should().BeTrue();
        warmData.Length.Should().BeGreaterThanOrEqualTo(saved.Length, "the warm cache holds at least what it was started from");
        Encoding.ASCII.GetString(saved, 0, 8).Should().Be("WGPUPLCH", "the data starts with wgpu-core's header");
        cached.UncapturedErrors.Should().BeEmpty();

        WebGpu.ComputePipelineRelease(second);
        WebGpu.ComputePipelineRelease(first);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.CodebrixPipelineCacheRelease(warm);
        WebGpu.CodebrixPipelineCacheRelease(cold);
    }

    [Fact]
    public void damaged_data_gives_an_empty_cache_with_fallback_and_an_error_without()
    {
        //Arrange
        RequireCacheDevice();
        cached.ClearUncapturedErrors();
        CodebrixPipelineCache cold = CreateCache(cached.Device, null, fallback: true);
        ShaderModule module = cached.CreateWgslModule(Doubling);
        ComputePipeline pipeline = CreateComputePipeline(cached, module, cold);
        byte[] damaged = GetData(cold);
        for (int index = 0; index < 8; index++) damaged[index] ^= 0x5A;

        //Act
        WebGpu.DevicePushErrorScope(cached.Device, ErrorFilter.Validation);
        CodebrixPipelineCache tolerant = CreateCache(cached.Device, damaged, fallback: true);
        (PopErrorScopeStatus _, ErrorType withFallback, string _) = cached.PopErrorScope();
        WebGpu.DevicePushErrorScope(cached.Device, ErrorFilter.Validation);
        CodebrixPipelineCache strict = CreateCache(cached.Device, damaged, fallback: false);
        (PopErrorScopeStatus _, ErrorType withoutFallback, string _) = cached.PopErrorScope();
        ComputePipeline throughTolerant = CreateComputePipeline(cached, module, tolerant);
        bool exact = RunDoubling(cached, throughTolerant);

        //Assert
        withFallback.Should().Be(ErrorType.NoError);
        withoutFallback.Should().Be(ErrorType.Validation);
        exact.Should().BeTrue("the fallback cache is an ordinary, empty cache");
        cached.UncapturedErrors.Should().BeEmpty();

        WebGpu.ComputePipelineRelease(throughTolerant);
        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.CodebrixPipelineCacheRelease(strict);
        WebGpu.CodebrixPipelineCacheRelease(tolerant);
        WebGpu.CodebrixPipelineCacheRelease(cold);
    }

    [Fact]
    public void get_data_reports_the_full_size_to_a_short_buffer()
    {
        //Arrange
        RequireCacheDevice();
        CodebrixPipelineCache cache = CreateCache(cached.Device, null, fallback: true);
        ShaderModule module = cached.CreateWgslModule(Doubling);
        ComputePipeline pipeline = CreateComputePipeline(cached, module, cache);
        nuint size = WebGpu.CodebrixPipelineCacheGetData(cache, null, 0);
        byte[] small = new byte[8];
        byte[] full = GetData(cache);

        //Act
        nuint reported;
        fixed (byte* bytes = small) reported = WebGpu.CodebrixPipelineCacheGetData(cache, bytes, (nuint)small.Length);

        //Assert
        reported.Should().Be(size);
        small.Should().Equal(full.Take(small.Length));

        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.CodebrixPipelineCacheRelease(cache);
    }

    [Fact]
    public void proc_status_marks_the_cache_functions_for_vulkan_and_add_ref_release_everywhere()
    {
        //Arrange
        CodeBrixWebGpuNative.EnsureLoaded();
        CodebrixProcStatus expected = OperatingSystem.IsMacOS() ? CodebrixProcStatus.NotAvailableOnBackend : CodebrixProcStatus.Implemented;

        //Act
        CodebrixProcStatus Status(string symbol) => WebGpu.CodebrixGetProcStatus((uint)CodebrixProcTable.Symbols.ToList().IndexOf(symbol));

        //Assert
        Status("wgpuCodebrixDeviceCreatePipelineCache").Should().Be(expected);
        Status("wgpuCodebrixPipelineCacheGetData").Should().Be(expected);
        Status("wgpuCodebrixAdapterGetPipelineCacheKey").Should().Be(expected);
        Status("wgpuCodebrixPipelineCacheAddRef").Should().Be(CodebrixProcStatus.Implemented);
        Status("wgpuCodebrixPipelineCacheRelease").Should().Be(CodebrixProcStatus.Implemented);
    }
}
