using System.Numerics;
using System.Runtime.InteropServices;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// The surface of a mesh drawn by a <see cref="MeshRenderer" />: a base colour, an optional base-colour texture,
/// Blinn-Phong specular settings and whether both faces are drawn. Create one with
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
    private bool dirty = true;

    internal LitMaterial(MeshRenderer renderer, Color4 baseColor, GpuTexture texture, bool doubleSided, string label, GpuSampler sampler)
        : base(renderer.Context, label)
    {
        Renderer = renderer;
        this.baseColor = baseColor;
        BaseColorTexture = texture;
        DoubleSided = doubleSided;
        uniforms = Context.CreateUniformBuffer(32, label == null ? "material" : label + " uniforms");
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

    /// <summary>True when the base colour is translucent (the renderer then alpha-blends it).</summary>
    public bool IsTranslucent => baseColor.A < 0.999f;

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
            Params = new Vector4(shininess, specularStrength, unlit ? 1f : 0f, 0f),
        };
        uniforms.WriteValue(in data);
    }

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
    }
}
