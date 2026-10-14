using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Shaders;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics.Models;

/// <summary>
/// The metallic-roughness shading path of <see cref="ModelRenderer" /> (see <see cref="PbrShader" /> for the shader and
/// its bind groups). Per frame it packs, for every instance, one instance record, the instance's node matrices and
/// joint palettes and its morph weights into three storage buffers (grow-only, written once per
/// <see cref="End" />); each opaque or alpha-tested (model, draw item) then becomes ONE instanced draw, and blended
/// draw items are drawn one instance at a time, sorted back to front. Pipelines are cached per (shader features, colour
/// format, depth format); per model it caches one material bind group per material, a draw-uniform slot per draw
/// item, the morph-delta buffer and the model bind group.
/// </summary>
internal sealed class PbrModelRenderPath : IModelRenderPath
{
    private static readonly int InstanceSize = Unsafe.SizeOf<PbrInstance>();
    private static readonly int MatrixSize = Unsafe.SizeOf<Matrix4x4>();

    private readonly GraphicsContext context;
    private readonly Dictionary<PbrFeatures, GpuShader> shaders = new Dictionary<PbrFeatures, GpuShader>();
    private readonly Dictionary<(PbrFeatures Features, TextureFormat Color, TextureFormat Depth), GpuRenderPipeline> pipelines = new Dictionary<(PbrFeatures, TextureFormat, TextureFormat), GpuRenderPipeline>();
    private readonly Dictionary<Model, PbrModelState> models = new Dictionary<Model, PbrModelState>();
    private readonly GpuBindGroupLayout frameLayout;
    private readonly GpuBindGroupLayout materialLayout;
    private readonly GpuBindGroupLayout instanceLayout;
    private readonly GpuBindGroupLayout modelLayout;
    private readonly GpuSampler defaultSampler;
    private readonly GpuBuffer emptyStorage;
    private readonly GpuTexture placeholderCubeTexture;
    private readonly GpuTexture placeholderCube;
    private readonly GpuTexture placeholderLut;
    private readonly int frameSlotSize;
    private readonly int drawSlotSize;

    private readonly Dictionary<object, GpuBindGroup> frameGroups = new Dictionary<object, GpuBindGroup>();
    private readonly List<object> staleGroups = new List<object>();
    private GpuTexture overrideIrradiance;
    private GpuTexture overridePrefiltered;
    private GpuTexture overrideLut;
    private GpuSampler overrideSampler;
    private float overrideIntensity;

    private GpuBuffer frameUniforms;
    private GpuBindGroup frameGroup;
    private object frameKey;
    private GpuTexture frameIrradiance;
    private GpuTexture framePrefiltered;
    private GpuTexture frameLut;
    private GpuSampler frameSampler;
    private int frameCapacity;
    private int frameSlot;

    private GpuBuffer instanceBuffer;
    private GpuBuffer matrixBuffer;
    private GpuBuffer weightBuffer;
    private GpuBindGroup instanceGroup;
    private int instanceCursor;
    private int matrixCursor;
    private int weightCursor;

    private PbrInstance[] instanceData = new PbrInstance[256];
    private Matrix4x4[] matrixData = new Matrix4x4[1024];
    private float[] weightData = new float[256];
    private int instanceCount;
    private int matrixCount;
    private int weightCount;

    private byte[] mirrorFlags = new byte[256];
    private int mirrorCount;

    private PbrBatch[] batches = new PbrBatch[16];
    private int batchCount;
    private float[] blendKeys = new float[64];
    private PbrBlendItem[] blendItems = new PbrBlendItem[64];
    private int blendCount;

    private long frameId = -1;
    private RenderPass pass;
    private PbrFrameUniforms frameData;
    private Vector3 cameraPosition;
    private Vector3 cameraForward;

    public PbrModelRenderPath(GraphicsContext context)
    {
        this.context = context;
        Limits limits = context.Limits;
        uint alignment = Math.Max(limits.MinUniformBufferOffsetAlignment, 16u);
        frameSlotSize = Align(Unsafe.SizeOf<PbrFrameUniforms>(), (int)alignment);
        drawSlotSize = Align(Unsafe.SizeOf<PbrDrawUniforms>(), (int)alignment);

        frameLayout = context.CreateBindGroupLayout(
            "model frame",
            BindingLayoutEntry.Uniform(0, ShaderStage.Vertex | ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<PbrFrameUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Cube),
            BindingLayoutEntry.Texture(2, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Cube),
            BindingLayoutEntry.Texture(3, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(4, ShaderStage.Fragment));
        materialLayout = context.CreateBindGroupLayout(
            "model material",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, false, (ulong)Unsafe.SizeOf<PbrMaterialUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment),
            BindingLayoutEntry.Texture(2, ShaderStage.Fragment),
            BindingLayoutEntry.Texture(3, ShaderStage.Fragment),
            BindingLayoutEntry.Texture(4, ShaderStage.Fragment),
            BindingLayoutEntry.Texture(5, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(6, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(7, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(8, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(9, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(10, ShaderStage.Fragment));
        instanceLayout = context.CreateBindGroupLayout(
            "model instances",
            BindingLayoutEntry.Storage(0, ShaderStage.Vertex, true),
            BindingLayoutEntry.Storage(1, ShaderStage.Vertex, true),
            BindingLayoutEntry.Storage(2, ShaderStage.Vertex, true));
        modelLayout = context.CreateBindGroupLayout(
            "model draws",
            BindingLayoutEntry.Uniform(0, ShaderStage.Vertex, true, (ulong)Unsafe.SizeOf<PbrDrawUniforms>()),
            BindingLayoutEntry.Storage(1, ShaderStage.Vertex, true),
            BindingLayoutEntry.Storage(2, ShaderStage.Vertex, true));

        defaultSampler = context.CreateSampler(SamplerDescription.LinearRepeat);
        emptyStorage = context.CreateBuffer(64, BufferUsage.Storage, "model empty storage");
        placeholderCubeTexture = context.CreateTextureArray(1, 1, 6, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding, 1, "model environment placeholder");
        placeholderCube = placeholderCubeTexture.CreateCubeView("model environment placeholder cube");
        placeholderLut = context.CreateTexture2D(1, 1, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding, 1, "model BRDF LUT placeholder");

        CreateFrameUniforms(4);
        instanceBuffer = context.CreateBuffer((ulong)(InstanceSize * 256), BufferUsage.Storage | BufferUsage.CopyDst, "model instances");
        matrixBuffer = context.CreateBuffer((ulong)(MatrixSize * 1024), BufferUsage.Storage | BufferUsage.CopyDst, "model matrices");
        weightBuffer = context.CreateBuffer(sizeof(float) * 256, BufferUsage.Storage | BufferUsage.CopyDst, "model morph weights");
        CreateInstanceGroup();
    }

    public int LastDrawCallCount { get; private set; }

    public int LastInstanceCount { get; private set; }

    /// <summary>The cached pipelines (one per shader features, colour format and depth format in use).</summary>
    internal int PipelineCount => pipelines.Count;

    /// <summary>The cached shader variants.</summary>
    internal int ShaderCount => shaders.Count;

    /// <summary>The models with cached GPU state.</summary>
    internal int ModelStateCount => models.Count;

    /// <summary>Test switch: draw skinned primitives with their node transform instead of skinning them.</summary>
    internal bool SkinningDisabled { get; set; }

    /// <summary>True when a test override environment is set (<see cref="SetEnvironment" />).</summary>
    internal bool HasEnvironmentOverride => overrideIrradiance != null;

    /// <summary>The frame bind groups cached per environment (tests: one per environment in use, plus one without).</summary>
    internal int FrameGroupCount => frameGroups.Count;

    /// <summary>
    /// Test hook: image-based lighting from raw textures that takes precedence over <see cref="SceneLighting.Environment" />
    /// whenever <see cref="SceneLighting.Ambient" /> is <see cref="AmbientLighting.Environment" />: a diffuse irradiance cube,
    /// a prefiltered specular cube (roughness 0..1 maps onto its mips) and a BRDF LUT (RG = scale and bias of F0, indexed
    /// by (N.V, roughness)). The textures are not owned. Null textures remove the override.
    /// </summary>
    /// <param name="irradiance">The irradiance cube view.</param>
    /// <param name="prefiltered">The prefiltered cube view.</param>
    /// <param name="lut">The BRDF LUT.</param>
    /// <param name="sampler">A filtering sampler (linear, clamp; mipmap linear), or null for the context's linear clamp sampler.</param>
    /// <param name="intensity">Scales both environment terms.</param>
    internal void SetEnvironment(GpuTexture irradiance, GpuTexture prefiltered, GpuTexture lut, GpuSampler sampler, float intensity)
    {
        bool set = irradiance != null && prefiltered != null && lut != null;
        overrideIrradiance = set ? irradiance : null;
        overridePrefiltered = set ? prefiltered : null;
        overrideLut = set ? lut : null;
        overrideSampler = sampler;
        overrideIntensity = intensity;
        if (frameGroups.Remove(OverrideKey, out GpuBindGroup group)) group.Dispose();
    }

    private object OverrideKey => frameGroups;

    public void Begin(RenderPass renderPass, Camera camera, SceneLighting lighting)
    {
        if (renderPass.IsEnded) throw new InvalidOperationException("The render pass has ended.");
        lighting ??= new SceneLighting();
        pass = renderPass;
        if (renderPass.Frame.Id != frameId)
        {
            frameId = renderPass.Frame.Id;
            frameSlot = 0;
            instanceCursor = 0;
            matrixCursor = 0;
            weightCursor = 0;
        }

        cameraPosition = camera.Position;
        cameraForward = camera.Forward;
        Color4 sky = lighting.AmbientColor;
        Color4 ground = lighting.AmbientGroundColor ?? sky;
        Vector3 up = lighting.AmbientUp.LengthSquared() > 0 ? Vector3.Normalize(lighting.AmbientUp) : Vector3.UnitY;
        frameData = default;
        frameData.ViewProjection = camera.GetViewProjectionMatrix(renderPass.Target.AspectRatio);
        frameData.CameraPosition = new Vector4(camera.Position, lighting.Exposure);
        frameData.AmbientSky = sky.ToVector4();
        frameData.AmbientGround = ground.ToVector4();
        SelectEnvironment(lighting);
        int lights = 0;
        AddLight(ref lights, lighting.LightDirection, lighting.LightColor, lighting.LightIntensity);
        List<DirectionalLight> more = lighting.AdditionalLights;
        for (int index = 0; index < more.Count && lights < SceneLighting.MaxDirectionalLights; index++)
        {
            DirectionalLight light = more[index];
            AddLight(ref lights, light.Direction, light.Color, light.Intensity);
        }

        frameData.AmbientUp = new Vector4(up, lights);
        instanceCount = 0;
        matrixCount = 0;
        weightCount = 0;
        mirrorCount = 0;
        batchCount = 0;
        blendCount = 0;
    }

    public void DrawModel(Model model, List<ModelInstance> instances)
    {
        PbrModelState state = StateFor(model);
        int nodes = model.Nodes.Count;
        int palette = model.PaletteSize;
        int weights = model.MorphWeightCount;
        int count = instances.Count;
        PbrDraw[] draws = state.Draws;
        EnsureCpu(ref instanceData, instanceCount + count);
        EnsureCpu(ref matrixData, matrixCount + (count * (nodes + palette)));
        EnsureCpu(ref weightData, weightCount + (count * weights));
        EnsureCpu(ref mirrorFlags, mirrorCount + (count * draws.Length));
        if (batchCount == batches.Length) Array.Resize(ref batches, batches.Length * 2);
        int batchIndex = batchCount;
        int first = instanceCount;
        int mirrorStart = mirrorCount;
        bool anyMirrored = false;
        ReadOnlySpan<float> baked = model.BakedMorphWeights;

        // Instances whose World mirrors are written after the others, so a mirrored placement costs one extra draw
        // per draw item, not one per instance (mirroring flips the front-face winding: glTF 2.0 3.7.4).
        for (int pass = 0; pass < 2; pass++)
        {
            int deferred = 0;
            for (int index = 0; index < count; index++)
            {
                ModelInstance instance = instances[index];
                bool worldMirrored = Determinant3(instance.World) < 0f;
                if (worldMirrored != (pass == 1))
                {
                    deferred++;
                    continue;
                }

                int position = instanceCount - first;
                ref PbrInstance record = ref instanceData[instanceCount++];
                record.World = instance.World;
                record.NodeBase = (uint)matrixCount;
                record.JointBase = (uint)(matrixCount + nodes);
                record.WeightBase = (uint)weightCount;
                ReadOnlySpan<Matrix4x4> nodeMatrices = instance.NodeMatrices;
                nodeMatrices.CopyTo(matrixData.AsSpan(matrixCount, nodes));
                matrixCount += nodes;
                if (palette > 0)
                {
                    instance.JointPalettes.CopyTo(matrixData.AsSpan(matrixCount, palette));
                    matrixCount += palette;
                }

                if (weights > 0)
                {
                    ReadOnlySpan<float> source = instance.MorphWeights;
                    Span<float> destination = weightData.AsSpan(weightCount, weights);
                    for (int weight = 0; weight < weights; weight++) destination[weight] = source[weight] - baked[weight];
                    weightCount += weights;
                }

                for (int drawIndex = 0; drawIndex < draws.Length; drawIndex++)
                {
                    PbrDraw draw = draws[drawIndex];
                    bool mirrored = draw.Skinned ? worldMirrored : worldMirrored != (Determinant3(nodeMatrices[draw.Node]) < 0f);
                    mirrorFlags[mirrorCount++] = mirrored ? (byte)1 : (byte)0;
                    anyMirrored |= mirrored;
                    if (!draw.Blend) continue;
                    Vector3 center = draw.Skinned ? draw.Center : Vector3.Transform(draw.Center, nodeMatrices[draw.Node]);
                    Vector3 world = Vector3.Transform(center, instance.World);
                    if (blendCount == blendKeys.Length)
                    {
                        Array.Resize(ref blendKeys, blendKeys.Length * 2);
                        Array.Resize(ref blendItems, blendItems.Length * 2);
                    }

                    blendKeys[blendCount] = -Vector3.Dot(world - cameraPosition, cameraForward);
                    blendItems[blendCount++] = new PbrBlendItem(batchIndex, drawIndex, position);
                }
            }

            if (deferred == 0) break;
        }

        batches[batchCount++] = new PbrBatch(state, first, count, mirrorStart, anyMirrored);
    }

    public void End()
    {
        RenderPass current = pass;
        pass = null;
        LastDrawCallCount = 0;
        LastInstanceCount = 0;
        if (batchCount == 0) return;
        if (current.IsEnded) throw new InvalidOperationException("The render pass ended before the renderer.");

        if (frameSlot >= frameCapacity)
        {
            CreateFrameUniforms(frameCapacity * 2);
            frameGroup = FrameGroup(frameKey, frameIrradiance, framePrefiltered, frameLut, frameSampler);
        }

        frameUniforms.WriteValue(in frameData, (ulong)(frameSlot * frameSlotSize));
        UploadInstances();

        RenderTargetView target = current.Target;
        PbrFeatures encode = NeedsShaderEncoding(target.ColorFormat) ? PbrFeatures.EncodeSrgb : PbrFeatures.None;
        Span<uint> frameOffset = stackalloc uint[1] { (uint)(frameSlot * frameSlotSize) };
        Span<uint> drawOffset = stackalloc uint[1];
        PbrRecorder recorder = new PbrRecorder { Pass = current, ColorFormat = target.ColorFormat, DepthFormat = target.DepthFormat, Encode = encode };

        for (int batch = 0; batch < batchCount; batch++)
        {
            PbrBatch item = batches[batch];
            PbrDraw[] draws = item.State.Draws;
            for (int drawIndex = 0; drawIndex < draws.Length; drawIndex++)
            {
                if (draws[drawIndex].Blend) continue;
                if (!item.AnyMirrored)
                {
                    Record(ref recorder, frameOffset, drawOffset, item.State, drawIndex, (uint)(instanceCursor + item.FirstInstance), (uint)item.Count, false);
                    continue;
                }

                // One draw per run of instances with the same winding.
                int start = 0;
                while (start < item.Count)
                {
                    byte mirrored = mirrorFlags[item.MirrorStart + (start * draws.Length) + drawIndex];
                    int end = start + 1;
                    while (end < item.Count && mirrorFlags[item.MirrorStart + (end * draws.Length) + drawIndex] == mirrored) end++;
                    Record(ref recorder, frameOffset, drawOffset, item.State, drawIndex, (uint)(instanceCursor + item.FirstInstance + start), (uint)(end - start), mirrored != 0);
                    start = end;
                }
            }
        }

        if (blendCount > 0)
        {
            Array.Sort(blendKeys, blendItems, 0, blendCount);
            for (int index = 0; index < blendCount; index++)
            {
                PbrBlendItem blend = blendItems[index];
                PbrBatch item = batches[blend.Batch];
                bool mirrored = item.AnyMirrored && mirrorFlags[item.MirrorStart + (blend.Instance * item.State.Draws.Length) + blend.Draw] != 0;
                Record(ref recorder, frameOffset, drawOffset, item.State, blend.Draw, (uint)(instanceCursor + item.FirstInstance + blend.Instance), 1, mirrored);
            }
        }

        frameSlot++;
        instanceCursor += instanceCount;
        matrixCursor += matrixCount;
        weightCursor += weightCount;
        batchCount = 0;
        blendCount = 0;
    }

    public void Forget(Model model)
    {
        if (!models.Remove(model, out PbrModelState state)) return;
        state.Dispose();
    }

    public void Dispose()
    {
        foreach (PbrModelState state in models.Values) state.Dispose();
        models.Clear();
        foreach (GpuRenderPipeline pipeline in pipelines.Values) pipeline.Dispose();
        pipelines.Clear();
        foreach (GpuShader shader in shaders.Values) shader.Dispose();
        shaders.Clear();
        foreach (GpuBindGroup group in frameGroups.Values) group.Dispose();
        frameGroups.Clear();
        frameUniforms?.Dispose();
        instanceGroup?.Dispose();
        instanceBuffer?.Dispose();
        matrixBuffer?.Dispose();
        weightBuffer?.Dispose();
        placeholderLut.Dispose();
        placeholderCube.Dispose();
        placeholderCubeTexture.Dispose();
        emptyStorage.Dispose();
        defaultSampler.Dispose();
        modelLayout.Dispose();
        instanceLayout.Dispose();
        materialLayout.Dispose();
        frameLayout.Dispose();
    }

    /// <summary>True for 8- and 10-bit UNORM colour formats: the shader encodes to sRGB itself (sRGB formats encode in hardware; float formats stay linear).</summary>
    /// <param name="format">The target format.</param>
    /// <returns>True when the shader encodes.</returns>
    internal static bool NeedsShaderEncoding(TextureFormat format) =>
        format is TextureFormat.RGBA8Unorm or TextureFormat.BGRA8Unorm or TextureFormat.RGB10A2Unorm;

    /// <summary>The determinant of a matrix's upper 3 x 3 (negative = the transform mirrors).</summary>
    /// <param name="m">The matrix.</param>
    /// <returns>The determinant.</returns>
    internal static float Determinant3(in Matrix4x4 m) =>
        (m.M11 * ((m.M22 * m.M33) - (m.M23 * m.M32))) - (m.M12 * ((m.M21 * m.M33) - (m.M23 * m.M31))) + (m.M13 * ((m.M21 * m.M32) - (m.M22 * m.M31)));

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void EnsureCpu<T>(ref T[] array, int needed)
    {
        if (needed > array.Length) Array.Resize(ref array, Math.Max(array.Length * 2, needed));
    }

    private void AddLight(ref int lights, Vector3 direction, Color4 color, float intensity)
    {
        if (intensity == 0f || direction.LengthSquared() == 0f) return;
        Vector3 normalized = Vector3.Normalize(direction);
        frameData.Lights[lights * 2] = new Vector4(normalized, 0f);
        frameData.Lights[(lights * 2) + 1] = new Vector4(color.R * intensity, color.G * intensity, color.B * intensity, 0f);
        lights++;
    }

    private void UploadInstances()
    {
        bool regroup = false;
        if (instanceCursor + instanceCount > (int)(instanceBuffer.Size / (ulong)InstanceSize))
        {
            instanceBuffer = Grow(instanceBuffer, (ulong)(Math.Max(instanceCount, (int)(instanceBuffer.Size / (ulong)InstanceSize)) * 2 * InstanceSize), "model instances");
            instanceCursor = 0;
            regroup = true;
        }

        if (matrixCursor + matrixCount > (int)(matrixBuffer.Size / (ulong)MatrixSize))
        {
            matrixBuffer = Grow(matrixBuffer, (ulong)(Math.Max(matrixCount, (int)(matrixBuffer.Size / (ulong)MatrixSize)) * 2 * MatrixSize), "model matrices");
            matrixCursor = 0;
            regroup = true;
        }

        if (weightCursor + weightCount > (int)(weightBuffer.Size / sizeof(float)))
        {
            weightBuffer = Grow(weightBuffer, (ulong)(Math.Max(weightCount, (int)(weightBuffer.Size / sizeof(float))) * 2 * sizeof(float)), "model morph weights");
            weightCursor = 0;
            regroup = true;
        }

        if (regroup) CreateInstanceGroup();
        uint matrixBase = (uint)matrixCursor;
        uint weightBase = (uint)weightCursor;
        if (matrixBase != 0 || weightBase != 0)
        {
            for (int index = 0; index < instanceCount; index++)
            {
                ref PbrInstance record = ref instanceData[index];
                record.NodeBase += matrixBase;
                record.JointBase += matrixBase;
                record.WeightBase += weightBase;
            }
        }

        instanceBuffer.Write(new ReadOnlySpan<PbrInstance>(instanceData, 0, instanceCount), (ulong)(instanceCursor * InstanceSize));
        if (matrixCount > 0) matrixBuffer.Write(new ReadOnlySpan<Matrix4x4>(matrixData, 0, matrixCount), (ulong)(matrixCursor * MatrixSize));
        if (weightCount > 0) weightBuffer.Write(new ReadOnlySpan<float>(weightData, 0, weightCount), (ulong)(weightCursor * sizeof(float)));
    }

    private GpuBuffer Grow(GpuBuffer buffer, ulong size, string label)
    {
        buffer.Dispose();
        return context.CreateBuffer(size, BufferUsage.Storage | BufferUsage.CopyDst, label);
    }

    private void Record(ref PbrRecorder recorder, Span<uint> frameOffset, Span<uint> drawOffset, PbrModelState state, int drawIndex, uint firstInstance, uint count, bool mirrored)
    {
        PbrDraw draw = state.Draws[drawIndex];
        PbrFeatures features = SkinningDisabled ? draw.Features & ~PbrFeatures.Skinned : draw.Features;
        if (mirrored) features |= PbrFeatures.Mirrored;
        GpuRenderPipeline pipeline = Pipeline(features | recorder.Encode, recorder.ColorFormat, recorder.DepthFormat);
        RenderPass current = recorder.Pass;
        if (!ReferenceEquals(pipeline, recorder.Pipeline))
        {
            current.SetPipeline(pipeline);
            current.SetBindGroup(0, frameGroup, frameOffset);
            current.SetBindGroup(2, instanceGroup);
            recorder.Pipeline = pipeline;
            recorder.Material = null;
        }

        if (!ReferenceEquals(state, recorder.State))
        {
            current.SetVertexBuffer(0, state.Model.VertexBuffer);
            current.SetIndexBuffer(state.Model.IndexBuffer, state.Model.IndexFormat);
            recorder.State = state;
        }

        if (!ReferenceEquals(draw.Material, recorder.Material))
        {
            current.SetBindGroup(1, draw.Material.Group);
            recorder.Material = draw.Material;
        }

        drawOffset[0] = (uint)(drawIndex * drawSlotSize);
        current.SetBindGroup(3, state.Group, drawOffset);
        ModelPrimitive primitive = draw.Primitive;
        current.DrawIndexed(primitive.IndexCount, count, primitive.FirstIndex, primitive.BaseVertex, firstInstance);
        LastDrawCallCount++;
        LastInstanceCount += (int)count;
    }

    private GpuRenderPipeline Pipeline(PbrFeatures features, TextureFormat color, TextureFormat depth)
    {
        if (pipelines.TryGetValue((features, color, depth), out GpuRenderPipeline pipeline)) return pipeline;
        PbrFeatures shaderFeatures = features & ~PbrFeatures.Mirrored;
        if (!shaders.TryGetValue(shaderFeatures, out GpuShader shader))
        {
            shader = context.CreateShader(PbrShader.Source(shaderFeatures), "model pbr " + shaderFeatures);
            shaders[shaderFeatures] = shader;
        }

        bool blend = (features & PbrFeatures.AlphaBlend) != 0;
        RenderPipelineBuilder builder = context.CreateRenderPipeline(shader)
            .WithLabel("model pbr " + features + " " + color)
            .WithVertexBuffer(VertexLayout.Describe()
                .Add(VertexFormat.Float32x3, 0)
                .Add(VertexFormat.Float32x3, 1)
                .Add(VertexFormat.Float32x4, 2)
                .Add(VertexFormat.Float32x2, 3)
                .Add(VertexFormat.Float32x2, 4)
                .Add(VertexFormat.Unorm16x4, 5)
                .Build(ModelVertex.Size))
            .WithColorTarget(color, blend ? BlendPreset.AlphaBlend : BlendPreset.Opaque)
            .WithCulling((features & PbrFeatures.DoubleSided) != 0 ? CullPreset.None : CullPreset.Back, (features & PbrFeatures.Mirrored) != 0 ? FrontFace.CW : FrontFace.CCW)
            .WithBindGroupLayouts(frameLayout, materialLayout, instanceLayout, modelLayout);
        if (depth != TextureFormat.Undefined) builder.WithDepth(depth, blend ? DepthPreset.ReadOnly : DepthPreset.ReadWrite);
        pipeline = builder.Build();
        pipelines[(features, color, depth)] = pipeline;
        return pipeline;
    }

    private void CreateFrameUniforms(int slots)
    {
        foreach (GpuBindGroup group in frameGroups.Values) group.Dispose();
        frameGroups.Clear();
        frameUniforms?.Dispose();
        frameUniforms = context.CreateBuffer((ulong)(slots * frameSlotSize), BufferUsage.Uniform | BufferUsage.CopyDst, "model frame");
        frameCapacity = slots;
        frameSlot = 0;
    }

    /// <summary>Chooses this Begin's environment (or the hemispheric ambient) and its cached frame bind group.</summary>
    private void SelectEnvironment(SceneLighting lighting)
    {
        float rotation = lighting.EnvironmentRotation;
        if (lighting.Ambient == AmbientLighting.Environment && overrideIrradiance != null)
        {
            frameData.Environment = new Vector4(1f, overrideIntensity, Math.Max(0, overridePrefiltered.MipLevelCount - 1), rotation);
            UseFrameGroup(OverrideKey, overrideIrradiance, overridePrefiltered, overrideLut, overrideSampler);
        }
        else if (lighting.Ambient == AmbientLighting.Environment)
        {
            EnvironmentMap environment = lighting.Environment ?? context.DefaultEnvironment;
            environment.ThrowIfDisposed();
            frameData.Environment = new Vector4(1f, lighting.EnvironmentIntensity, environment.PrefilteredMipLevels - 1, rotation);
            UseFrameGroup(environment, environment.Irradiance, environment.Prefiltered, null, null);
        }
        else
        {
            frameData.Environment = new Vector4(0f, 0f, 0f, rotation);
            UseFrameGroup(this, null, null, null, null);
        }
    }

    private void UseFrameGroup(object key, GpuTexture irradiance, GpuTexture prefiltered, GpuTexture lut, GpuSampler sampler)
    {
        frameKey = key;
        frameIrradiance = irradiance;
        framePrefiltered = prefiltered;
        frameLut = lut;
        frameSampler = sampler;
        frameGroup = FrameGroup(key, irradiance, prefiltered, lut, sampler);
    }

    /// <summary>The frame bind group for an environment (cached until the frame buffer grows; disposed environments are dropped).</summary>
    private GpuBindGroup FrameGroup(object key, GpuTexture irradiance, GpuTexture prefiltered, GpuTexture lut, GpuSampler sampler)
    {
        if (frameGroups.TryGetValue(key, out GpuBindGroup group)) return group;
        foreach (KeyValuePair<object, GpuBindGroup> pair in frameGroups)
        {
            if (pair.Key is EnvironmentMap stale && stale.IsDisposed) staleGroups.Add(pair.Key);
        }

        foreach (object stale in staleGroups)
        {
            frameGroups.Remove(stale, out GpuBindGroup old);
            old.Dispose();
        }

        staleGroups.Clear();
        bool environment = irradiance != null;
        group = context.CreateBindGroup(
            frameLayout,
            "model frame",
            BindingEntry.ForBuffer(0, frameUniforms, 0, (ulong)Unsafe.SizeOf<PbrFrameUniforms>()),
            BindingEntry.ForTexture(1, irradiance ?? placeholderCube),
            BindingEntry.ForTexture(2, prefiltered ?? placeholderCube),
            BindingEntry.ForTexture(3, environment ? lut ?? context.BrdfLut : placeholderLut),
            BindingEntry.ForSampler(4, sampler ?? context.LinearClampSampler));
        frameGroups[key] = group;
        return group;
    }

    private void CreateInstanceGroup()
    {
        instanceGroup?.Dispose();
        instanceGroup = context.CreateBindGroup(
            instanceLayout,
            "model instances",
            BindingEntry.ForBuffer(0, instanceBuffer),
            BindingEntry.ForBuffer(1, matrixBuffer),
            BindingEntry.ForBuffer(2, weightBuffer));
    }

    private PbrModelState StateFor(Model model)
    {
        if (models.TryGetValue(model, out PbrModelState state)) return state;
        state = new PbrModelState(this, model);
        models[model] = state;
        return state;
    }

    /// <summary>Builds a material's uniforms and bind group (the textures stay owned by the model or its texture cache).</summary>
    private PbrMaterial CreateMaterial(ModelMaterial source, string label)
    {
        PbrMaterialUniforms data = default;
        data.BaseColor = source.BaseColorFactor;
        data.Emissive = new Vector4(source.EmissiveFactor * source.EmissiveStrength, source.ClearCoatRoughness);
        data.Params = new Vector4(source.MetallicFactor, source.RoughnessFactor, source.NormalScale, source.OcclusionStrength);
        float ior = Math.Max(source.IndexOfRefraction, 1f);
        float f0 = (ior - 1f) / (ior + 1f);
        data.Extra = new Vector4(source.AlphaCutoff, f0 * f0, source.SpecularFactor, source.ClearCoatFactor);
        data.Specular = new Vector4(source.SpecularColor, 0f);
        ModelTextureSlot[] slots = { source.BaseColorTexture, source.MetallicRoughnessTexture, source.NormalTexture, source.OcclusionTexture, source.EmissiveTexture };
        uint texCoords = 0;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (slots[slot].TexCoord == 1) texCoords |= 1u << slot;
            Matrix3x2 transform = slots[slot].Transform;
            data.Uv[slot * 2] = new Vector4(transform.M11, transform.M21, transform.M31, 0f);
            data.Uv[(slot * 2) + 1] = new Vector4(transform.M12, transform.M22, transform.M32, 0f);
        }

        data.TexCoordSets = texCoords;
        PbrFeatures features = PbrFeatures.None;
        if (source.NormalTexture.Texture != null) features |= PbrFeatures.NormalMap;
        if (source.Unlit) features |= PbrFeatures.Unlit;
        if (source.AlphaMode == MaterialAlphaMode.Mask) features |= PbrFeatures.AlphaMask;
        if (source.AlphaMode == MaterialAlphaMode.Blend) features |= PbrFeatures.AlphaBlend;
        if (source.DoubleSided) features |= PbrFeatures.DoubleSided;
        if (source.ClearCoatFactor > 0f) features |= PbrFeatures.ClearCoat;

        GpuBuffer uniforms = context.CreateBuffer((ulong)Unsafe.SizeOf<PbrMaterialUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, label + " uniforms");
        uniforms.WriteValue(in data);
        GpuTexture white = context.WhiteTexture;
        GpuBindGroup group = context.CreateBindGroup(
            materialLayout,
            label,
            BindingEntry.ForBuffer(0, uniforms),
            BindingEntry.ForTexture(1, slots[0].Texture ?? white),
            BindingEntry.ForTexture(2, slots[1].Texture ?? white),
            BindingEntry.ForTexture(3, slots[2].Texture ?? white),
            BindingEntry.ForTexture(4, slots[3].Texture ?? white),
            BindingEntry.ForTexture(5, slots[4].Texture ?? white),
            BindingEntry.ForSampler(6, slots[0].Sampler ?? defaultSampler),
            BindingEntry.ForSampler(7, slots[1].Sampler ?? defaultSampler),
            BindingEntry.ForSampler(8, slots[2].Sampler ?? defaultSampler),
            BindingEntry.ForSampler(9, slots[3].Sampler ?? defaultSampler),
            BindingEntry.ForSampler(10, slots[4].Sampler ?? defaultSampler));
        return new PbrMaterial(uniforms, group, features);
    }

    /// <summary>What the path keeps per model: materials, draw items with their uniforms, morph deltas and the model bind group.</summary>
    private sealed class PbrModelState : IDisposable
    {
        private readonly PbrMaterial[] materials;
        private readonly PbrMaterial fallback;
        private readonly GpuBuffer drawUniforms;
        private readonly GpuBuffer morphDeltas;

        public PbrModelState(PbrModelRenderPath path, Model model)
        {
            Model = model;
            string label = model.Label ?? model.Name ?? "model";
            materials = new PbrMaterial[model.Materials.Count];
            for (int index = 0; index < materials.Length; index++) materials[index] = path.CreateMaterial(model.Materials[index], label + " material " + index);
            fallback = path.CreateMaterial(model.DefaultMaterial, label + " default material");

            Dictionary<ModelPrimitive, (uint Base, uint Channels, uint Stride)> morphLayout = new Dictionary<ModelPrimitive, (uint, uint, uint)>();
            List<Vector4> deltas = new List<Vector4>();
            ModelDrawItem[] items = model.DrawItems;
            Draws = new PbrDraw[items.Length];
            byte[] slots = new byte[Math.Max(1, items.Length) * path.drawSlotSize];
            for (int index = 0; index < items.Length; index++)
            {
                ModelDrawItem item = items[index];
                ModelPrimitive primitive = model.Meshes[item.Mesh].Primitives[item.Primitive];
                int skin = model.Nodes[item.Node].SkinIndex;
                bool skinned = primitive.IsSkinned && skin >= 0 && skin < model.Skins.Count;
                PbrMaterial material = primitive.Material.Index >= 0 ? materials[primitive.Material.Index] : fallback;
                PbrDrawUniforms uniforms = new PbrDrawUniforms
                {
                    Node = (uint)item.Node,
                    BaseVertex = (uint)primitive.BaseVertex,
                    SkinBase = skinned ? (uint)primitive.SkinBaseVertex : 0u,
                    JointOffset = skinned ? (uint)model.SkinPaletteOffsets[skin] : 0u,
                };
                PbrFeatures features = material.Features;
                if (skinned) features |= PbrFeatures.Skinned;
                int weightOffset = model.MorphWeightOffsets[item.Node];
                if (primitive.MorphTargets.Count > 0 && weightOffset >= 0)
                {
                    if (!morphLayout.TryGetValue(primitive, out (uint Base, uint Channels, uint Stride) layout))
                    {
                        layout = PackMorph(primitive, deltas);
                        morphLayout[primitive] = layout;
                    }

                    if (layout.Stride > 0)
                    {
                        features |= PbrFeatures.Morphed;
                        uniforms.MorphTargets = (uint)Math.Min(primitive.MorphTargets.Count, model.Nodes[item.Node].DefaultMorphWeights.Length);
                        uniforms.MorphWeightOffset = (uint)weightOffset;
                        uniforms.MorphDeltaBase = layout.Base;
                        uniforms.MorphVertexCount = (uint)primitive.VertexCount;
                        uniforms.MorphChannels = layout.Channels;
                        uniforms.MorphStride = layout.Stride;
                    }
                }

                MemoryMarshal.Write(slots.AsSpan(index * path.drawSlotSize), in uniforms);
                bool blend = (features & PbrFeatures.AlphaBlend) != 0;
                if (blend) BlendDraws++;
                Draws[index] = new PbrDraw(primitive, material, features, item.Node, skinned, blend, (primitive.BoundsMin + primitive.BoundsMax) * 0.5f);
            }

            drawUniforms = path.context.CreateBuffer<byte>(slots, BufferUsage.Uniform | BufferUsage.CopyDst, label + " draws");
            if (deltas.Count > 0) morphDeltas = path.context.CreateBuffer<Vector4>(CollectionsMarshal.AsSpan(deltas), BufferUsage.Storage, label + " morph deltas");
            GpuBuffer skinStream = model.SkinBuffer ?? path.emptyStorage;
            Group = path.context.CreateBindGroup(
                path.modelLayout,
                label + " draws",
                BindingEntry.ForBuffer(0, drawUniforms, 0, (ulong)Unsafe.SizeOf<PbrDrawUniforms>()),
                BindingEntry.ForBuffer(1, skinStream),
                BindingEntry.ForBuffer(2, morphDeltas ?? path.emptyStorage));
        }

        public Model Model { get; }

        public PbrDraw[] Draws { get; }

        public int BlendDraws { get; }

        public GpuBindGroup Group { get; }

        public void Dispose()
        {
            Group.Dispose();
            drawUniforms.Dispose();
            morphDeltas?.Dispose();
            foreach (PbrMaterial material in materials) material.Dispose();
            fallback.Dispose();
        }

        /// <summary>Appends a primitive's deltas (target-major, then vertex, then the present channels) and returns where they start.</summary>
        private static (uint Base, uint Channels, uint Stride) PackMorph(ModelPrimitive primitive, List<Vector4> deltas)
        {
            uint channels = 0;
            foreach (ModelMorphTarget target in primitive.MorphTargets)
            {
                if (target.PositionDeltas.Length > 0) channels |= 1;
                if (target.NormalDeltas.Length > 0) channels |= 2;
                if (target.TangentDeltas.Length > 0) channels |= 4;
            }

            uint stride = (uint)BitOperations.PopCount(channels);
            if (stride == 0) return (0, 0, 0);
            uint start = (uint)deltas.Count;
            int vertices = primitive.VertexCount;
            foreach (ModelMorphTarget target in primitive.MorphTargets)
            {
                ReadOnlySpan<Vector3> positions = target.PositionDeltas.Span;
                ReadOnlySpan<Vector3> normals = target.NormalDeltas.Span;
                ReadOnlySpan<Vector3> tangents = target.TangentDeltas.Span;
                for (int vertex = 0; vertex < vertices; vertex++)
                {
                    if ((channels & 1) != 0) deltas.Add(new Vector4(vertex < positions.Length ? positions[vertex] : Vector3.Zero, 0f));
                    if ((channels & 2) != 0) deltas.Add(new Vector4(vertex < normals.Length ? normals[vertex] : Vector3.Zero, 0f));
                    if ((channels & 4) != 0) deltas.Add(new Vector4(vertex < tangents.Length ? tangents[vertex] : Vector3.Zero, 0f));
                }
            }

            return (start, channels, stride);
        }
    }

    /// <summary>A material's GPU state.</summary>
    private sealed class PbrMaterial : IDisposable
    {
        public PbrMaterial(GpuBuffer uniforms, GpuBindGroup group, PbrFeatures features)
        {
            Uniforms = uniforms;
            Group = group;
            Features = features;
        }

        public GpuBuffer Uniforms { get; }

        public GpuBindGroup Group { get; }

        public PbrFeatures Features { get; }

        public void Dispose()
        {
            Group.Dispose();
            Uniforms.Dispose();
        }
    }

    /// <summary>One draw item of a model as the path draws it.</summary>
    private readonly record struct PbrDraw(ModelPrimitive Primitive, PbrMaterial Material, PbrFeatures Features, int Node, bool Skinned, bool Blend, Vector3 Center);

    /// <summary>
    /// The instances of one model in this batch; when <paramref name="AnyMirrored" />, <c>mirrorFlags[MirrorStart +
    /// instance * draws + draw]</c> says which (instance, draw item) pairs have a mirroring model matrix.
    /// </summary>
    private readonly record struct PbrBatch(PbrModelState State, int FirstInstance, int Count, int MirrorStart, bool AnyMirrored);

    /// <summary>One blended (draw item, instance) to sort.</summary>
    private readonly record struct PbrBlendItem(int Batch, int Draw, int Instance);

    /// <summary>What is bound while recording.</summary>
    private struct PbrRecorder
    {
        public RenderPass Pass;
        public TextureFormat ColorFormat;
        public TextureFormat DepthFormat;
        public PbrFeatures Encode;
        public GpuRenderPipeline Pipeline;
        public PbrMaterial Material;
        public PbrModelState State;
    }
}

/// <summary>The frame uniforms of the model shader (400 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PbrFrameUniforms
{
    public Matrix4x4 ViewProjection;
    public Vector4 CameraPosition;
    public Vector4 AmbientSky;
    public Vector4 AmbientGround;
    public Vector4 AmbientUp;
    public Vector4 Environment;
    public PbrLightArray Lights;
}

/// <summary>Eight lights as (direction, colour) pairs.</summary>
[InlineArray(16)]
internal struct PbrLightArray
{
    private Vector4 element;
}

/// <summary>The material uniforms of the model shader (256 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PbrMaterialUniforms
{
    public Vector4 BaseColor;
    public Vector4 Emissive;
    public Vector4 Params;
    public Vector4 Extra;
    public Vector4 Specular;
    public uint Flags;
    public uint TexCoordSets;
    public uint Reserved0;
    public uint Reserved1;
    public PbrUvRows Uv;
}

/// <summary>Two texture-coordinate transform rows for each of the five texture slots.</summary>
[InlineArray(10)]
internal struct PbrUvRows
{
    private Vector4 element;
}

/// <summary>One instance record in the instance storage buffer (80 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PbrInstance
{
    public Matrix4x4 World;
    public uint NodeBase;
    public uint JointBase;
    public uint WeightBase;
    public uint Reserved;
}

/// <summary>The per-draw-item uniforms (48 bytes, one dynamic-offset slot each).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PbrDrawUniforms
{
    public uint Node;
    public uint BaseVertex;
    public uint SkinBase;
    public uint JointOffset;
    public uint MorphTargets;
    public uint MorphWeightOffset;
    public uint MorphDeltaBase;
    public uint MorphVertexCount;
    public uint MorphChannels;
    public uint MorphStride;
    public uint Reserved0;
    public uint Reserved1;
}
