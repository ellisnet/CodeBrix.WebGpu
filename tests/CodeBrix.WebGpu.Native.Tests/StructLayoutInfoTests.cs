using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

public class StructLayoutInfoTests
{
    [Fact]
    public void Of_measures_a_pointer_aligned_struct()
        => StructLayoutInfo.Of<StringView>("WGPUStringView").Should().Be(new StructLayoutInfo("WGPUStringView", IntPtr.Size * 2, IntPtr.Size));

    [Fact]
    public void Of_measures_a_four_byte_aligned_struct()
        => StructLayoutInfo.Of<Extent3D>("WGPUExtent3D").Should().Be(new StructLayoutInfo("WGPUExtent3D", 12, 4));

    [Fact]
    public void Of_measures_a_double_aligned_struct()
        => StructLayoutInfo.Of<Color>("WGPUColor").Should().Be(new StructLayoutInfo("WGPUColor", 32, 8));

    [Fact]
    public void StructLayoutCheck_matches_only_equal_native_layouts()
    {
        //Arrange
        StructLayoutInfo managed = new StructLayoutInfo("X", 16, 8);

        //Assert
        new StructLayoutCheck(managed, managed).Matches.Should().BeTrue();
        new StructLayoutCheck(managed, new StructLayoutInfo("X", 16, 4)).Matches.Should().BeFalse();
        new StructLayoutCheck(managed, null).Matches.Should().BeFalse();
    }
}
