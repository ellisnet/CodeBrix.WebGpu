using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>The generated handle wrappers, through <see cref="Device" /> (every handle is generated from one template).</summary>
public class DeviceTests
{
    [Fact]
    public void Null_is_null()
    {
        //Assert
        Device.Null.IsNull.Should().BeTrue();
        default(Device).Should().Be(Device.Null);
    }

    [Fact]
    public void Equals_compares_the_native_pointer()
    {
        //Arrange
        Device first = new Device((IntPtr)0x1000);
        Device same = new Device((IntPtr)0x1000);
        Device other = new Device((IntPtr)0x2000);

        //Assert
        first.Equals(same).Should().BeTrue();
        (first == same).Should().BeTrue();
        (first != other).Should().BeTrue();
        first.Equals((object)same).Should().BeTrue();
        first.GetHashCode().Should().Be(same.GetHashCode());
    }

    [Fact]
    public void ToString_shows_the_type_and_pointer()
        => new Device((IntPtr)0xABC).ToString().Should().Be("Device(0xABC)");

    [Fact]
    public unsafe void handles_are_pointer_sized()
        => sizeof(Device).Should().Be(IntPtr.Size);
}
