using System;
using System.IO;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats.Jxl;
using CodeBrix.Imaging.Formats.Png;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Images decoded by CodeBrix.Imaging into textures, GPU mipmaps, readback and saving (images generated in-test).</summary>
public class ImageTextureTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public ImageTextureTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static Image<Rgba32> Checker(int size)
    {
        Image<Rgba32> image = new Image<Rgba32>(size, size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++) image[x, y] = new Rgba32((byte)(x * 30), (byte)(y * 30), (byte)((x + y) * 10), 255);
        }

        return image;
    }

    private static MemoryStream Png(Image image)
    {
        MemoryStream stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void png_stream_becomes_a_linear_texture_with_the_same_pixels()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> source = Checker(5);
        using MemoryStream png = Png(source);

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromStream(png, new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear, Mipmaps = false });
        using Image<Rgba32> back = texture.ToImage();

        //Assert
        texture.Format.Should().Be(TextureFormat.RGBA8Unorm);
        texture.MipLevelCount.Should().Be(1);
        back.Width.Should().Be(5);
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 5; x++) back[x, y].Should().Be(source[x, y]);
        }
    }

    [Fact]
    public void srgb_mipmaps_are_generated_on_the_gpu_in_linear_light()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> halves = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++) halves[x, y] = x < 4 ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 255);
        }

        //Act
        using GpuTexture srgb = gpu.Context.CreateTextureFromImage(halves);
        using GpuTexture linear = gpu.Context.CreateTextureFromImage(halves, TextureLoadOptions.Data);
        using Image<Rgba32> srgbTop = srgb.ToImage(3);
        using Image<Rgba32> linearTop = linear.ToImage(3);
        using Image<Rgba32> srgbLevel1 = srgb.ToImage(1);

        //Assert
        srgb.Format.Should().Be(TextureFormat.RGBA8UnormSrgb);
        srgb.MipLevelCount.Should().Be(4);
        (srgb.Usage & TextureUsage.RenderAttachment).Should().Be(TextureUsage.RenderAttachment);
        srgbTop.Width.Should().Be(1);
        ((int)linearTop[0, 0].R).Should().BeInRange(124, 131);
        ((int)srgbTop[0, 0].R).Should().BeInRange(184, 192);
        srgbLevel1[0, 0].R.Should().Be(255);
        srgbLevel1[3, 0].R.Should().Be(0);
        gpu.Context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void premultiplied_alpha_scales_colour_by_alpha_and_leaves_the_image_alone()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(200, 100, 50, 128);
        image[1, 0] = new Rgba32(10, 20, 30, 255);

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromImage(image, new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear, Mipmaps = false, PremultiplyAlpha = true });
        using Image<Rgba32> back = texture.ToImage();

        //Assert
        back[0, 0].Should().Be(new Rgba32(100, 50, 25, 128));
        back[1, 0].Should().Be(new Rgba32(10, 20, 30, 255));
        image[0, 0].Should().Be(new Rgba32(200, 100, 50, 128));
    }

    [Fact]
    public void other_pixel_types_are_converted_and_files_load_and_save_by_extension()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgb24> rgb = new Image<Rgb24>(3, 2, new Rgb24(10, 200, 30));
        string folder = Directory.CreateTempSubdirectory("webgpu-images-").FullName;
        try
        {
            //Act
            using GpuTexture fromRgb = gpu.Context.CreateTextureFromImage(rgb, new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear });
            string png = Path.Combine(folder, "saved.png");
            string bmp = Path.Combine(folder, "saved.bmp");
            fromRgb.SaveAsPng(png);
            fromRgb.SaveAs(bmp);
            using GpuTexture loaded = gpu.Context.CreateTextureFromFile(png, new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear, Mipmaps = false });
            using Image<Rgba32> bmpBack = Image.Load<Rgba32>(bmp);
            using MemoryStream stream = new MemoryStream();
            loaded.SaveAs(stream, PngFormat.Instance);

            //Assert
            loaded.Label.Should().Be("saved.png");
            using Image<Rgba32> back = loaded.ToImage();
            back[2, 1].Should().Be(new Rgba32(10, 200, 30, 255));
            bmpBack[0, 0].Should().Be(new Rgba32(10, 200, 30, 255));
            stream.Length.Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void undecodable_bytes_and_missing_files_are_reported_clearly()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] garbage = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        //Act
        Action bytes = () => gpu.Context.CreateTextureFromEncoded(garbage);
        Action missing = () => gpu.Context.CreateTextureFromFile("/nonexistent/folder/none.png");

        //Assert
        bytes.Should().Throw<InvalidDataException>().WithMessage("*could not be decoded*");
        missing.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void texture_arrays_have_layer_views_that_read_and_write_their_own_layer()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture array = gpu.Context.CreateTextureArray(2, 2, 3, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc, 1, "layers");
        using GpuTexture layer1 = array.CreateLayerView(1);
        using GpuTexture layer2 = array.CreateLayerView(2);

        //Act
        layer1.WriteRgba(new byte[] { 9, 9, 9, 255, 9, 9, 9, 255, 9, 9, 9, 255, 9, 9, 9, 255 });
        layer2.WriteRgba(new byte[] { 7, 7, 7, 255, 7, 7, 7, 255, 7, 7, 7, 255, 7, 7, 7, 255 });
        using Image<Rgba32> one = layer1.ToImage();
        using Image<Rgba32> two = array.ToImage(0, 2);

        //Assert
        array.DepthOrArrayLayers.Should().Be(3);
        layer1.ViewLayer.Should().Be(1);
        layer1.IsWrapped.Should().BeTrue();
        one[1, 1].R.Should().Be(9);
        two[0, 0].R.Should().Be(7);
        layer1.ReadRgba()[0].Should().Be(9);
        Action outOfRange = () => array.CreateLayerView(3);
        outOfRange.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void generate_mipmaps_refuses_textures_it_cannot_render_into()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture single = gpu.Context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.RenderAttachment);
        using GpuTexture noRender = gpu.Context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst, 3);

        //Act
        Action one = () => gpu.Context.GenerateMipmaps(single);
        Action two = () => gpu.Context.GenerateMipmaps(noRender);

        //Assert
        one.Should().Throw<ArgumentException>().WithMessage("*one mip level*");
        two.Should().Throw<ArgumentException>().WithMessage("*RenderAttachment*");
    }

    [Fact]
    public void headless_target_saves_what_was_rendered()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, TextureFormat.RGBA8Unorm, false);
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.FromBytes(10, 20, 30))) { }
            frame.Submit();
        }

        string file = Path.Combine(Path.GetTempPath(), "webgpu-headless-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            //Act
            target.SaveAsPng(file);
            using Image<Rgba32> image = target.ToImage();
            using Image<Rgba32> loaded = Image.Load<Rgba32>(file);

            //Assert
            image[3, 3].Should().Be(new Rgba32(10, 20, 30, 255));
            loaded[0, 0].Should().Be(new Rgba32(10, 20, 30, 255));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void lossless_jpeg_xl_loads_from_bytes_streams_and_files_with_exact_pixels()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> source = Checker(5);
        source[1, 1] = new Rgba32(10, 20, 30, 0);
        source[3, 2] = new Rgba32(200, 100, 50, 128);
        byte[] jxl = TestJxl.Encode(source);
        TextureLoadOptions linear = new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear, Mipmaps = false };
        string folder = Directory.CreateTempSubdirectory("webgpu-jxl-").FullName;
        try
        {
            string path = Path.Combine(folder, "checker.jxl");
            File.WriteAllBytes(path, jxl);

            //Act
            using GpuTexture fromBytes = gpu.Context.CreateTextureFromEncoded(jxl, linear);
            using GpuTexture fromStream = gpu.Context.CreateTextureFromStream(new MemoryStream(jxl), linear);
            using GpuTexture fromForwardOnly = gpu.Context.CreateTextureFromStream(new TestJxl.ForwardOnlyStream(new MemoryStream(jxl)), linear);
            using GpuTexture fromFile = gpu.Context.CreateTextureFromFile(path);

            //Assert
            fromFile.Label.Should().Be("checker.jxl");
            fromFile.Format.Should().Be(TextureFormat.RGBA8UnormSrgb);
            fromFile.MipLevelCount.Should().Be(3);
            foreach (GpuTexture texture in new[] { fromBytes, fromStream, fromForwardOnly })
            {
                using Image<Rgba32> back = texture.ToImage();
                for (int y = 0; y < 5; y++)
                {
                    for (int x = 0; x < 5; x++) back[x, y].Should().Be(source[x, y]);
                }
            }

            using Image<Rgba32> fileBack = fromFile.ToImage();
            fileBack[3, 2].Should().Be(source[3, 2]);
            gpu.Context.TakeErrors().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void linear_light_and_float_jpeg_xl_files_are_converted_to_srgb_for_textures()
    {
        //Arrange - linear 0.18 is sRGB 0.4614 = 118; an HDR value above 1 clips to 255 (no PQ/HLG curve to tone map)
        gpu.RequireDevice();
        byte[] grey = TestJxl.LinearFloat(4, 2, 0.18f, 0.18f, 4f);
        TextureLoadOptions linear = new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear, Mipmaps = false };

        //Act
        using GpuTexture texture = gpu.Context.CreateTextureFromEncoded(grey, linear);
        using Image<Rgba32> back = texture.ToImage();

        //Assert
        ((int)back[0, 0].R).Should().BeInRange(117, 119);
        ((int)back[3, 1].G).Should().BeInRange(117, 119);
        back[2, 1].B.Should().Be((byte)255);
        back[2, 1].A.Should().Be((byte)255);
    }

    [Fact]
    public void textures_save_as_jpeg_xl_by_extension_and_losslessly_through_the_image()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> source = Checker(6);
        using GpuTexture texture = gpu.Context.CreateTextureFromImage(source, new TextureLoadOptions { ColorSpace = TextureColorSpace.Linear, Mipmaps = false });
        string folder = Directory.CreateTempSubdirectory("webgpu-jxl-save-").FullName;
        try
        {
            string lossy = Path.Combine(folder, "lossy.jxl");
            string exact = Path.Combine(folder, "exact.jxl");

            //Act
            texture.SaveAs(lossy);
            using (Image<Rgba32> image = texture.ToImage()) image.SaveAsJxl(exact, new JxlEncoder { Lossless = true, Effort = 3 });
            using Image<Rgba32> lossyBack = Image.Load<Rgba32>(lossy);
            using Image<Rgba32> exactBack = Image.Load<Rgba32>(exact);

            //Assert
            lossyBack.Format.Should().BeSameAs(JxlFormat.Instance);
            lossyBack.Width.Should().Be(6);
            lossyBack.Height.Should().Be(6);
            for (int y = 0; y < 6; y++)
            {
                for (int x = 0; x < 6; x++) exactBack[x, y].Should().Be(source[x, y]);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
