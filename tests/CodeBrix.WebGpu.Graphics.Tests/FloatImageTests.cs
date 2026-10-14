using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The linear float image used for HDR panoramas.</summary>
public class FloatImageTests
{
    [Fact]
    public void pixel_buffer_length_must_match_the_dimensions()
    {
        //Act
        Action act = () => new FloatImage(2, 2, new float[5]);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void get_and_set_pixel_address_rgb_triplets_row_major()
    {
        //Arrange
        FloatImage image = new FloatImage(2, 2);

        //Act
        image.SetPixel(1, 1, 1f, 2f, 3f);
        image.GetPixel(1, 1, out float r, out float g, out float b);

        //Assert
        (r, g, b).Should().Be((1f, 2f, 3f));
        image.Pixels[9].Should().Be(1f);
    }

    [Fact]
    public void from_rgba_drops_the_alpha_channel()
    {
        //Act
        FloatImage image = FloatImage.FromRgba(new float[] { 1f, 2f, 3f, 0.5f, 4f, 5f, 6f, 0.25f }, 2, 1);

        //Assert
        image.Pixels.Should().Equal(1f, 2f, 3f, 4f, 5f, 6f);
    }
}
