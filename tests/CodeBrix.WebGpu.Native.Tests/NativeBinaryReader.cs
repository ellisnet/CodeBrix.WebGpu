using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// Small readers for the identity a dynamic loader matches an already-loaded library by: the ELF <c>DT_SONAME</c>
/// (Linux, Android) and the Mach-O <c>LC_ID_DYLIB</c> install name (macOS). Only what the package tests need: 64-bit
/// little-endian ELF shared objects and 64-bit Mach-O images (thin or inside a universal binary).
/// </summary>
internal static class NativeBinaryReader
{
    private const uint PtLoad = 1;
    private const uint PtDynamic = 2;
    private const long DtNull = 0;
    private const long DtStrtab = 5;
    private const long DtSoname = 14;
    private const uint MachOMagic64 = 0xFEEDFACF;
    private const uint FatMagic = 0xCAFEBABE;
    private const uint LcIdDylib = 0xD;

    /// <summary>The <c>DT_SONAME</c> of a 64-bit little-endian ELF shared object, or null when it has none.</summary>
    /// <param name="image">The file bytes.</param>
    /// <returns>The SONAME.</returns>
    /// <exception cref="FormatException">The file is not a 64-bit little-endian ELF image.</exception>
    public static string ElfSoname(byte[] image)
    {
        ReadOnlySpan<byte> data = image;
        if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F') throw new FormatException("not an ELF file");
        if (data[4] != 2 || data[5] != 1) throw new FormatException("not a 64-bit little-endian ELF file");

        ulong programHeaders = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(0x20));
        ushort entrySize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x36));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x38));

        List<(ulong VirtualAddress, ulong Offset, ulong FileSize)> loads = new List<(ulong, ulong, ulong)>();
        ulong dynamicOffset = 0;
        ulong dynamicSize = 0;
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> header = data.Slice(checked((int)programHeaders + (index * entrySize)), entrySize);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(header);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(8));
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16));
            ulong fileSize = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32));
            if (type == PtLoad) loads.Add((address, offset, fileSize));
            else if (type == PtDynamic)
            {
                dynamicOffset = offset;
                dynamicSize = fileSize;
            }
        }

        if (dynamicSize == 0) throw new FormatException("no PT_DYNAMIC segment");

        ulong stringTable = 0;
        ulong soname = ulong.MaxValue;
        for (ulong position = dynamicOffset; position + 16 <= dynamicOffset + dynamicSize; position += 16)
        {
            long tag = BinaryPrimitives.ReadInt64LittleEndian(data.Slice((int)position));
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice((int)position + 8));
            if (tag == DtNull) break;
            if (tag == DtStrtab) stringTable = value;
            else if (tag == DtSoname) soname = value;
        }

        if (soname == ulong.MaxValue) return null;
        foreach ((ulong address, ulong offset, ulong fileSize) in loads)
        {
            if (stringTable < address || stringTable >= address + fileSize) continue;
            return CString(data, checked((int)(offset + (stringTable - address) + soname)));
        }

        throw new FormatException("DT_STRTAB is outside every PT_LOAD segment");
    }

    /// <summary>The <c>LC_ID_DYLIB</c> install name of every 64-bit Mach-O image in the file (one per architecture).</summary>
    /// <param name="image">The file bytes.</param>
    /// <returns>The install names (null for an image without one).</returns>
    /// <exception cref="FormatException">The file is not a 64-bit Mach-O or universal binary.</exception>
    public static List<string> MachOInstallNames(byte[] image)
    {
        List<string> names = new List<string>();
        ReadOnlySpan<byte> data = image;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) == FatMagic)
        {
            uint architectures = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
            for (int index = 0; index < architectures; index++)
            {
                int offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8 + (index * 20) + 8)));
                names.Add(ThinInstallName(data.Slice(offset)));
            }

            return names;
        }

        names.Add(ThinInstallName(data));
        return names;
    }

    private static string ThinInstallName(ReadOnlySpan<byte> data)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != MachOMagic64) throw new FormatException("not a 64-bit little-endian Mach-O image");
        uint commands = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16));
        int position = 32;
        for (int index = 0; index < commands; index++)
        {
            uint command = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position));
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4)));
            if (command == LcIdDylib)
            {
                int nameOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 8)));
                return CString(data, position + nameOffset);
            }

            position += size;
        }

        return null;
    }

    private static string CString(ReadOnlySpan<byte> data, int start)
    {
        int end = data.Slice(start).IndexOf((byte)0);
        if (end < 0) throw new FormatException("unterminated string");
        return Encoding.UTF8.GetString(data.Slice(start, end));
    }
}
