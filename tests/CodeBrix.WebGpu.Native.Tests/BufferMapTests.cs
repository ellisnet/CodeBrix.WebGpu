using System;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// <c>BufferMapAsync</c> with <see cref="Constants.WholeMapSize" /> maps from the offset to the end of the buffer
/// (wgpu-native-tools/patches/0006, upstream #602). Every test skips, with the reason, when the machine has no
/// adapter.
/// </summary>
public unsafe class BufferMapTests : IClassFixture<GpuFixture>
{
    private const int Words = 16;
    private const ulong Size = Words * sizeof(uint);

    private readonly GpuSession gpu;

    public BufferMapTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private void RequireGpu() => Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);

    private Buffer CreateFilledReadback(uint[] words)
    {
        Buffer upload = gpu.CreateBuffer(BufferUsage.CopySrc | BufferUsage.CopyDst, Size);
        Buffer readback = gpu.CreateBuffer(BufferUsage.MapRead | BufferUsage.CopyDst, Size);
        fixed (uint* data = words) WebGpu.QueueWriteBuffer(gpu.Queue, upload, 0, data, (nuint)Size);
        CommandEncoder encoder = WebGpu.DeviceCreateCommandEncoder(gpu.Device, null);
        WebGpu.CommandEncoderCopyBufferToBuffer(encoder, upload, 0, readback, 0, Size);
        CommandBuffer commands = WebGpu.CommandEncoderFinish(encoder, null);
        WebGpu.QueueSubmit(gpu.Queue, 1, &commands);
        WebGpu.CommandBufferRelease(commands);
        WebGpu.CommandEncoderRelease(encoder);
        WebGpu.BufferRelease(upload);
        return readback;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void BufferMapAsync_with_WholeMapSize_maps_from_the_offset_to_the_end(int offset)
    {
        //Arrange
        RequireGpu();
        gpu.ClearUncapturedErrors();
        uint[] words = Enumerable.Range(0, Words).Select(i => 0xA5000000u + (uint)i).ToArray();
        Buffer readback = CreateFilledReadback(words);
        int skipped = offset / sizeof(uint);

        //Act
        MapAsyncStatus mapped = GpuSession.MapRead(gpu.Device, readback, (nuint)offset, Constants.WholeMapSize);
        uint* range = mapped == MapAsyncStatus.Success
            ? (uint*)WebGpu.BufferGetConstMappedRange(readback, (nuint)offset, (nuint)(Size - (ulong)offset))
            : null;
        uint[] output = range == null ? Array.Empty<uint>() : new ReadOnlySpan<uint>(range, Words - skipped).ToArray();
        if (mapped == MapAsyncStatus.Success) WebGpu.BufferUnmap(readback);

        //Assert
        mapped.Should().Be(MapAsyncStatus.Success);
        output.Should().Equal(words.Skip(skipped));
        gpu.UncapturedErrors.Should().BeEmpty();
        WebGpu.BufferRelease(readback);
    }
}
