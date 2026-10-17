using System;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native.Interop;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// <see cref="FastMemory" />: byte-exact copies at every size class (empty, below and at the native threshold, a page,
/// a 12.7 MB read-back image), strided rows that leave the padding alone, neighbours of the copied range untouched,
/// overlapping ranges still copied correctly, and the C runtime lookup with its managed fallback.
/// </summary>
public unsafe class FastMemoryTests
{
    /// <summary>A 1328 x 2371 BGRA8 image with rows padded to 256 bytes - the read-back size the copy was measured at.</summary>
    private const int ReadbackBytes = 5376 * 2371;

    private static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        uint state = (uint)seed * 2654435761u + 1;
        for (int index = 0; index < length; index++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[index] = (byte)(state >> 24);
        }

        return bytes;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(FastMemory.NativeThreshold - 1)]
    [InlineData(FastMemory.NativeThreshold)]
    [InlineData(4096)]
    [InlineData(ReadbackBytes)]
    public void copy_is_byte_exact_and_leaves_the_neighbours_alone(int length)
    {
        //Arrange - 16 guard bytes before and after the destination range
        byte[] source = Pattern(length, length);
        byte[] destination = new byte[length + 32];
        Array.Fill(destination, (byte)0xCD);

        //Act
        fixed (byte* from = source)
        fixed (byte* to = destination)
        {
            FastMemory.Copy(from, to + 16, (nuint)length);
        }

        //Assert
        destination.AsSpan(16, length).SequenceEqual(source).Should().BeTrue();
        destination.AsSpan(0, 16).ToArray().Should().AllBeEquivalentTo((byte)0xCD);
        destination.AsSpan(16 + length, 16).ToArray().Should().AllBeEquivalentTo((byte)0xCD);
    }

    [Fact]
    public void copy_between_native_blocks_is_byte_exact()
    {
        //Arrange
        byte[] pattern = Pattern(ReadbackBytes, 3);
        byte* source = (byte*)NativeMemory.Alloc(ReadbackBytes);
        byte* destination = (byte*)NativeMemory.AllocZeroed(ReadbackBytes);
        try
        {
            pattern.CopyTo(new Span<byte>(source, ReadbackBytes));

            //Act
            FastMemory.Copy(source, destination, ReadbackBytes);

            //Assert
            new ReadOnlySpan<byte>(destination, ReadbackBytes).SequenceEqual(pattern).Should().BeTrue();
        }
        finally
        {
            NativeMemory.Free(source);
            NativeMemory.Free(destination);
        }
    }

    [Theory]
    [InlineData(4, 256, 4, 3)]
    [InlineData(5312, 5376, 5312, 2371)]
    [InlineData(20, 256, 32, 5)]
    [InlineData(300, 512, 300, 7)]
    public void copy_rows_drops_the_source_padding_and_keeps_the_destination_padding(int rowBytes, int sourceStride, int destinationStride, int rows)
    {
        //Arrange
        byte[] source = Pattern(sourceStride * rows, rowBytes + rows);
        byte[] destination = new byte[destinationStride * rows];
        Array.Fill(destination, (byte)0xEE);

        //Act
        fixed (byte* from = source)
        fixed (byte* to = destination)
        {
            FastMemory.CopyRows(from, (nuint)sourceStride, to, (nuint)destinationStride, (nuint)rowBytes, (nuint)rows);
        }

        //Assert
        for (int row = 0; row < rows; row++)
        {
            destination.AsSpan(row * destinationStride, rowBytes).SequenceEqual(source.AsSpan(row * sourceStride, rowBytes)).Should().BeTrue("row " + row);
            destination.AsSpan((row * destinationStride) + rowBytes, destinationStride - rowBytes).ToArray().Should().AllBeEquivalentTo((byte)0xEE);
        }
    }

    [Fact]
    public void copy_rows_with_tight_strides_copies_one_block()
    {
        //Arrange
        byte[] source = Pattern(4096 * 3, 11);
        byte[] destination = new byte[4096 * 3];

        //Act
        fixed (byte* from = source)
        fixed (byte* to = destination)
        {
            FastMemory.CopyRows(from, 4096, to, 4096, 4096, 3);
        }

        //Assert
        destination.AsSpan().SequenceEqual(source).Should().BeTrue();
    }

    [Fact]
    public void zero_rows_or_zero_row_bytes_copy_nothing()
    {
        //Arrange
        byte[] destination = new byte[64];
        Array.Fill(destination, (byte)0x11);
        byte[] source = new byte[64];

        //Act
        fixed (byte* from = source)
        fixed (byte* to = destination)
        {
            FastMemory.CopyRows(from, 32, to, 32, 32, 0);
            FastMemory.CopyRows(from, 32, to, 32, 0, 2);
            FastMemory.Copy(null, null, 0);
        }

        //Assert
        destination.Should().AllBeEquivalentTo((byte)0x11);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(4096)]
    [InlineData(-4096)]
    public void overlapping_ranges_are_still_copied_correctly(int shift)
    {
        //Arrange - a 64 KiB range moved by a shift that makes source and destination overlap
        const int Length = 65536;
        byte[] buffer = Pattern(Length + 8192, 5);
        byte[] expected = buffer.AsSpan(4096, Length).ToArray();

        //Act
        fixed (byte* start = buffer)
        {
            FastMemory.Copy(start + 4096, start + 4096 + shift, Length);
        }

        //Assert
        buffer.AsSpan(4096 + shift, Length).SequenceEqual(expected).Should().BeTrue();
    }

    [Fact]
    public void the_c_runtime_memcpy_is_found_on_linux()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The library names are verified on Linux here; other systems fall back to the managed copy when theirs is missing.");

        //Act
        string[] names = FastMemory.LibraryNames();

        //Assert
        names[0].Should().Be("libc.so.6");
        FastMemory.UsesNativeCopy.Should().BeTrue();
        FastMemory.NativeLibraryName.Should().Be("libc.so.6");
    }

    [Fact]
    public void resolve_returns_null_when_no_library_exports_memcpy()
    {
        //Arrange
        string[] names = { "codebrix-no-such-library-" + Guid.NewGuid().ToString("N"), string.Empty };

        //Act
        delegate* unmanaged[Cdecl]<void*, void*, nuint, void*> found = FastMemory.Resolve(names, out string library);

        //Assert
        (found == null).Should().BeTrue();
        library.Should().BeNull();
    }
}
