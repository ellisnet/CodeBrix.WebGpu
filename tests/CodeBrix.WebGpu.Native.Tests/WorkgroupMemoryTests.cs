using System;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// Workgroup memory starts zeroed (wgpu-native-tools/patches/0005): a shader that reads its
/// <c>var&lt;workgroup&gt;</c> array before writing it reads zeros, even straight after another pipeline left
/// the same amount of shared memory full of non-zero values. Skips, with the reason, when the machine has no
/// adapter.
/// </summary>
public unsafe class WorkgroupMemoryTests : IClassFixture<GpuFixture>
{
    private const int WorkgroupSize = 64;
    private const int Workgroups = 256;

    // Leaves every workgroup's shared memory holding non-zero values.
    private const string FillShared =
        "var<workgroup> shared_words: array<u32, 64>;\n"
        + "@group(0) @binding(0) var<storage, read_write> sink: array<u32>;\n"
        + "@compute @workgroup_size(64)\n"
        + "fn main(@builtin(local_invocation_index) i: u32, @builtin(workgroup_id) w: vec3<u32>) {\n"
        + "    shared_words[i] = 0xC0DEB000u + i + w.x;\n"
        + "    workgroupBarrier();\n"
        + "    sink[w.x * 64u + i] = shared_words[63u - i];\n"
        + "}\n";

    // Reads shared memory BEFORE writing it and stores what it read.
    private const string ReadSharedFirst =
        "var<workgroup> shared_words: array<u32, 64>;\n"
        + "@group(0) @binding(0) var<storage, read_write> seen: array<u32>;\n"
        + "@compute @workgroup_size(64)\n"
        + "fn main(@builtin(local_invocation_index) i: u32, @builtin(workgroup_id) w: vec3<u32>) {\n"
        + "    seen[w.x * 64u + i] = shared_words[i];\n"
        + "    workgroupBarrier();\n"
        + "    shared_words[i] = 0xFFFFFFFFu;\n"
        + "}\n";

    private readonly GpuSession gpu;

    public WorkgroupMemoryTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private void RequireGpu() => Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);

    [Fact]
    public void workgroup_memory_read_before_any_write_is_zero()
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        const int count = WorkgroupSize * Workgroups;
        const ulong size = count * sizeof(uint);
        ShaderModule fillModule = gpu.CreateWgslModule(FillShared);
        ShaderModule readModule = gpu.CreateWgslModule(ReadSharedFirst);
        ComputePipeline fill = gpu.CreateComputePipeline(fillModule, PipelineLayout.Null);
        ComputePipeline read = gpu.CreateComputePipeline(readModule, PipelineLayout.Null);
        Buffer sink = gpu.CreateBuffer(BufferUsage.Storage, size);
        Buffer seen = gpu.CreateBuffer(BufferUsage.Storage | BufferUsage.CopySrc, size);
        Buffer readback = gpu.CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, size);
        BindGroupLayout fillLayout = WebGpu.ComputePipelineGetBindGroupLayout(fill, 0);
        BindGroupLayout readLayout = WebGpu.ComputePipelineGetBindGroupLayout(read, 0);
        BindGroupEntry fillEntry = new BindGroupEntry { Binding = 0, Buffer = sink, Size = size };
        BindGroupEntry readEntry = new BindGroupEntry { Binding = 0, Buffer = seen, Size = size };
        BindGroupDescriptor fillDescriptor = new BindGroupDescriptor { Layout = fillLayout, EntryCount = 1, Entries = &fillEntry };
        BindGroupDescriptor readDescriptor = new BindGroupDescriptor { Layout = readLayout, EntryCount = 1, Entries = &readEntry };
        BindGroup fillGroup = WebGpu.DeviceCreateBindGroup(gpu.Device, &fillDescriptor);
        BindGroup readGroup = WebGpu.DeviceCreateBindGroup(gpu.Device, &readDescriptor);

        //Act
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        ComputePassEncoder pass = WebGpu.CommandEncoderBeginComputePass(encoder, null);
        for (int round = 0; round < 4; round++)
        {
            WebGpu.ComputePassEncoderSetPipeline(pass, fill);
            WebGpu.ComputePassEncoderSetBindGroup(pass, 0, fillGroup, 0, null);
            WebGpu.ComputePassEncoderDispatchWorkgroups(pass, Workgroups, 1, 1);
            WebGpu.ComputePassEncoderSetPipeline(pass, read);
            WebGpu.ComputePassEncoderSetBindGroup(pass, 0, readGroup, 0, null);
            WebGpu.ComputePassEncoderDispatchWorkgroups(pass, Workgroups, 1, 1);
        }

        WebGpu.ComputePassEncoderEnd(pass);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, seen, 0, readback, 0, size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        MapAsyncStatus mapped = GpuSession.MapRead(gpu.Device, readback, (nuint)size);
        uint[] output = new ReadOnlySpan<uint>(WebGpu.BufferGetConstMappedRange(readback, 0, (nuint)size), count).ToArray();
        WebGpu.BufferUnmap(readback);

        //Assert
        mapped.Should().Be(MapAsyncStatus.Success);
        output.Count(word => word != 0).Should().Be(0);
        gpu.UncapturedErrors.Should().BeEmpty();

        WebGpu.CommandBufferRelease(commands);
        WebGpu.ComputePassEncoderRelease(pass);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BindGroupRelease(readGroup);
        WebGpu.BindGroupRelease(fillGroup);
        WebGpu.BindGroupLayoutRelease(readLayout);
        WebGpu.BindGroupLayoutRelease(fillLayout);
        WebGpu.BufferRelease(readback);
        WebGpu.BufferRelease(seen);
        WebGpu.BufferRelease(sink);
        WebGpu.ComputePipelineRelease(read);
        WebGpu.ComputePipelineRelease(fill);
        WebGpu.ShaderModuleRelease(readModule);
        WebGpu.ShaderModuleRelease(fillModule);
    }
}
