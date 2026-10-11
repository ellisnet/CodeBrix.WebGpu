using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Colours (no GPU).</summary>
public class Color4Tests
{
    [Fact]
    public void FromHex_and_FromBytes_agree_and_pack_red_first()
    {
        //Arrange
        //Act
        Color4 hex = Color4.FromHex("#FF800040");
        Color4 bytes = Color4.FromBytes(255, 128, 0, 64);

        //Assert
        hex.Should().Be(bytes);
        hex.ToPackedRgba().Should().Be(0x400080FFu);
        Color4.FromHex("00FF00").A.Should().Be(1f);
    }

    [Fact]
    public void FromHex_rejects_bad_text()
    {
        //Arrange
        //Act
        Action act = () => Color4.FromHex("#12345");

        //Assert
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Lerp_WithAlpha_and_multiply_work_per_channel()
    {
        //Arrange
        //Act
        Color4 mid = Color4.Lerp(Color4.Black, Color4.White, 0.5f);
        Color4 faded = Color4.Red.WithAlpha(0.25f);
        Color4 product = new Color4(0.5f, 1, 1) * new Color4(1, 0.5f, 0);

        //Assert
        mid.Should().Be(new Color4(0.5f, 0.5f, 0.5f));
        faded.A.Should().Be(0.25f);
        product.Should().Be(new Color4(0.5f, 0.5f, 0));
    }
}
