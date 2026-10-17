using System;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// Pipeline layouts with absent bind group slots (wgpu-native-tools/patches/0004): a layout of
/// <c>[null, group 1]</c> drives a compute pipeline that binds group 1 only, and an invalid layout descriptor is
/// a validation error an error scope captures. Every test skips, with the reason, when the machine has no adapter.
/// </summary>
public unsafe class PipelineLayoutTests : IClassFixture<GpuFixture>
{
    private const string DoubleGroupOne =
        "@group(1) @binding(0) var<storage, read_write> data: array<u32>;\n"
        + "@compute @workgroup_size(64)\n"
        + "fn main(@builtin(global_invocation_id) id: vec3<u32>) {\n"
        + "    data[id.x] = data[id.x] * 2u + 1u;\n"
        + "}\n";

    private readonly GpuSession gpu;

    public PipelineLayoutTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private void RequireGpu() => Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);

    private BindGroupLayout CreateStorageLayout()
    {
        BindGroupLayoutEntry entry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Compute,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Storage },
        };
        BindGroupLayoutDescriptor descriptor = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &entry };
        return WebGpu.DeviceCreateBindGroupLayout(gpu.Device, &descriptor);
    }

    [Fact]
    public void layout_with_an_absent_group_0_runs_a_compute_pipeline_that_binds_group_1_only()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        const int count = 128;
        const ulong size = count * sizeof(uint);
        uint[] input = Enumerable.Range(0, count).Select(i => (uint)(i * 3)).ToArray();
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Validation);
        BindGroupLayout groupOne = CreateStorageLayout();
        BindGroupLayout* layouts = stackalloc BindGroupLayout[2];
        layouts[0] = BindGroupLayout.Null;
        layouts[1] = groupOne;
        PipelineLayoutDescriptor layoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = layouts };
        PipelineLayout layout = WebGpu.DeviceCreatePipelineLayout(gpu.Device, &layoutDescriptor);
        ShaderModule module = gpu.CreateWgslModule(DoubleGroupOne);
        ComputePipeline pipeline = gpu.CreateComputePipeline(module, layout);
        Buffer storage = gpu.CreateBuffer(BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, size);
        Buffer readback = gpu.CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, size);
        fixed (uint* data = input) WebGpu.QueueWriteBuffer(gpu.Queue, storage, 0, data, (nuint)size);
        BindGroupEntry binding = new BindGroupEntry { Binding = 0, Buffer = storage, Offset = 0, Size = size };
        BindGroupDescriptor groupDescriptor = new BindGroupDescriptor { Layout = groupOne, EntryCount = 1, Entries = &binding };
        BindGroup group = WebGpu.DeviceCreateBindGroup(gpu.Device, &groupDescriptor);

        //Act
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        ComputePassEncoder pass = WebGpu.CommandEncoderBeginComputePass(encoder, null);
        WebGpu.ComputePassEncoderSetPipeline(pass, pipeline);
        WebGpu.ComputePassEncoderSetBindGroup(pass, 1, group, 0, null);
        WebGpu.ComputePassEncoderDispatchWorkgroups(pass, count / 64, 1, 1);
        WebGpu.ComputePassEncoderEnd(pass);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, storage, 0, readback, 0, size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) scope = gpu.PopErrorScope();
        scope.Type.Should().Be(ErrorType.NoError, scope.Message);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        MapAsyncStatus mapped = GpuSession.MapRead(gpu.Device, readback, (nuint)size);
        uint[] output = new ReadOnlySpan<uint>(WebGpu.BufferGetConstMappedRange(readback, 0, (nuint)size), count).ToArray();
        WebGpu.BufferUnmap(readback);

        //Assert
        layout.IsNull.Should().BeFalse();
        mapped.Should().Be(MapAsyncStatus.Success);
        output.Should().Equal(input.Select(v => (v * 2) + 1));
        gpu.UncapturedErrors.Should().BeEmpty();

        WebGpu.CommandBufferRelease(commands);
        WebGpu.ComputePassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BindGroupRelease(group);
        WebGpu.BufferRelease(readback);
        WebGpu.BufferRelease(storage);
        WebGpu.ComputePipelineRelease(pipeline);
        WebGpu.ShaderModuleRelease(module);
        WebGpu.PipelineLayoutRelease(layout);
        WebGpu.BindGroupLayoutRelease(groupOne);
    }

    [Fact]
    public void layout_with_more_groups_than_the_limit_is_a_validation_error_a_scope_captures()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        Limits limits = default;
        WebGpu.DeviceGetLimits(gpu.Device, &limits);
        int count = (int)limits.MaxBindGroups + 1;
        BindGroupLayout groupOne = CreateStorageLayout();
        BindGroupLayout* layouts = stackalloc BindGroupLayout[count];
        for (int i = 0; i < count; i++) layouts[i] = i == 1 ? groupOne : BindGroupLayout.Null;
        PipelineLayoutDescriptor descriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = (nuint)count, BindGroupLayouts = layouts };

        //Act
        WebGpu.DevicePushErrorScope(gpu.Device, ErrorFilter.Validation);
        PipelineLayout layout = WebGpu.DeviceCreatePipelineLayout(gpu.Device, &descriptor);
        (PopErrorScopeStatus Status, ErrorType Type, string Message) scope = gpu.PopErrorScope();

        //Assert
        scope.Status.Should().Be(PopErrorScopeStatus.Success);
        scope.Type.Should().Be(ErrorType.Validation);
        layout.IsNull.Should().BeFalse();
        gpu.UncapturedErrors.Should().BeEmpty();
        WebGpu.PipelineLayoutRelease(layout);
        WebGpu.BindGroupLayoutRelease(groupOne);
    }
}
