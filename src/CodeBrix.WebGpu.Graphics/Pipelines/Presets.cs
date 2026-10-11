using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How a colour target blends a fragment with what is already there.</summary>
public enum BlendPreset
{
    /// <summary>No blending: the fragment replaces the pixel.</summary>
    Opaque,

    /// <summary>Straight (non-premultiplied) alpha: <c>src * a + dst * (1 - a)</c>.</summary>
    AlphaBlend,

    /// <summary>Premultiplied alpha: <c>src + dst * (1 - a)</c>.</summary>
    PremultipliedAlpha,

    /// <summary>Additive: <c>src * a + dst</c> (glows, particles).</summary>
    Additive,

    /// <summary>Multiply: <c>src * dst</c>.</summary>
    Multiply,
}

/// <summary>How a pipeline tests and writes depth.</summary>
public enum DepthPreset
{
    /// <summary>No depth test; the pipeline still declares the pass's depth format (compare Always, no writes).</summary>
    None,

    /// <summary>Nearer fragments win (<see cref="CompareFunction.Less" />) and write depth - the usual 3D setting.</summary>
    ReadWrite,

    /// <summary>Like <see cref="ReadWrite" /> with <see cref="CompareFunction.LessEqual" /> (for re-drawing the same geometry).</summary>
    ReadWriteLessEqual,

    /// <summary>Test against depth (<see cref="CompareFunction.LessEqual" />) without writing (transparent geometry after opaque).</summary>
    ReadOnly,
}

/// <summary>Which triangles a pipeline discards.</summary>
public enum CullPreset
{
    /// <summary>Draw both faces.</summary>
    None,

    /// <summary>Discard back faces (counter-clockwise triangles are front faces) - the usual setting for closed meshes.</summary>
    Back,

    /// <summary>Discard front faces.</summary>
    Front,
}

/// <summary>The native state behind the presets.</summary>
internal static class Presets
{
    public static BlendState? Blend(BlendPreset preset) => preset switch
    {
        BlendPreset.AlphaBlend => new BlendState
        {
            Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
            Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
        },
        BlendPreset.PremultipliedAlpha => new BlendState
        {
            Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
            Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha },
        },
        BlendPreset.Additive => new BlendState
        {
            Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.One },
            Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.One },
        },
        BlendPreset.Multiply => new BlendState
        {
            Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.Dst, DstFactor = BlendFactor.Zero },
            Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.DstAlpha, DstFactor = BlendFactor.Zero },
        },
        _ => null,
    };

    public static CullMode Cull(CullPreset preset) => preset switch
    {
        CullPreset.Back => CullMode.Back,
        CullPreset.Front => CullMode.Front,
        _ => CullMode.None,
    };

    public static void Depth(DepthPreset preset, out CompareFunction compare, out bool write)
    {
        switch (preset)
        {
            case DepthPreset.ReadWrite:
                compare = CompareFunction.Less;
                write = true;
                break;
            case DepthPreset.ReadWriteLessEqual:
                compare = CompareFunction.LessEqual;
                write = true;
                break;
            case DepthPreset.ReadOnly:
                compare = CompareFunction.LessEqual;
                write = false;
                break;
            default:
                compare = CompareFunction.Always;
                write = false;
                break;
        }
    }
}
