using System;
using System.Globalization;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>A straight-alpha RGBA colour with float channels in 0..1.</summary>
public readonly struct Color4 : IEquatable<Color4>
{
    /// <summary>Creates a colour.</summary>
    /// <param name="r">Red, 0..1.</param>
    /// <param name="g">Green, 0..1.</param>
    /// <param name="b">Blue, 0..1.</param>
    /// <param name="a">Alpha, 0..1 (default 1).</param>
    public Color4(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Red, 0..1.</summary>
    public float R { get; }

    /// <summary>Green, 0..1.</summary>
    public float G { get; }

    /// <summary>Blue, 0..1.</summary>
    public float B { get; }

    /// <summary>Alpha, 0..1.</summary>
    public float A { get; }

    /// <summary>Opaque white.</summary>
    public static Color4 White => new Color4(1, 1, 1);

    /// <summary>Opaque black.</summary>
    public static Color4 Black => new Color4(0, 0, 0);

    /// <summary>Fully transparent black.</summary>
    public static Color4 Transparent => new Color4(0, 0, 0, 0);

    /// <summary>Opaque red.</summary>
    public static Color4 Red => new Color4(1, 0, 0);

    /// <summary>Opaque green.</summary>
    public static Color4 Green => new Color4(0, 1, 0);

    /// <summary>Opaque blue.</summary>
    public static Color4 Blue => new Color4(0, 0, 1);

    /// <summary>Opaque yellow.</summary>
    public static Color4 Yellow => new Color4(1, 1, 0);

    /// <summary>Opaque cyan.</summary>
    public static Color4 Cyan => new Color4(0, 1, 1);

    /// <summary>Opaque magenta.</summary>
    public static Color4 Magenta => new Color4(1, 0, 1);

    /// <summary>Opaque mid grey.</summary>
    public static Color4 Gray => new Color4(0.5f, 0.5f, 0.5f);

    /// <summary>Opaque cornflower blue.</summary>
    public static Color4 CornflowerBlue => FromBytes(100, 149, 237);

    /// <summary>A colour from 8-bit channels.</summary>
    /// <param name="r">Red, 0..255.</param>
    /// <param name="g">Green, 0..255.</param>
    /// <param name="b">Blue, 0..255.</param>
    /// <param name="a">Alpha, 0..255 (default 255).</param>
    /// <returns>The colour.</returns>
    public static Color4 FromBytes(byte r, byte g, byte b, byte a = 255) => new Color4(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>A colour from <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (the <c>#</c> is optional).</summary>
    /// <param name="hex">The text.</param>
    /// <returns>The colour.</returns>
    /// <exception cref="FormatException">The text is not 6 or 8 hexadecimal digits.</exception>
    public static Color4 FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        string digits = hex.StartsWith('#') ? hex.Substring(1) : hex;
        if ((digits.Length != 6 && digits.Length != 8) || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)) throw new FormatException("'" + hex + "' is not #RRGGBB or #RRGGBBAA.");
        if (digits.Length == 6) value = (value << 8) | 0xFF;
        return FromBytes((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>The same colour with another alpha.</summary>
    /// <param name="alpha">The alpha, 0..1.</param>
    /// <returns>The colour.</returns>
    public Color4 WithAlpha(float alpha) => new Color4(R, G, B, alpha);

    /// <summary>Linear interpolation between two colours.</summary>
    /// <param name="from">At 0.</param>
    /// <param name="to">At 1.</param>
    /// <param name="amount">0..1.</param>
    /// <returns>The colour.</returns>
    public static Color4 Lerp(Color4 from, Color4 to, float amount) =>
        new Color4(from.R + ((to.R - from.R) * amount), from.G + ((to.G - from.G) * amount), from.B + ((to.B - from.B) * amount), from.A + ((to.A - from.A) * amount));

    /// <summary>The channels as a vector (x = red ... w = alpha).</summary>
    /// <returns>The vector.</returns>
    public Vector4 ToVector4() => new Vector4(R, G, B, A);

    /// <summary>The colour packed into 32 bits with red in the lowest byte (memory order R, G, B, A - <c>Unorm8x4</c>).</summary>
    /// <returns>The packed value.</returns>
    public uint ToPackedRgba() =>
        ToByte(R) | ((uint)ToByte(G) << 8) | ((uint)ToByte(B) << 16) | ((uint)ToByte(A) << 24);

    /// <summary>Compares the channels.</summary>
    /// <param name="other">The other colour.</param>
    /// <returns>True when all channels are equal.</returns>
    public bool Equals(Color4 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    /// <summary>Compares the channels.</summary>
    /// <param name="obj">The other object.</param>
    /// <returns>True when <paramref name="obj" /> is an equal <see cref="Color4" />.</returns>
    public override bool Equals(object obj) => obj is Color4 other && Equals(other);

    /// <summary>The hash of the channels.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    /// <summary>The channels.</summary>
    /// <returns>For example <c>(1, 0.5, 0, 1)</c>.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"({R}, {G}, {B}, {A})");

    /// <summary>Compares the channels.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>True when equal.</returns>
    public static bool operator ==(Color4 left, Color4 right) => left.Equals(right);

    /// <summary>Compares the channels.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>True when different.</returns>
    public static bool operator !=(Color4 left, Color4 right) => !left.Equals(right);

    /// <summary>Multiplies channel by channel.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>The product.</returns>
    public static Color4 operator *(Color4 left, Color4 right) => new Color4(left.R * right.R, left.G * right.G, left.B * right.B, left.A * right.A);

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
