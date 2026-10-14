using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How a <see cref="LitMaterial" /> treats alpha (the glTF alpha modes, plus the renderer's original rule).</summary>
public enum MaterialAlphaMode
{
    /// <summary>The original rule: alpha-blended when <see cref="LitMaterial.BaseColor" /> alpha is below 1, otherwise opaque with the alpha written as computed.</summary>
    Auto,

    /// <summary>Opaque: alpha is ignored and written as 1.</summary>
    Opaque,

    /// <summary>Alpha-tested: fragments below <see cref="LitMaterial.AlphaCutoff" /> are discarded, the rest are opaque.</summary>
    Mask,

    /// <summary>Alpha-blended (straight alpha), drawn after the opaque draws with depth writes off.</summary>
    Blend,
}

/// <summary>
/// The surface of a mesh drawn by a <see cref="MeshRenderer" />: a base colour, an optional base-colour texture,
/// Blinn-Phong specular settings, whether both faces are drawn, an alpha mode and a texture-coordinate transform. Create one with
/// <see cref="MeshRenderer.CreateMaterial(Color4, GpuTexture, bool, string, GpuSampler)" />. Property changes reach the GPU at the next
/// draw that uses the material; within one frame every draw sees the material's last values.
/// </summary>
public sealed class LitMaterial : GraphicsResource
{
    private readonly GpuBuffer uniforms;
    private Color4 baseColor;
    private float shininess = 32f;
    private float specularStrength = 0.25f;
    private bool unlit;
    private MaterialAlphaMode alphaMode;
    private float alphaCutoff = 0.5f;
    private Matrix3x2 textureTransform = Matrix3x2.Identity;
    private bool dirty = true;

    internal LitMaterial(MeshRenderer renderer, Color4 baseColor, GpuTexture texture, bool doubleSided, string label, GpuSampler sampler)
        : base(renderer.Context, label)
    {
        Renderer = renderer;
        this.baseColor = baseColor;
        BaseColorTexture = texture;
        DoubleSided = doubleSided;
        uniforms = Context.CreateUniformBuffer(UniformSize, label == null ? "material" : label + " uniforms");
        Sampler = sampler;
        BindGroup = renderer.CreateMaterialGroup(uniforms, texture, sampler);
    }

    /// <summary>The renderer that created it.</summary>
    public MeshRenderer Renderer { get; }

    /// <summary>The base-colour texture, or null.</summary>
    public GpuTexture BaseColorTexture { get; }

    /// <summary>The sampler given at creation, or null (the renderer's linear repeating sampler).</summary>
    public GpuSampler Sampler { get; }

    /// <summary>True to draw back faces too (with flipped normals); false culls them.</summary>
    public bool DoubleSided { get; }

    /// <summary>The base colour (multiplies the texture and the vertex colour); alpha below 1 blends.</summary>
    public Color4 BaseColor
    {
        get => baseColor;
        set
        {
            baseColor = value;
            dirty = true;
        }
    }

    /// <summary>The specular exponent. Default 32.</summary>
    public float Shininess
    {
        get => shininess;
        set
        {
            shininess = value;
            dirty = true;
        }
    }

    /// <summary>The specular strength, 0 for matte. Default 0.25.</summary>
    public float SpecularStrength
    {
        get => specularStrength;
        set
        {
            specularStrength = value;
            dirty = true;
        }
    }

    /// <summary>True to ignore lighting (the colour is shown as is). Default false.</summary>
    public bool Unlit
    {
        get => unlit;
        set
        {
            unlit = value;
            dirty = true;
        }
    }

    /// <summary>How alpha is treated. Default <see cref="MaterialAlphaMode.Auto" /> (blend when the base colour's alpha is below 1).</summary>
    public MaterialAlphaMode AlphaMode
    {
        get => alphaMode;
        set
        {
            alphaMode = value;
            dirty = true;
        }
    }

    /// <summary>The alpha below which <see cref="MaterialAlphaMode.Mask" /> discards a fragment. Default 0.5.</summary>
    public float AlphaCutoff
    {
        get => alphaCutoff;
        set
        {
            alphaCutoff = value;
            dirty = true;
        }
    }

    /// <summary>
    /// Transforms the texture coordinate before the base-colour texture is sampled (<c>Vector2.Transform(uv, m)</c>;
    /// for example a glTF KHR_texture_transform). Default identity.
    /// </summary>
    public Matrix3x2 TextureTransform
    {
        get => textureTransform;
        set
        {
            textureTransform = value;
            dirty = true;
        }
    }

    /// <summary>True when the renderer alpha-blends the material (<see cref="MaterialAlphaMode.Blend" />, or <see cref="MaterialAlphaMode.Auto" /> with a base-colour alpha below 1).</summary>
    public bool IsTranslucent => alphaMode == MaterialAlphaMode.Blend || (alphaMode == MaterialAlphaMode.Auto && baseColor.A < 0.999f);

    /// <summary>The size of the material uniform block.</summary>
    internal const int UniformSize = 64;

    /// <summary>The material's bind group (group 1 of the lit shader).</summary>
    internal GpuBindGroup BindGroup { get; }

    /// <summary>Uploads changed properties.</summary>
    internal void Upload()
    {
        if (!dirty) return;
        dirty = false;
        MaterialUniforms data = new MaterialUniforms
        {
            BaseColor = baseColor.ToVector4(),
            Params = new Vector4(shininess, specularStrength, unlit ? 1f : 0f, AlphaParameter()),
            UvRow0 = new Vector4(textureTransform.M11, textureTransform.M21, textureTransform.M31, 0f),
            UvRow1 = new Vector4(textureTransform.M12, textureTransform.M22, textureTransform.M32, 0f),
        };
        uniforms.WriteValue(in data);
    }

    /// <summary>The shader's alpha parameter: 0 keeps alpha, -1 forces it to 1, a positive value is the mask cutoff.</summary>
    /// <returns>The parameter.</returns>
    private float AlphaParameter() => alphaMode switch
    {
        MaterialAlphaMode.Opaque => -1f,
        MaterialAlphaMode.Mask => System.Math.Max(alphaCutoff, 1e-6f),
        _ => 0f,
    };

    private protected override void ReleaseNative()
    {
        BindGroup?.Dispose();
        uniforms?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialUniforms
    {
        public Vector4 BaseColor;
        public Vector4 Params;
        public Vector4 UvRow0;
        public Vector4 UvRow1;
    }
}
