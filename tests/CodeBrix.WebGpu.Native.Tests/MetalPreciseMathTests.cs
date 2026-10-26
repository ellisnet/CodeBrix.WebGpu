using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// The opt-in IEEE floating point for WGSL compiled to Metal (wgpu-native-tools/patches/0010,
/// <c>wgpuCodebrixSetMetalPreciseMath</c>): the switch exists only where the Metal backend is built, and with it
/// on a NaN made on the GPU compares unequal to itself - Metal's default fast math removes that comparison. The
/// GPU test runs on macOS only and skips, with the reason, when the machine has no adapter. The switch is
/// process-wide; a pipeline another test creates while it is on gets IEEE semantics, which WGSL allows.
/// </summary>
public unsafe class MetalPreciseMathTests : IClassFixture<GpuFixture>
{
    // 0/0 from a buffer (nothing can fold it), then compared with itself.
    private const string NanCompare =
        "@group(0) @binding(0) var<storage, read_write> data: array<u32>;\n"
        + "@compute @workgroup_size(1)\n"
        + "fn main() { let zero = bitcast<f32>(data[0]); let x = zero / zero; data[1] = select(0u, 1u, x != x); }\n";

    private readonly GpuSession gpu;

    public MetalPreciseMathTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    [Fact]
    public void SetMetalPreciseMath_succeeds_only_on_a_Metal_build()
    {
        //Act
        Status status = WebGpu.CodebrixSetMetalPreciseMath(0);

        //Assert
        status.Should().Be(OperatingSystem.IsMacOS() ? Status.Success : Status.Error);
    }

    [Fact]
    public void SetMetalPreciseMath_is_a_Metal_function_in_the_proc_status_table() =>
        CodeBrixWebGpuNative.GetProcStatus("wgpuCodebrixSetMetalPreciseMath")
            .Should().Be(OperatingSystem.IsMacOS() ? CodebrixProcStatus.Implemented : CodebrixProcStatus.NotAvailableOnBackend);

    [Fact]
    public void nan_compares_unequal_to_itself_with_precise_math_on()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Metal only (wgpuCodebrixSetMetalPreciseMath returns Error elsewhere).");
        Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);
        gpu.ClearUncapturedErrors();
        const ulong size = 2 * sizeof(uint);
        uint[] input = { 0u, 7u };

        //Act
        uint[] output;
        WebGpu.CodebrixSetMetalPreciseMath(1).Should().Be(Status.Success);
        try
        {
            output = RunNanCompare(input, size);
        }
        finally
        {
            WebGpu.CodebrixSetMetalPreciseMath(0);
        }

        //Assert
        output[1].Should().Be(1u, "with precise math the comparison of a NaN with itself must survive the Metal compiler");
        gpu.UncapturedErrors.Should().BeEmpty();
    }

    private uint[] RunNanCompare(uint[] input, ulong size)
    {
        ShaderModule module = gpu.CreateWgslModule(NanCompare);
        ComputePipeline pipeline = gpu.CreateComputePipeline(module, PipelineLayout.Null);
        Buffer storage = gpu.CreateBuffer(BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, size);
        Buffer readback = gpu.CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, size);
        fixed (uint* data = input) WebGpu.QueueWriteBuffer(gpu.Queue, storage, 0, data, (nuint)size);
        BindGroupLayout layout = WebGpu.ComputePipelineGetBindGroupLayout(pipeline, 0);
        BindGroupEntry entry = new BindGroupEntry { Binding = 0, Buffer = storage, Size = size };
        BindGroupDescriptor descriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry };
        BindGroup group = WebGpu.DeviceCreateBindGroup(gpu.Device, &descriptor);

        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        ComputePassEncoder pass = WebGpu.CommandEncoderBeginComputePass(encoder, null);
        WebGpu.ComputePassEncoderSetPipeline(pass, pipeline);
        WebGpu.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
        WebGpu.ComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
        WebGpu.ComputePassEncoderEnd(pass);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, storage, 0, readback, 0, size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        GpuSession.MapRead(gpu.Device, readback, (nuint)size).Should().Be(MapAsyncStatus.Success);
        uint[] output = new ReadOnlySpan<uint>(WebGpu.BufferGetConstMappedRange(readback, 0, (nuint)size), input.Length).ToArray();
        WebGpu.BufferUnmap(readback);

        WebGpu.CommandBufferRelease(commands);
        WebGpu.ComputePassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BindGroupRelease(group);
        WebGpu.BindGroupLayoutRelease(layout);
        WebGpu.BufferRelease(readback);
        WebGpu.BufferRelease(storage);
        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
        return output;
    }
}
