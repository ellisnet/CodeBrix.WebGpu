using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

public unsafe class WgpuStringTests
{
    [Fact]
    public void ToManaged_returns_null_for_the_null_view()
        => WgpuString.ToManaged(WgpuString.Null).Should().BeNull();

    [Fact]
    public void ToManaged_returns_empty_for_the_empty_view()
        => WgpuString.ToManaged(WgpuString.Empty).Should().BeEmpty();

    [Fact]
    public void ToManaged_reads_an_explicit_length()
    {
        //Arrange
        byte[] bytes = "héllo world"u8.ToArray();

        //Act
        string text;
        fixed (byte* data = bytes) text = WgpuString.ToManaged(WgpuString.FromPinned(data, 6));

        //Assert
        text.Should().Be("héllo");
    }

    [Fact]
    public void ToManaged_reads_a_null_terminated_view()
    {
        //Arrange
        byte[] bytes = "abc\0def"u8.ToArray();

        //Act
        string text;
        fixed (byte* data = bytes) text = WgpuString.ToManaged(new StringView { Data = data, Length = Constants.Strlen });

        //Assert
        text.Should().Be("abc");
    }

    [Fact]
    public void IsNull_distinguishes_null_from_empty()
    {
        //Assert
        WgpuString.IsNull(WgpuString.Null).Should().BeTrue();
        WgpuString.IsNull(WgpuString.Empty).Should().BeFalse();
    }

    [Fact]
    public void FromPinned_rejects_a_negative_length()
    {
        //Act
        Action act = () => WgpuString.FromPinned(null, -1);

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void WgpuUtf8String_round_trips_text()
    {
        //Arrange
        using WgpuUtf8String text = new WgpuUtf8String("Grüße");

        //Act
        string back = WgpuString.ToManaged(text.View);

        //Assert
        back.Should().Be("Grüße");
        text.ByteLength.Should().Be(7);
        text.View.Data[7].Should().Be((byte)0);
    }

    [Fact]
    public void WgpuUtf8String_of_null_gives_the_null_view()
    {
        //Arrange
        using WgpuUtf8String text = new WgpuUtf8String(null);

        //Act
        StringView view = text.View;

        //Assert
        WgpuString.IsNull(view).Should().BeTrue();
        text.ByteLength.Should().Be(-1);
    }

    [Fact]
    public void WgpuUtf8String_view_throws_after_dispose()
    {
        //Arrange
        WgpuUtf8String text = new WgpuUtf8String("x");
        text.Dispose();

        //Act
        Action act = () => _ = text.View;

        //Assert
        act.Should().Throw<ObjectDisposedException>();
    }
}
