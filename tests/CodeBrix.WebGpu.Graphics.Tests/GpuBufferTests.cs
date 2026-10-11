using System;
using System.Numerics;
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
}
