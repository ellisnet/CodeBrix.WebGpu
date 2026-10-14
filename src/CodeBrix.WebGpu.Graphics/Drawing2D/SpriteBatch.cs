using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using CodeBrix.WebGpu.Graphics.Shaders;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A batched 2D renderer for sprites and shapes: textured quads (whole textures or atlas regions), rectangles, lines,
/// circles, triangles and quads, with a transform stack and per-item colours. Coordinates are pixels with the origin
/// at the target's top-left corner and y pointing down. Everything between <see cref="Begin" /> and <see cref="End" />
/// becomes ONE indexed draw per run of the same texture - shapes never break a run, so sprites from one atlas and any
/// number of shapes cost a single draw.
/// </summary>
/// <remarks>
/// Several batches (or several Begin/End pairs) may draw in one <see cref="GraphicsFrame" />: each keeps its data in
/// its own region of the batch's buffers, reset when a new frame starts. Not thread-safe.
/// </remarks>
public sealed class SpriteBatch : GraphicsResource
{
    private const int UniformSlotSize = 256;
    private const int VertexSize = 24;

    private readonly Dictionary<(TextureFormat Color, TextureFormat Depth, BlendPreset Blend), GpuRenderPipeline> pipelines = new Dictionary<(TextureFormat, TextureFormat, BlendPreset), GpuRenderPipeline>();
    private readonly Dictionary<(GpuTexture Texture, bool Nearest), GpuBindGroup> textureGroups = new Dictionary<(GpuTexture, bool), GpuBindGroup>();
    private readonly List<(GpuTexture Texture, int FirstQuad, int QuadCount)> runs = new List<(GpuTexture, int, int)>();
    private readonly Stack<Matrix3x2> transforms = new Stack<Matrix3x2>();
    private readonly GpuShader shader;
    private readonly GpuBindGroupLayout globalsLayout;
    private readonly GpuBindGroupLayout textureLayout;
    private readonly GpuSampler linearSampler;
    private readonly GpuSampler nearestSampler;
    private GpuBuffer uniforms;
    private GpuBindGroup uniformGroup;
    private GpuBuffer vertexBuffer;
    private GpuBuffer indexBuffer;
    private SpriteVertex[] vertices;
    private int vertexCount;
    private int uniformCapacity;
    private int indexQuadCapacity;
    private long frameId = -1;
    private int uniformSlot;
    private ulong vertexCursor;
    private RenderPass pass;
    private BlendPreset blend;
    private Matrix4x4 projection;

    /// <summary>Creates a batch (pipelines are built lazily, one per target format combination).</summary>
    /// <param name="context">The context.</param>
    /// <param name="initialQuadCapacity">Quads the buffers hold before they grow (default 2048).</param>
    public SpriteBatch(GraphicsContext context, int initialQuadCapacity = 2048)
        : base(context, "sprite batch")
    {
        int quads = Math.Max(64, initialQuadCapacity);
        shader = context.CreateShader(BuiltInShaders.Sprite, "sprite batch");
        globalsLayout = context.CreateBindGroupLayout("sprite globals", BindingLayoutEntry.Uniform(0, ShaderStage.Vertex, true, 64));
        textureLayout = context.CreateBindGroupLayout("sprite texture", BindingLayoutEntry.Texture(0, ShaderStage.Fragment), BindingLayoutEntry.Sampler(1, ShaderStage.Fragment));
        linearSampler = context.CreateSampler(SamplerDescription.LinearClamp);
        nearestSampler = context.CreateSampler(SamplerDescription.NearestClamp);
        vertices = new SpriteVertex[quads * 4];
        CreateUniforms(16);
        vertexBuffer = context.CreateBuffer((ulong)(quads * 4 * VertexSize), BufferUsage.Vertex, "sprite vertices");
        CreateIndices(quads);
    }

    /// <summary>Sample textures with nearest-texel filtering (pixel art, exact atlases) instead of linear. Default false.</summary>
    public bool UseNearestFiltering { get; set; }

    /// <summary>True between <see cref="Begin" /> and <see cref="End" />.</summary>
    public bool IsDrawing => pass != null;

    /// <summary>The current transform (applied to every position before the projection).</summary>
    public Matrix3x2 Transform => transforms.Count == 0 ? Matrix3x2.Identity : transforms.Peek();

    /// <summary>How many draw calls the last <see cref="End" /> issued.</summary>
    public int LastDrawCallCount { get; private set; }

    /// <summary>How many quads (shapes count as quads; a triangle is one) the last <see cref="End" /> drew.</summary>
    public int LastQuadCount { get; private set; }

    /// <summary>
    /// Starts collecting into a render pass. The projection maps the pass target's pixels (top-left origin, y down).
    /// </summary>
    /// <param name="renderPass">The pass the batch draws into at <see cref="End" />.</param>
    /// <param name="transform">An initial transform (default identity).</param>
    /// <param name="blendPreset">How items blend (default straight alpha).</param>
    /// <exception cref="InvalidOperationException">The batch is already drawing.</exception>
    public void Begin(RenderPass renderPass, Matrix3x2? transform = null, BlendPreset blendPreset = BlendPreset.AlphaBlend)
    {
        ArgumentNullException.ThrowIfNull(renderPass);
        ThrowIfDisposed();
        if (pass != null) throw new InvalidOperationException("Begin was called twice without End.");
        if (renderPass.IsEnded) throw new InvalidOperationException("The render pass has ended.");
        pass = renderPass;
        blend = blendPreset;
        if (renderPass.Frame.Id != frameId)
        {
            frameId = renderPass.Frame.Id;
            uniformSlot = 0;
            vertexCursor = 0;
        }

        RenderTargetView target = renderPass.Target;
        projection = Matrix4x4.CreateOrthographicOffCenter(0, Math.Max(1, target.Width), Math.Max(1, target.Height), 0, -1, 1);
        transforms.Clear();
        transforms.Push(transform ?? Matrix3x2.Identity);
        vertexCount = 0;
        runs.Clear();
    }

    /// <summary>Pushes a transform, combined with the current one (the new one applies first).</summary>
    /// <param name="transform">The transform.</param>
    public void PushTransform(Matrix3x2 transform)
    {
        ThrowIfNotDrawing();
        transforms.Push(transform * transforms.Peek());
    }

    /// <summary>Pops the last pushed transform.</summary>
    /// <exception cref="InvalidOperationException">Only the initial transform is left.</exception>
    public void PopTransform()
    {
        ThrowIfNotDrawing();
        if (transforms.Count <= 1) throw new InvalidOperationException("PopTransform without a matching PushTransform.");
        transforms.Pop();
    }

    /// <summary>Draws a whole texture into a rectangle.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="destination">The rectangle, in pixels.</param>
    /// <param name="tint">Multiplies the texture (white = unchanged).</param>
    public void Draw(GpuTexture texture, RectangleF destination, Color4 tint)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Draw(texture, destination, new RectangleF(0, 0, texture.Width, texture.Height), tint);
    }

    /// <summary>Draws a region of a texture (an atlas cell, in texels) into a rectangle.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="destination">The rectangle, in pixels.</param>
    /// <param name="source">The region, in texels.</param>
    /// <param name="tint">Multiplies the texture.</param>
    public void Draw(GpuTexture texture, RectangleF destination, RectangleF source, Color4 tint)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ThrowIfNotDrawing();
        Vector2 uv0 = new Vector2(source.Left / texture.Width, source.Top / texture.Height);
        Vector2 uv1 = new Vector2(source.Right / texture.Width, source.Bottom / texture.Height);
        AddQuad(texture,
            new Vector2(destination.Left, destination.Top), new Vector2(destination.Right, destination.Top),
            new Vector2(destination.Right, destination.Bottom), new Vector2(destination.Left, destination.Bottom),
            uv0, new Vector2(uv1.X, uv0.Y), uv1, new Vector2(uv0.X, uv1.Y), tint.ToPackedRgba(), 1f);
    }

    /// <summary>Draws a region of a texture positioned, rotated and scaled about an origin.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="position">Where the origin lands, in pixels.</param>
    /// <param name="source">The region, in texels.</param>
    /// <param name="tint">Multiplies the texture.</param>
    /// <param name="rotation">Clockwise rotation in radians (y points down).</param>
    /// <param name="origin">The pivot within the region, in texels from its top-left corner.</param>
    /// <param name="scale">The scale.</param>
    public void Draw(GpuTexture texture, Vector2 position, RectangleF source, Color4 tint, float rotation, Vector2 origin, Vector2 scale)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ThrowIfNotDrawing();
        Matrix3x2 local = Matrix3x2.CreateTranslation(-origin) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(rotation) * Matrix3x2.CreateTranslation(position);
        Vector2 a = Vector2.Transform(Vector2.Zero, local);
        Vector2 b = Vector2.Transform(new Vector2(source.Width, 0), local);
        Vector2 c = Vector2.Transform(new Vector2(source.Width, source.Height), local);
        Vector2 d = Vector2.Transform(new Vector2(0, source.Height), local);
        Vector2 uv0 = new Vector2(source.Left / texture.Width, source.Top / texture.Height);
        Vector2 uv1 = new Vector2(source.Right / texture.Width, source.Bottom / texture.Height);
        AddQuad(texture, a, b, c, d, uv0, new Vector2(uv1.X, uv0.Y), uv1, new Vector2(uv0.X, uv1.Y), tint.ToPackedRgba(), 1f);
    }

    /// <summary>Draws the frame of an animated texture shown after <paramref name="elapsed" /> into a rectangle.</summary>
    /// <param name="texture">The animated texture.</param>
    /// <param name="elapsed">The playback time (see <see cref="AnimatedTexture.GetFrameIndex" />).</param>
    /// <param name="destination">The rectangle, in pixels.</param>
    /// <param name="tint">Multiplies the texture.</param>
    public void Draw(AnimatedTexture texture, TimeSpan elapsed, RectangleF destination, Color4 tint)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Draw(texture.GetFrameTexture(elapsed), destination, tint);
    }

    /// <summary>Draws the current frame of an animated texture positioned, rotated and scaled about an origin.</summary>
    /// <param name="texture">The animated texture.</param>
    /// <param name="elapsed">The playback time.</param>
    /// <param name="position">Where the origin lands, in pixels.</param>
    /// <param name="tint">Multiplies the texture.</param>
    /// <param name="rotation">Clockwise rotation in radians (y points down).</param>
    /// <param name="origin">The pivot, in texels from the frame's top-left corner.</param>
    /// <param name="scale">The scale.</param>
    public void Draw(AnimatedTexture texture, TimeSpan elapsed, Vector2 position, Color4 tint, float rotation, Vector2 origin, Vector2 scale)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Draw(texture.GetFrameTexture(elapsed), position, new RectangleF(0, 0, texture.Width, texture.Height), tint, rotation, origin, scale);
    }

    /// <summary>Fills a rectangle.</summary>
    /// <param name="rectangle">The rectangle, in pixels.</param>
    /// <param name="color">The colour.</param>
    public void FillRectangle(RectangleF rectangle, Color4 color)
    {
        FillQuad(new Vector2(rectangle.Left, rectangle.Top), new Vector2(rectangle.Right, rectangle.Top), new Vector2(rectangle.Right, rectangle.Bottom), new Vector2(rectangle.Left, rectangle.Bottom), color);
    }

    /// <summary>Outlines a rectangle (the outline lies inside it).</summary>
    /// <param name="rectangle">The rectangle, in pixels.</param>
    /// <param name="color">The colour.</param>
    /// <param name="thickness">The line thickness in pixels.</param>
    public void DrawRectangle(RectangleF rectangle, Color4 color, float thickness = 1f)
    {
        float t = Math.Min(thickness, Math.Min(rectangle.Width, rectangle.Height) / 2f);
        FillRectangle(new RectangleF(rectangle.Left, rectangle.Top, rectangle.Width, t), color);
        FillRectangle(new RectangleF(rectangle.Left, rectangle.Bottom - t, rectangle.Width, t), color);
        FillRectangle(new RectangleF(rectangle.Left, rectangle.Top + t, t, rectangle.Height - (2 * t)), color);
        FillRectangle(new RectangleF(rectangle.Right - t, rectangle.Top + t, t, rectangle.Height - (2 * t)), color);
    }

    /// <summary>Draws a line segment.</summary>
    /// <param name="from">The start, in pixels.</param>
    /// <param name="to">The end, in pixels.</param>
    /// <param name="color">The colour.</param>
    /// <param name="thickness">The thickness in pixels.</param>
    public void DrawLine(Vector2 from, Vector2 to, Color4 color, float thickness = 1f)
    {
        Vector2 direction = to - from;
        float length = direction.Length();
        if (length <= float.Epsilon) return;
        Vector2 normal = new Vector2(-direction.Y, direction.X) / length * (thickness / 2f);
        FillQuad(from + normal, to + normal, to - normal, from - normal, color);
    }

    /// <summary>Fills a triangle.</summary>
    /// <param name="a">A corner.</param>
    /// <param name="b">A corner.</param>
    /// <param name="c">A corner.</param>
    /// <param name="color">The colour.</param>
    public void FillTriangle(Vector2 a, Vector2 b, Vector2 c, Color4 color) => FillQuad(a, b, c, c, color);

    /// <summary>Fills a convex quadrilateral (corners in order around it).</summary>
    /// <param name="a">The first corner.</param>
    /// <param name="b">The second corner.</param>
    /// <param name="c">The third corner.</param>
    /// <param name="d">The fourth corner.</param>
    /// <param name="color">The colour.</param>
    public void FillQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color4 color)
    {
        ThrowIfNotDrawing();
        AddQuad(null, a, b, c, d, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, color.ToPackedRgba(), 0f);
    }

    /// <summary>Fills a circle.</summary>
    /// <param name="center">The centre, in pixels.</param>
    /// <param name="radius">The radius, in pixels.</param>
    /// <param name="color">The colour.</param>
    /// <param name="segments">The number of segments (0 = chosen from the radius).</param>
    public void FillCircle(Vector2 center, float radius, Color4 color, int segments = 0)
    {
        int count = SegmentsFor(radius, segments);
        Vector2 previous = center + new Vector2(radius, 0);
        for (int index = 1; index <= count; index++)
        {
            float angle = index * MathF.Tau / count;
            Vector2 next = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            FillTriangle(center, previous, next, color);
            previous = next;
        }
    }

    /// <summary>Outlines a circle (the outline is centred on the radius).</summary>
    /// <param name="center">The centre, in pixels.</param>
    /// <param name="radius">The radius, in pixels.</param>
    /// <param name="color">The colour.</param>
    /// <param name="thickness">The thickness in pixels.</param>
    /// <param name="segments">The number of segments (0 = chosen from the radius).</param>
    public void DrawCircle(Vector2 center, float radius, Color4 color, float thickness = 1f, int segments = 0)
    {
        int count = SegmentsFor(radius, segments);
        float inner = Math.Max(0, radius - (thickness / 2f));
        float outer = radius + (thickness / 2f);
        for (int index = 0; index < count; index++)
        {
            float a0 = index * MathF.Tau / count;
            float a1 = (index + 1) * MathF.Tau / count;
            Vector2 d0 = new Vector2(MathF.Cos(a0), MathF.Sin(a0));
            Vector2 d1 = new Vector2(MathF.Cos(a1), MathF.Sin(a1));
            FillQuad(center + (d0 * outer), center + (d1 * outer), center + (d1 * inner), center + (d0 * inner), color);
        }
    }

    /// <summary>Uploads everything collected since <see cref="Begin" /> and records the draws into the pass.</summary>
    /// <exception cref="InvalidOperationException">The batch is not drawing, or the pass has ended.</exception>
    public void End()
    {
        ThrowIfNotDrawing();
        RenderPass current = pass;
        pass = null;
        LastDrawCallCount = 0;
        LastQuadCount = vertexCount / 4;
        if (vertexCount == 0) return;
        if (current.IsEnded) throw new InvalidOperationException("The render pass ended before the batch.");

        int quads = vertexCount / 4;
        ulong bytes = (ulong)(vertexCount * VertexSize);
        if (vertexCursor + bytes > vertexBuffer.Size)
        {
            ulong size = Math.Max(vertexBuffer.Size * 2, vertexCursor + bytes);
            vertexBuffer.Dispose();
            vertexBuffer = Context.CreateBuffer(size, BufferUsage.Vertex, "sprite vertices");
            vertexCursor = 0;
        }

        if (quads > indexQuadCapacity) CreateIndices(Math.Max(quads, indexQuadCapacity * 2));
        if (uniformSlot >= uniformCapacity) CreateUniforms(uniformCapacity * 2);

        Matrix4x4 matrix = projection;
        uniforms.WriteValue(in matrix, (ulong)(uniformSlot * UniformSlotSize));
        vertexBuffer.Write(new ReadOnlySpan<SpriteVertex>(vertices, 0, vertexCount), vertexCursor);

        RenderTargetView target = current.Target;
        current.SetPipeline(Pipeline(target.ColorFormat, target.DepthFormat, blend));
        Span<uint> offsets = stackalloc uint[1] { (uint)(uniformSlot * UniformSlotSize) };
        current.SetBindGroup(0, uniformGroup, offsets);
        current.SetVertexBuffer(0, vertexBuffer, vertexCursor, bytes);
        current.SetIndexBuffer(indexBuffer, IndexFormat.Uint32);
        foreach ((GpuTexture texture, int firstQuad, int quadCount) in runs)
        {
            current.SetBindGroup(1, TextureGroup(texture));
            current.DrawIndexed((uint)(quadCount * 6), 1, (uint)(firstQuad * 6), 0, 0);
            LastDrawCallCount++;
        }

        uniformSlot++;
        vertexCursor += bytes;
        vertexCount = 0;
        runs.Clear();
    }

    /// <summary>Forgets the cached bind group of a texture (call after disposing a texture the batch drew, to free it sooner).</summary>
    /// <param name="texture">The texture.</param>
    public void Forget(GpuTexture texture)
    {
        foreach (bool nearest in new[] { false, true })
        {
            if (textureGroups.Remove((texture, nearest), out GpuBindGroup group)) group.Dispose();
        }
    }

    private protected override void ReleaseNative()
    {
        foreach (GpuBindGroup group in textureGroups.Values) group.Dispose();
        textureGroups.Clear();
        foreach (GpuRenderPipeline pipeline in pipelines.Values) pipeline.Dispose();
        pipelines.Clear();
        uniformGroup?.Dispose();
        uniforms?.Dispose();
        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();
        linearSampler.Dispose();
        nearestSampler.Dispose();
        textureLayout.Dispose();
        globalsLayout.Dispose();
        shader.Dispose();
    }

    private void AddQuad(GpuTexture texture, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD, uint color, float textured)
    {
        GpuTexture runTexture = texture ?? (runs.Count > 0 ? runs[^1].Texture : Context.WhiteTexture);
        int quadIndex = vertexCount / 4;
        if (runs.Count == 0 || !ReferenceEquals(runs[^1].Texture, runTexture))
        {
            runs.Add((runTexture, quadIndex, 1));
        }
        else
        {
            (GpuTexture Texture, int FirstQuad, int QuadCount) last = runs[^1];
            runs[^1] = (last.Texture, last.FirstQuad, last.QuadCount + 1);
        }

        if (vertexCount + 4 > vertices.Length) Array.Resize(ref vertices, vertices.Length * 2);
        Matrix3x2 m = transforms.Peek();
        vertices[vertexCount++] = new SpriteVertex(Vector2.Transform(a, m), uvA, color, textured);
        vertices[vertexCount++] = new SpriteVertex(Vector2.Transform(b, m), uvB, color, textured);
        vertices[vertexCount++] = new SpriteVertex(Vector2.Transform(c, m), uvC, color, textured);
        vertices[vertexCount++] = new SpriteVertex(Vector2.Transform(d, m), uvD, color, textured);
    }

    private GpuRenderPipeline Pipeline(TextureFormat color, TextureFormat depth, BlendPreset preset)
    {
        if (pipelines.TryGetValue((color, depth, preset), out GpuRenderPipeline pipeline)) return pipeline;
        RenderPipelineBuilder builder = Context.CreateRenderPipeline(shader)
            .WithLabel("sprite batch " + color + " " + preset)
            .WithVertexBuffer<SpriteVertex>()
            .WithColorTarget(color, preset)
            .WithBindGroupLayouts(globalsLayout, textureLayout);
        if (depth != TextureFormat.Undefined) builder.WithDepth(depth, DepthPreset.None);
        pipeline = builder.Build();
        pipelines[(color, depth, preset)] = pipeline;
        return pipeline;
    }

    private GpuBindGroup TextureGroup(GpuTexture texture)
    {
        bool nearest = UseNearestFiltering;
        if (textureGroups.TryGetValue((texture, nearest), out GpuBindGroup group) && !group.IsDisposed) return group;
        if (texture.IsDisposed) throw new ObjectDisposedException(texture.ToString(), "A texture drawn by the batch was disposed before End.");
        group = Context.CreateBindGroup(textureLayout, "sprite texture", BindingEntry.ForTexture(0, texture), BindingEntry.ForSampler(1, nearest ? nearestSampler : linearSampler));
        textureGroups[(texture, nearest)] = group;
        return group;
    }

    private void CreateUniforms(int slots)
    {
        uniformGroup?.Dispose();
        uniforms?.Dispose();
        uniformCapacity = slots;
        uniforms = Context.CreateBuffer((ulong)(slots * UniformSlotSize), BufferUsage.Uniform, "sprite globals");
        uniformGroup = Context.CreateBindGroup(globalsLayout, "sprite globals", BindingEntry.ForBuffer(0, uniforms, 0, 64));
        uniformSlot = 0;
    }

    private void CreateIndices(int quads)
    {
        indexBuffer?.Dispose();
        uint[] indices = new uint[quads * 6];
        for (int quad = 0; quad < quads; quad++)
        {
            uint vertex = (uint)(quad * 4);
            int index = quad * 6;
            indices[index] = vertex;
            indices[index + 1] = vertex + 1;
            indices[index + 2] = vertex + 2;
            indices[index + 3] = vertex;
            indices[index + 4] = vertex + 2;
            indices[index + 5] = vertex + 3;
        }

        indexBuffer = Context.CreateIndexBuffer(indices, "sprite indices");
        indexQuadCapacity = quads;
    }

    private static int SegmentsFor(float radius, int segments) => segments > 2 ? segments : Math.Clamp((int)(radius * 0.75f), 12, 96);

    private void ThrowIfNotDrawing()
    {
        ThrowIfDisposed();
        if (pass == null) throw new InvalidOperationException("Call Begin first.");
    }
}
