using System;
using System.Collections.Generic;
using System.Numerics;
using CodeBrix.WebGpu.Graphics.Models;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Draws <see cref="ModelInstance" />s. <see cref="Begin" /> with a camera and lighting, <see cref="Draw" /> any number
/// of instances (of any models), then <see cref="End" />: instances of the same model are batched, so each primitive
/// becomes ONE instanced draw for all of them. Opaque and alpha-tested primitives draw first, blended ones after
/// (depth read-only, sorted back to front). Per-frame drawing allocates nothing once the renderer has seen the models.
/// </summary>
/// <remarks>
/// <para>
/// Shading is glTF 2.0 metallic-roughness (Lambert diffuse, GGX, height-correlated Smith, Schlick Fresnel): base
/// colour factor and texture (sRGB), metallic-roughness texture (B = metal, G = rough) and factors, normal map with
/// scale (tangents from the file or generated at load; screen-space derivatives otherwise), occlusion with strength,
/// emissive factor, strength and texture, vertex colours, KHR_texture_transform and the texture-coordinate set per
/// texture, KHR_materials_unlit, alpha modes OPAQUE, MASK (cutoff) and BLEND (sorted back to front per instance and
/// primitive, depth writes off), doubleSided (back faces shade with flipped normals). KHR_materials_ior and
/// KHR_materials_specular set the dielectric reflectance (factors only), KHR_materials_clearcoat adds an approximate
/// coat lobe on the geometric normal, KHR_materials_transmission is not rendered (the surface draws as an opaque
/// dielectric). Skinned primitives are skinned on the GPU from each instance's joint palette
/// (<see cref="ModelInstance.GetJointPalette" />); morph targets use each instance's weights
/// (<see cref="ModelInstance.GetMorphWeights" />). Mirroring node or instance transforms (negative determinant) draw
/// with clockwise front faces, as glTF requires (a skinned primitive follows its instance's World). Lighting comes from <see cref="SceneLighting" />: up to
/// <see cref="SceneLighting.MaxDirectionalLights" /> directional lights, an exposure and an ambient term: image-based
/// lighting from <see cref="SceneLighting.Environment" /> (the context's procedural
/// <see cref="GraphicsContext.DefaultEnvironment" /> sky when none is set; diffuse irradiance, GGX-prefiltered
/// reflections, split-sum BRDF LUT, multiple-scattering energy compensation) or, with
/// <see cref="AmbientLighting.Hemisphere" />, a sky/ground colour gradient. No shadows.
/// </para>
/// <para>
/// Colour output: shading happens in linear space. An sRGB target (<c>RGBA8UnormSrgb</c>, <c>BGRA8UnormSrgb</c>)
/// encodes in hardware; an 8- or 10-bit UNORM target (<c>RGBA8Unorm</c>, <c>BGRA8Unorm</c>, <c>RGB10A2Unorm</c>) gets
/// the same encoding from the shader, so both show the same picture; float targets receive linear values. Blending
/// on a UNORM target happens on encoded values (on an sRGB target in linear light).
/// </para>
/// <para>
/// GPU data: the renderer packs every drawn instance's world matrix, node matrices, joint palettes and morph weights
/// into storage buffers once per <see cref="End" /> (read in the vertex shader), so the device needs storage buffers
/// in the vertex stage (every Vulkan, Metal and Direct3D 12 device has them).
/// </para>
/// <para>Several Begin/End pairs may run in one <see cref="GraphicsFrame" />. Not thread-safe.</para>
/// </remarks>
public sealed class ModelRenderer : GraphicsResource
{
    private readonly PbrModelRenderPath path;
    private readonly Dictionary<Model, List<ModelInstance>> buckets = new Dictionary<Model, List<ModelInstance>>();
    private readonly List<Model> order = new List<Model>();
    private readonly Stack<List<ModelInstance>> spareLists = new Stack<List<ModelInstance>>();
    private bool drawing;

    /// <summary>Creates a renderer.</summary>
    /// <param name="context">The context.</param>
    public ModelRenderer(GraphicsContext context)
        : base(context, "model renderer")
    {
        path = new PbrModelRenderPath(context);
    }

    /// <summary>The shading path (tests and the environment-lighting work reach its internals).</summary>
    internal PbrModelRenderPath Path => path;

    /// <summary>True between <see cref="Begin" /> and <see cref="End" />.</summary>
    public bool IsDrawing => drawing;

    /// <summary>How many draw calls the last <see cref="End" /> issued.</summary>
    public int LastDrawCallCount => path.LastDrawCallCount;

    /// <summary>How many primitive instances the last <see cref="End" /> drew.</summary>
    public int LastInstanceCount => path.LastInstanceCount;

    /// <summary>Starts drawing into a pass.</summary>
    /// <param name="renderPass">The pass (depth testing happens when its target has a depth view).</param>
    /// <param name="camera">The camera.</param>
    /// <param name="lighting">The lighting, or null for the defaults.</param>
    /// <exception cref="InvalidOperationException">The renderer is already drawing.</exception>
    public void Begin(RenderPass renderPass, Camera camera, SceneLighting lighting = null)
    {
        ArgumentNullException.ThrowIfNull(renderPass);
        ArgumentNullException.ThrowIfNull(camera);
        ThrowIfDisposed();
        if (drawing) throw new InvalidOperationException("Begin was called twice without End.");
        path.Begin(renderPass, camera, lighting);
        drawing = true;
    }

    /// <summary>Queues an instance (skipped when <see cref="ModelInstance.Visible" /> is false).</summary>
    /// <param name="instance">The instance.</param>
    /// <exception cref="ObjectDisposedException">The instance's model has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The instance has an open pose edit (<see cref="ModelInstance.BeginPoseEdit" />).</exception>
    public void Draw(ModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ThrowIfNotDrawing();
        instance.Model.ThrowIfDisposed();
        instance.ThrowIfEditing();
        if (!instance.Visible) return;
        if (!buckets.TryGetValue(instance.Model, out List<ModelInstance> bucket))
        {
            bucket = spareLists.Count > 0 ? spareLists.Pop() : new List<ModelInstance>();
            buckets[instance.Model] = bucket;
            order.Add(instance.Model);
        }

        bucket.Add(instance);
    }

    /// <summary>Records the queued instances into the pass.</summary>
    /// <exception cref="InvalidOperationException">The renderer is not drawing.</exception>
    public void End()
    {
        ThrowIfNotDrawing();
        drawing = false;
        try
        {
            foreach (Model model in order) path.DrawModel(model, buckets[model]);
            path.End();
        }
        finally
        {
            foreach (Model model in order)
            {
                List<ModelInstance> bucket = buckets[model];
                bucket.Clear();
                spareLists.Push(bucket);
            }

            buckets.Clear();
            order.Clear();
        }
    }

    /// <summary>Drops the GPU state the renderer keeps for a model (its materials); call after disposing a model you will not draw again.</summary>
    /// <param name="model">The model.</param>
    public void Forget(Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        path.Forget(model);
    }

    private protected override void ReleaseNative() => path.Dispose();

    private void ThrowIfNotDrawing()
    {
        ThrowIfDisposed();
        if (!drawing) throw new InvalidOperationException("Call Begin first.");
    }
}
