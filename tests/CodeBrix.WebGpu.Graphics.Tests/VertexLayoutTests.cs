using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Vertex layouts derived from structs and described fluently (no GPU).</summary>
public class VertexLayoutTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PlainVertex
    {
        public Vector3 Position;
        public Vector2 Uv;
        public float Weight;
        public uint Id;
        public int Signed;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MarkedVertex
    {
        [VertexElement(0)]
        public Vector2 Position;

        public float Padding;

        [VertexElement(2, VertexFormat.Unorm8x4)]
        public uint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceVertex
    {
        public Matrix4x4 Model;
        public Vector4 Tint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnsupportedVertex
    {
        public double Value;
    }

    [Fact]
    public void FromStruct_infers_formats_offsets_and_stride()
    {
        //Arrange
        //Act
        VertexLayout layout = VertexLayout.FromStruct<PlainVertex>();

        //Assert
        layout.ArrayStride.Should().Be(48u);
        layout.StepMode.Should().Be(VertexStepMode.Vertex);
        layout.Elements.Should().Equal(
            new VertexElement(0, VertexFormat.Float32x3, 0),
            new VertexElement(1, VertexFormat.Float32x2, 12),
            new VertexElement(2, VertexFormat.Float32, 20),
            new VertexElement(3, VertexFormat.Uint32, 24),
            new VertexElement(4, VertexFormat.Sint32, 28),
            new VertexElement(5, VertexFormat.Float32x4, 32));
        layout.NextLocation.Should().Be(6);
    }

    [Fact]
    public void FromStruct_uses_only_marked_fields_with_explicit_formats()
    {
        //Arrange
        //Act
        VertexLayout layout = VertexLayout.FromStruct<MarkedVertex>();

        //Assert
        layout.ArrayStride.Should().Be(16u);
        layout.Elements.Should().Equal(
            new VertexElement(0, VertexFormat.Float32x2, 0),
            new VertexElement(2, VertexFormat.Unorm8x4, 12));
    }

    [Fact]
    public void FromStruct_expands_a_matrix_into_four_rows_after_the_first_location()
    {
        //Arrange
        //Act
        VertexLayout layout = VertexLayout.FromStruct<InstanceVertex>(VertexStepMode.Instance, 4);

        //Assert
        layout.StepMode.Should().Be(VertexStepMode.Instance);
        layout.ArrayStride.Should().Be(80u);
        layout.Elements.Should().Equal(
            new VertexElement(4, VertexFormat.Float32x4, 0),
            new VertexElement(5, VertexFormat.Float32x4, 16),
            new VertexElement(6, VertexFormat.Float32x4, 32),
            new VertexElement(7, VertexFormat.Float32x4, 48),
            new VertexElement(8, VertexFormat.Float32x4, 64));
        layout.NextLocation.Should().Be(9);
    }

    [Fact]
    public void FromStruct_rejects_a_field_with_no_inferred_format()
    {
        //Arrange
        //Act
        Action act = () => VertexLayout.FromStruct<UnsupportedVertex>();

        //Assert
        act.Should().Throw<ArgumentException>().WithMessage("*Value*");
    }

    [Fact]
    public void FromStruct_matches_the_mesh_vertex()
    {
        //Arrange
        //Act
        VertexLayout layout = VertexLayout.FromStruct<MeshVertex>();

        //Assert
        layout.ArrayStride.Should().Be(48u);
        layout.ToString().Should().Be("stride 48 Vertex: @0 Float32x3 +0, @1 Float32x3 +12, @2 Float32x2 +24, @3 Float32x4 +32");
    }

    [Fact]
    public void Describe_appends_attributes_at_increasing_offsets()
    {
        //Arrange
        //Act
        VertexLayout layout = VertexLayout.Describe(VertexStepMode.Vertex, 1)
            .Add(VertexFormat.Float32x3)
            .Skip(4)
            .Add(VertexFormat.Unorm8x4)
            .Add(VertexFormat.Float32x2, 7)
            .Build();

        //Assert
        layout.ArrayStride.Should().Be(28u);
        layout.Elements.Should().Equal(
            new VertexElement(1, VertexFormat.Float32x3, 0),
            new VertexElement(2, VertexFormat.Unorm8x4, 16),
            new VertexElement(7, VertexFormat.Float32x2, 20));
    }

    [Fact]
    public void VertexFormats_SizeOf_knows_every_size()
    {
        //Arrange
        //Act
        //Assert
        VertexFormats.SizeOf(VertexFormat.Unorm8x4).Should().Be(4);
        VertexFormats.SizeOf(VertexFormat.Float16x2).Should().Be(4);
        VertexFormats.SizeOf(VertexFormat.Float32x3).Should().Be(12);
        VertexFormats.SizeOf(VertexFormat.Uint32x4).Should().Be(16);
    }
}
