using System;
using System.Linq;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Textures: uploads in both byte orders, mipmaps, depth readback, wrapping.</summary>
public class GpuTextureTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public GpuTextureTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static byte[] Gradient(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * 4;
                rgba[index] = (byte)(x * 16);
                rgba[index + 1] = (byte)(y * 16);
                rgba[index + 2] = 200;
                rgba[index + 3] = 255;
            }
        }

        return rgba;
    }

    [Fact]
    public void CreateTextureFromRgba_round_trips_the_pixels()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] rgba = Gradient(8, 4);

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(8, 4, rgba);

        //Assert
        texture.Format.Should().Be(TextureFormat.RGBA8Unorm);
        texture.MipLevelCount.Should().Be(1);
        texture.ReadPixels().Should().Equal(rgba);
        texture.ReadRgba().Should().Equal(rgba);
    }

    [Fact]
    public void WriteRgba_on_a_bgra_texture_swaps_red_and_blue()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture texture = gpu.Context.CreateTexture2D(2, 1, TextureFormat.BGRA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc);

        //Act
        texture.WriteRgba(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 });
        byte[] raw = texture.ReadPixels();
        byte[] rgba = texture.ReadRgba();

        //Assert
        raw.Should().Equal(0, 0, 255, 255, 255, 0, 0, 255);
        rgba.Should().Equal(255, 0, 0, 255, 0, 0, 255, 255);
    }

    [Fact]
    public void Mipmapped_upload_generates_box_filtered_levels()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] checker = new byte[4 * 4 * 4];
        for (int index = 0; index < 16; index++)
        {
            byte value = (byte)((((index % 4) + (index / 4)) % 2) == 0 ? 255 : 0);
            checker[index * 4] = value;
            checker[(index * 4) + 1] = value;
            checker[(index * 4) + 2] = value;
            checker[(index * 4) + 3] = 255;
        }

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(4, 4, checker, mipmaps: true);
        byte[] level1 = new byte[2 * 2 * 4];
        texture.ReadPixels(level1, 1);
        byte[] level2 = new byte[4];
        texture.ReadPixels(level2, 2);

        //Assert
        texture.MipLevelCount.Should().Be(3);
        level1[0].Should().BeInRange((byte)127, (byte)128);
        level2[0].Should().BeInRange((byte)127, (byte)128);
        level2[3].Should().Be(255);
    }

    [Fact]
    public void FullMipChainLength_counts_down_to_one_texel()
    {
        //Arrange
        //Act
        int square = GpuTexture.FullMipChainLength(256, 256);
        int wide = GpuTexture.FullMipChainLength(300, 20);
        int single = GpuTexture.FullMipChainLength(1, 1);

        //Assert
        square.Should().Be(9);
        wide.Should().Be(9);
        single.Should().Be(1);
    }

    [Fact]
    public void Write_with_the_wrong_size_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(2, 2, new byte[16]);

        //Act
        Action act = () => texture.Write(new byte[12]);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Depth_texture_reads_back_the_clear_value()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4);

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.BeginRenderPass(target.View, Color4.Black, 0.25f).Dispose();
            frame.Submit();
        }

        float[] depth = target.ReadDepth();

        //Assert
        depth.Should().HaveCount(16);
        depth.Should().OnlyContain(value => Math.Abs(value - 0.25f) < 1e-6f);
    }

    [Fact]
    public void WrapTexture_shares_another_owners_texture_and_keeps_it_alive()
    {
        //Arrange
        gpu.RequireDevice();
        GpuTexture original = gpu.Context.CreateTextureFromRgba(1, 1, new byte[] { 1, 2, 3, 4 });

        //Act
        using GpuTexture wrapped = gpu.Context.WrapTexture(original.Handle, original.View, original.Format, 1, 1, "wrapped");
        original.Dispose();
        byte[] pixels = wrapped.ReadPixels();

        //Assert
        wrapped.IsWrapped.Should().BeTrue();
        (wrapped.Usage & TextureUsage.CopySrc).Should().Be(TextureUsage.CopySrc);
        pixels.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Depth24Plus_cannot_be_read_back()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture depth = gpu.Context.CreateDepthTexture(4, 4, TextureFormat.Depth24Plus);

        //Act
        Action act = () => depth.ReadPixels();

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Introspection_reports_the_native_texture_shape()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using GpuTexture texture = gpu.Context.CreateTexture2D(8, 4, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst, 2);

        //Assert
        texture.Dimension.Should().Be(TextureDimension.Dimension2D);
        texture.DepthOrArrayLayers.Should().Be(1);
        texture.SampleCount.Should().Be(1);
        texture.IsExternal.Should().BeFalse();
    }

    [Fact]
    public void WriteRegion_and_ReadRegion_round_trip_a_sub_rectangle()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture texture = gpu.Context.CreateTexture2D(6, 6, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc);
        texture.Write(new byte[6 * 6 * 4]);
        byte[] patch = Enumerable.Range(10, 2 * 3 * 4).Select(value => (byte)value).ToArray();
        TextureRegion region = new TextureRegion(3, 1, 2, 3);

        //Act
        texture.WriteRegion(region, patch);
        byte[] read = new byte[patch.Length];
        texture.ReadRegion(region, read);
        byte[] whole = texture.ReadRgba();

        //Assert
        read.Should().Equal(patch);
        TestPixels.At(whole, 6, 0, 0).Should().Be(((byte)0, (byte)0, (byte)0, (byte)0));
        TestPixels.At(whole, 6, 3, 1).Should().Be(((byte)10, (byte)11, (byte)12, (byte)13));
    }

    [Fact]
    public void CopyTo_copies_every_mip_level_on_the_gpu()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture source = gpu.Context.CreateTextureFromRgba(4, 4, Enumerable.Repeat((byte)90, 64).ToArray(), mipmaps: true);
        using GpuTexture destination = gpu.Context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc, source.MipLevelCount);

        //Act
        ulong submission = source.CopyTo(destination);
        byte[] level2 = new byte[4];
        destination.ReadPixels(level2, 2);

        //Assert
        submission.Should().BeGreaterThan(0UL);
        destination.ReadPixels().Should().OnlyContain(value => value == 90);
        level2.Should().Equal((byte)90, (byte)90, (byte)90, (byte)90);
    }

    [Fact]
    public void Destroy_frees_the_texture_and_disposes_the_wrapper()
    {
        //Arrange
        gpu.RequireDevice();
        GpuTexture texture = gpu.Context.CreateRenderTexture(4, 4);

        //Act
        texture.Destroy();
        Action act = () => texture.ReadPixels();

        //Assert
        texture.IsDisposed.Should().BeTrue();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Wrapped_texture_reads_its_shape_from_the_native_texture()
    {
        //Arrange
        gpu.RequireDevice();
        GpuTexture original = gpu.Context.CreateTextureFromRgba(2, 2, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });

        //Act
        using GpuTexture wrapped = gpu.Context.WrapTexture(original.Handle, "wrapped");
        original.Dispose();

        //Assert
        wrapped.IsWrapped.Should().BeTrue();
        wrapped.Width.Should().Be(2);
        wrapped.Height.Should().Be(2);
        wrapped.Format.Should().Be(TextureFormat.RGBA8Unorm);
        wrapped.ReadRgba()[15].Should().Be(16);
    }

    [Fact]
    public void Vulkan_texture_info_names_the_image()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipUnless(gpu.Context.Backend == BackendType.Vulkan, "The device is not on Vulkan.");
        using GpuTexture texture = gpu.Context.CreateRenderTexture(4, 4);

        //Act
        bool vulkan = texture.TryGetVulkanInfo(out VulkanTextureInfo info);
        bool d3d12 = texture.TryGetD3D12Info(out _);

        //Assert
        vulkan.Should().BeTrue();
        info.Image.Should().NotBe(IntPtr.Zero);
        info.Format.Should().Be(37u);
        info.SampleCount.Should().Be(1u);
        d3d12.Should().BeFalse();
        texture.MetalTexture.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void Texture_over_an_external_Vulkan_image_shares_its_pixels()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipUnless(gpu.Context.Backend == BackendType.Vulkan, "The device is not on Vulkan.");
        using GpuTexture owner = gpu.Context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc | TextureUsage.TextureBinding | TextureUsage.RenderAttachment);
        owner.TryGetVulkanInfo(out VulkanTextureInfo info).Should().BeTrue();

        // The engine tracks initialisation per texture: write and read the owner first so it is initialised (a read of
        // an uninitialised texture is zero-filled) and both wrappers end in the same copy-source state.
        owner.Write(new byte[64]);
        owner.ReadPixels();

        //Act
        GpuTexture external = gpu.Context.CreateTextureFromVulkanImage(info.Image, 4, 4, TextureFormat.RGBA8Unorm, owner.Usage, label: "external");
        external.Write(Enumerable.Repeat((byte)77, 64).ToArray());
        byte[] throughExternal = external.ReadPixels();
        external.Dispose();
        byte[] throughOwner = owner.ReadPixels();

        //Assert
        external.IsExternal.Should().BeTrue();
        throughExternal.Should().OnlyContain(value => value == 77);
        throughOwner.Should().OnlyContain(value => value == 77);
    }

    [Fact]
    public void External_image_on_the_wrong_backend_throws()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipUnless(gpu.Context.Backend == BackendType.Vulkan, "The device is not on Vulkan.");

        //Act
        Action metal = () => gpu.Context.CreateTextureFromMetalTexture(new IntPtr(0x1000), 4, 4, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding);
        Action d3d12 = () => gpu.Context.CreateTextureFromD3D12Resource(new IntPtr(0x1000), 4, 4, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding);
        Action none = () => gpu.Context.CreateTextureFromVulkanImage(IntPtr.Zero, 4, 4, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding);

        //Assert
        metal.Should().Throw<GraphicsException>();
        d3d12.Should().Throw<GraphicsException>();
        none.Should().Throw<ArgumentException>();
    }
}
