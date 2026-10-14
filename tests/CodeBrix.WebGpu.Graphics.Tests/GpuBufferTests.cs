using System;
using System.Numerics;
using System.Threading.Tasks;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Buffers: typed creation helpers, uploads and readback.</summary>
public class GpuBufferTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public GpuBufferTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Vertex_buffer_round_trips_structs()
    {
        //Arrange
        gpu.RequireDevice();
        MeshVertex[] vertices =
        {
            new MeshVertex(new Vector3(1, 2, 3), Vector3.UnitY, new Vector2(0.25f, 0.75f), Color4.Red),
            new MeshVertex(new Vector3(-4, 5, -6), Vector3.UnitZ, new Vector2(1, 0), Color4.Blue),
        };

        //Act
        using GpuBuffer buffer = gpu.Context.CreateVertexBuffer<MeshVertex>(vertices, "vertices");
        MeshVertex[] read = buffer.Read<MeshVertex>();

        //Assert
        buffer.Size.Should().Be(96UL);
        (buffer.Usage & BufferUsage.Vertex).Should().Be(BufferUsage.Vertex);
        read[0].Position.Should().Be(new Vector3(1, 2, 3));
        read[1].TexCoord.Should().Be(new Vector2(1, 0));
        read[1].Color.Should().Be(new Vector4(0, 0, 1, 1));
    }

    [Fact]
    public void Index_buffers_round_trip_16_and_32_bit_indices()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuBuffer small = gpu.Context.CreateIndexBuffer(new ushort[] { 0, 1, 2 });
        using GpuBuffer large = gpu.Context.CreateIndexBuffer(new uint[] { 70000, 1, 2 });

        //Assert
        small.Size.Should().Be(8UL);
        small.Read<ushort>(3).Should().Equal(0, 1, 2);
        large.Read<uint>().Should().Equal(70000u, 1u, 2u);
        (large.Usage & BufferUsage.Index).Should().Be(BufferUsage.Index);
    }

    [Fact]
    public void Uniform_buffer_is_rounded_to_16_bytes_and_holds_the_value()
    {
        //Arrange
        gpu.RequireDevice();
        Vector3 value = new Vector3(1, 2, 3);

        //Act
        using GpuBuffer buffer = gpu.Context.CreateUniformBufferFrom(in value);

        //Assert
        buffer.Size.Should().Be(16UL);
        buffer.Read<float>().Should().Equal(1f, 2f, 3f, 0f);
    }

    [Fact]
    public void Storage_buffer_write_at_an_offset_updates_only_that_range()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer buffer = gpu.Context.CreateStorageBuffer<int>(new[] { 1, 2, 3, 4 });

        //Act
        buffer.Write<int>(new[] { 9, 8 }, 8);
        int[] tail = new int[2];
        buffer.Read<int>(tail, 8);

        //Assert
        buffer.Read<int>().Should().Equal(1, 2, 9, 8);
        tail.Should().Equal(9, 8);
    }

    [Fact]
    public void Odd_sized_writes_are_padded_to_four_bytes()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer buffer = gpu.Context.CreateBuffer(3, BufferUsage.Storage);

        //Act
        buffer.Write<byte>(new byte[] { 10, 20, 30 });
        byte[] read = new byte[3];
        buffer.Read<byte>(read);

        //Assert
        buffer.Size.Should().Be(4UL);
        read.Should().Equal(10, 20, 30);
    }

    [Fact]
    public void Write_past_the_end_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer buffer = gpu.Context.CreateUniformBuffer(16);

        //Act
        Action act = () => buffer.Write<float>(new float[5]);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Read_of_a_map_buffer_without_CopySrc_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer buffer = gpu.Context.CreateBuffer(16, BufferUsage.MapRead | BufferUsage.CopyDst);

        //Act
        Action act = () => buffer.Read<int>();

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Invalid_usage_combination_throws_GraphicsException_with_the_validation_message()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action act = () => gpu.Context.CreateBuffer(16, BufferUsage.MapRead | BufferUsage.Vertex, "bad");

        //Assert
        act.Should().Throw<GraphicsException>().WithMessage("*bad*");
    }

    [Fact]
    public void Mapped_at_creation_buffer_is_filled_through_the_mapped_span()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuBuffer buffer = gpu.Context.CreateMappedBuffer(16, BufferUsage.Storage, "mapped");
        BufferMapState whileMapped = buffer.MapState;
        Span<uint> words = buffer.GetMappedSpan<uint>();
        words[0] = 11;
        words[3] = 44;
        buffer.WriteMapped<uint>(new uint[] { 22, 33 }, 4);
        buffer.Unmap();

        //Assert
        whileMapped.Should().Be(BufferMapState.Mapped);
        buffer.MapState.Should().Be(BufferMapState.Unmapped);
        buffer.Read<uint>().Should().Equal(11u, 22u, 33u, 44u);
    }

    [Fact]
    public void Initialized_buffer_holds_the_data_without_queue_staging()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuBuffer buffer = gpu.Context.CreateInitializedBuffer<int>(new[] { 5, 6, 7 }, BufferUsage.Vertex);

        //Assert
        buffer.MapState.Should().Be(BufferMapState.Unmapped);
        buffer.Size.Should().Be(12UL);
        buffer.Read<int>().Should().Equal(5, 6, 7);
    }

    [Fact]
    public void Map_for_reading_exposes_the_gpu_results()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer source = gpu.Context.CreateStorageBuffer<uint>(new uint[] { 1, 2, 3, 4, 5, 6 });
        using GpuBuffer readback = gpu.Context.CreateBuffer(24, BufferUsage.MapRead | BufferUsage.CopyDst);
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.CopyBuffer(source, 0, readback, 0, 24);
            frame.Submit();
        }

        //Act
        readback.Map(MapMode.Read, 8, 16);
        uint[] read = new uint[4];
        readback.ReadMapped<uint>(read, 8);
        uint fourth = readback.GetMappedSpan<uint>(12, 1)[0];
        Action write = () => readback.WriteMapped<uint>(new uint[] { 9 }, 8);
        readback.Unmap();

        //Assert
        read.Should().Equal(3u, 4u, 5u, 6u);
        fourth.Should().Be(4u);
        write.Should().Throw<InvalidOperationException>();
        readback.MappedSize.Should().Be(0UL);
    }

    [Fact]
    public async Task MapAsync_completes_without_pumping()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer source = gpu.Context.CreateStorageBuffer<uint>(new uint[] { 42, 43 });
        using GpuBuffer readback = gpu.Context.CreateBuffer(8, BufferUsage.MapRead | BufferUsage.CopyDst);
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.CopyBuffer(source, 0, readback, 0, 8);
            frame.Submit();
        }

        //Act
        Task mapping = readback.MapAsync(MapMode.Read);
        BufferMapState pending = readback.MapState;
        await mapping.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        uint[] read = readback.GetMappedSpan<uint>().ToArray();
        readback.Unmap();

        //Assert
        (pending == BufferMapState.Pending || pending == BufferMapState.Mapped).Should().BeTrue();
        read.Should().Equal(42u, 43u);
    }

    [Fact]
    public void Map_write_then_unmap_uploads_the_data()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer staging = gpu.Context.CreateBuffer(8, BufferUsage.MapWrite | BufferUsage.CopySrc);
        using GpuBuffer target = gpu.Context.CreateStorageBuffer(8);

        //Act
        staging.Map(MapMode.Write);
        staging.WriteMapped<uint>(new uint[] { 7, 8 }, 0);
        staging.Unmap();
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.CopyBuffer(staging, 0, target, 0, 8);
            frame.Submit();
        }

        //Assert
        target.Read<uint>().Should().Equal(7u, 8u);
    }

    [Fact]
    public void Mapping_checks_usage_alignment_and_state()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer storage = gpu.Context.CreateStorageBuffer(16);
        using GpuBuffer readable = gpu.Context.CreateBuffer(16, BufferUsage.MapRead | BufferUsage.CopyDst);
        using GpuBuffer mapped = gpu.Context.CreateMappedBuffer(16, BufferUsage.Storage);

        //Act
        Action noUsage = () => storage.Map(MapMode.Read);
        Action misaligned = () => readable.Map(MapMode.Read, 4);
        Action notMapped = () => readable.GetMappedRange(0);
        Action twice = () => mapped.Map(MapMode.Write);
        Action queueWrite = () => mapped.Write<uint>(new uint[] { 1 });
        Action outside = () => mapped.GetMappedRange(8, 16);

        //Assert
        noUsage.Should().Throw<InvalidOperationException>();
        misaligned.Should().Throw<ArgumentOutOfRangeException>();
        notMapped.Should().Throw<InvalidOperationException>();
        twice.Should().Throw<InvalidOperationException>();
        queueWrite.Should().Throw<InvalidOperationException>().WithMessage("*Mapped*");
        outside.Should().Throw<ArgumentOutOfRangeException>();
        mapped.Unmap();
    }

    [Fact]
    public void Destroy_frees_the_buffer_and_disposes_the_wrapper()
    {
        //Arrange
        gpu.RequireDevice();
        GpuBuffer buffer = gpu.Context.CreateStorageBuffer(16);

        //Act
        buffer.Destroy();
        buffer.Destroy();

        //Assert
        buffer.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Wrapped_buffer_reads_size_and_usage_from_the_native_buffer()
    {
        //Arrange
        gpu.RequireDevice();
        GpuBuffer original = gpu.Context.CreateStorageBuffer<uint>(new uint[] { 3, 4, 5 });

        //Act
        using GpuBuffer wrapped = gpu.Context.WrapBuffer(original.Handle, "wrapped");
        original.Dispose();

        //Assert
        wrapped.IsWrapped.Should().BeTrue();
        wrapped.Size.Should().Be(12UL);
        (wrapped.Usage & BufferUsage.Storage).Should().Be(BufferUsage.Storage);
        wrapped.Read<uint>().Should().Equal(3u, 4u, 5u);
    }
}
