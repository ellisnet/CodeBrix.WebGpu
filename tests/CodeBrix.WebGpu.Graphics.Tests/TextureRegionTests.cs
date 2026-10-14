using System;
using System.Runtime.CompilerServices;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Pure-CPU checks of texture regions, copy row layouts, indirect argument structs and pass options.</summary>
public class TextureRegionTests
{
    [Fact]
    public void Region_keeps_its_box_and_describes_it()
    {
        //Arrange / Act
        TextureRegion region = new TextureRegion(4, 8, 16, 2, 1, 0, 1, TextureAspect.All);

        //Assert
        region.X.Should().Be(4);
        region.Y.Should().Be(8);
        region.Width.Should().Be(16);
        region.Height.Should().Be(2);
        region.MipLevel.Should().Be(1);
        region.Layers.Should().Be(1);
        region.ToString().Should().Be("(4, 8) 16 x 2 mip 1");
    }

    [Fact]
    public void Region_rejects_empty_and_negative_boxes()
    {
        //Arrange / Act
        Action empty = () => new TextureRegion(0, 0, 0, 1);
        Action negative = () => new TextureRegion(-1, 0, 1, 1);
        Action noLayers = () => new TextureRegion(0, 0, 1, 1, 0, 0, 0);

        //Assert
        empty.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
        noLayers.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Copy_rows_align_to_256_bytes()
    {
        //Arrange / Act / Assert
        TextureCopyLayout.TightBytesPerRow(5, TextureFormat.RGBA8Unorm).Should().Be(20);
        TextureCopyLayout.AlignedBytesPerRow(5, TextureFormat.RGBA8Unorm).Should().Be(256);
        TextureCopyLayout.AlignedBytesPerRow(64, TextureFormat.RGBA8Unorm).Should().Be(256);
        TextureCopyLayout.AlignedBytesPerRow(65, TextureFormat.RGBA8Unorm).Should().Be(512);
        TextureCopyLayout.AlignedBytesPerRow(3, TextureFormat.RGBA32Float).Should().Be(256);
        TextureCopyLayout.BufferSize(5, 3, TextureFormat.R8Unorm).Should().Be(768UL);
    }

    [Fact]
    public void Unknown_texel_sizes_are_refused()
    {
        //Arrange / Act
        Action act = () => TextureCopyLayout.TightBytesPerRow(4, TextureFormat.BC1RGBAUnorm);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Pack_and_unpack_rows_round_trip()
    {
        //Arrange
        byte[] tight = { 1, 2, 3, 4, 5, 6 };
        byte[] aligned = new byte[8 * 2];
        byte[] back = new byte[6];

        //Act
        TextureCopyLayout.PackRows(tight, 3, 2, aligned, 8);
        TextureCopyLayout.UnpackRows(aligned, 8, 2, back, 3);

        //Assert
        aligned.Should().Equal(1, 2, 3, 0, 0, 0, 0, 0, 4, 5, 6, 0, 0, 0, 0, 0);
        back.Should().Equal(tight);
    }

    [Fact]
    public void Indirect_argument_structs_have_the_gpu_layout()
    {
        //Arrange / Act / Assert
        Unsafe.SizeOf<DrawIndirectArgs>().Should().Be(DrawIndirectArgs.Size);
        Unsafe.SizeOf<DrawIndexedIndirectArgs>().Should().Be(DrawIndexedIndirectArgs.Size);
        Unsafe.SizeOf<DispatchIndirectArgs>().Should().Be(DispatchIndirectArgs.Size);
        new DrawIndexedIndirectArgs(6, 2, 1, -3, 4).BaseVertex.Should().Be(-3);
        new DispatchIndirectArgs(8).Y.Should().Be(1u);
        new DrawIndirectArgs(3).InstanceCount.Should().Be(1u);
    }

    [Fact]
    public void Pass_options_default_to_plain_passes()
    {
        //Arrange / Act
        RenderPassOptions render = default;
        ComputePassOptions compute = default;
        PassTimestamps none = default;

        //Assert
        render.Timestamps.IsEmpty.Should().BeTrue();
        render.OcclusionQuerySet.Should().BeNull();
        render.LoadStencil.Should().BeFalse();
        compute.Label.Should().BeNull();
        none.IsEmpty.Should().BeTrue();
        PassTimestamps.NotWritten.Should().Be(uint.MaxValue);
    }
}
