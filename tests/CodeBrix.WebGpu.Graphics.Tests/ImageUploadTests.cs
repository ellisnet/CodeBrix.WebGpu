using System.Collections.Generic;
using System.Linq;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats;
using CodeBrix.Imaging.Formats.Jxl;
using CodeBrix.Imaging.Formats.Png;
using CodeBrix.WebGpu.Graphics.Images;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The decoding configuration the image paths share (no GPU needed).</summary>
public class ImageUploadTests
{
    /// <summary>A format only this test registers, so the shared configuration's real formats are never touched.</summary>
    private sealed class TestOnlyFormat : IImageFormat
    {
        public string Name => "CodeBrixTestOnly";

        public string DefaultMimeType => "image/x-codebrix-test-only";

        public IEnumerable<string> MimeTypes => new[] { DefaultMimeType };

        public IEnumerable<string> FileExtensions => new[] { "cbtestonly" };

        public string DefaultFileExtension => "cbtestonly";
    }

    [Fact]
    public void decoding_configuration_owns_its_format_manager()
    {
        //Arrange
        Configuration decoding = ImageUpload.Decoding;

        //Act
        ImageFormatManager own = decoding.ImageFormatsManager;
        ImageFormatManager shared = Configuration.Default.ImageFormatsManager;

        //Assert
        ReferenceEquals(own, shared).Should().BeFalse();
        decoding.PreferContiguousImageBuffers.Should().BeTrue();
        own.FindDecoder(PngFormat.Instance).Should().BeSameAs(shared.FindDecoder(PngFormat.Instance));
    }

    [Fact]
    public void set_decoder_on_the_decoding_configuration_leaves_the_default_unchanged()
    {
        //Arrange
        TestOnlyFormat format = new TestOnlyFormat();
        JxlDecoder decoder = new JxlDecoder();
        ImageFormatManager own = ImageUpload.Decoding.ImageFormatsManager;

        //Act
        own.AddImageFormat(format);
        own.SetDecoder(format, decoder);

        //Assert
        own.FindDecoder(format).Should().BeSameAs(decoder);
        Configuration.Default.ImageFormats.Contains(format).Should().BeFalse();
        Configuration.Default.ImageFormatsManager.FindFormatByFileExtension("cbtestonly").Should().BeNull();
    }
}
