using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Morph targets on the GPU: per-instance weights relative to the default weights baked into the vertex buffer, for
/// positions and normals, several instances in one draw, and (opt-in, read-only) the Poly Haven cannon.
/// </summary>
public class ModelMorphTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 96;
    private const string PolyHavenVariable = "CODEBRIX_WEBGPU_POLYHAVEN";
    private readonly GraphicsGpuFixture gpu;

    public ModelMorphTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    /// <summary>A white quad with target 0 lifting it by +1 in Y and target 1 tilting its normal towards +X; default weights 0.5 and 0.</summary>
    private Model MorphQuad()
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.MeshPrimitive primitive = TestGltf.AddMesh(root, "quad", TestGltf.Material(root, "white", Vector4.One, 0f, 1f), TestGltf.Quad());
        Gltf.Accessor lift = Deltas(root, "lift", Vector3.UnitY);
        Gltf.Accessor tilt = Deltas(root, "tilt", new Vector3(0.8f, 0f, -0.4f));
        primitive.SetMorphTargetAccessors(0, new Dictionary<string, Gltf.Accessor> { ["POSITION"] = lift });
        primitive.SetMorphTargetAccessors(1, new Dictionary<string, Gltf.Accessor> { ["NORMAL"] = tilt });
        primitive.LogicalParent.SetMorphWeights(new[] { 0.5f, 0f });
        return TestGltf.Load(gpu.Context, root, "morph quad");
    }

    /// <summary>A morph accessor giving every one of the quad's four vertices the same delta.</summary>
    private static Gltf.Accessor Deltas(Gltf.ModelRoot root, string name, Vector3 delta)
    {
        Gltf.Accessor accessor = root.CreateAccessor(name);
        accessor.SetZeros(4, CodeBrix.Graphics3D.Gltf2.Memory.AttributeFormat.Float3);
        accessor.CreateSparseData(new Dictionary<int, Vector3> { [0] = delta, [1] = delta, [2] = delta, [3] = delta });
        return accessor;
    }

    private byte[] Render(ModelRenderer renderer, Camera camera, SceneLighting lighting, params ModelInstance[] instances) =>
        ModelPbrTests.Render(gpu.Context, renderer, camera, lighting, Size, TextureFormat.RGBA8UnormSrgb, Color4.Black, instances);

    private static bool Covered(byte[] pixels, Vector3 screen) =>
        TestPixels.At(pixels, Size, (int)screen.X, (int)screen.Y) != ((byte)0, (byte)0, (byte)0, (byte)255);

    [Fact]
    public void position_weights_apply_per_instance_relative_to_the_baked_default()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = MorphQuad();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        ModelInstance lowered = quad.CreateInstance(Matrix4x4.CreateTranslation(-2, 0, 0));
        ModelInstance raised = quad.CreateInstance(Matrix4x4.CreateTranslation(2, 0, 0));
        lowered.SetMorphWeight(0, 0, 0f);
        raised.SetMorphWeight(0, 0, 1f);
        Camera camera = ModelPbrTests.Front(8, Vector3.Zero);

        //Act
        byte[] pixels = Render(renderer, camera, new SceneLighting { Ambient = AmbientLighting.Hemisphere, AmbientColor = Color4.White }, lowered, raised);

        //Assert - weight 0 spans y -1..1, weight 1 spans y 0..2 (the vertex buffer holds the 0.5 shape).
        quad.Meshes[0].DefaultMorphWeights.ToArray().Should().Equal(0.5f, 0f);
        renderer.LastDrawCallCount.Should().Be(1);
        Covered(pixels, camera.WorldToScreen(new Vector3(-2, -0.8f, 0), Size, Size)).Should().BeTrue();
        Covered(pixels, camera.WorldToScreen(new Vector3(-2, 1.6f, 0), Size, Size)).Should().BeFalse();
        Covered(pixels, camera.WorldToScreen(new Vector3(2, -0.4f, 0), Size, Size)).Should().BeFalse();
        Covered(pixels, camera.WorldToScreen(new Vector3(2, 1.8f, 0), Size, Size)).Should().BeTrue();
    }

    [Fact]
    public void normal_weights_turn_the_shading_normal()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = MorphQuad();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        ModelInstance flat = quad.CreateInstance();
        ModelInstance tilted = quad.CreateInstance();
        tilted.SetMorphWeight(0, 1, 1f);
        Camera camera = ModelPbrTests.Front(4, Vector3.Zero);
        SceneLighting fromRight = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = new Vector3(-1f, 0f, -0.2f), AmbientColor = Color4.Black };

        //Act
        double flatLuma = TestPixels.Luma(Render(renderer, camera, fromRight, flat), Size, Size / 2, Size / 2 + 10);
        double tiltedLuma = TestPixels.Luma(Render(renderer, camera, fromRight, tilted), Size, Size / 2, Size / 2 + 10);

        //Assert - the morphed normal (0.8, 0, 0.6) faces the light far more than +Z does.
        tiltedLuma.Should().BeGreaterThan(flatLuma + 60);
    }

    [Fact]
    public void polyhaven_cannon_morph_target_moves_pixels()
    {
        //Arrange
        string folder = Environment.GetEnvironmentVariable(PolyHavenVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "Opt-in: set " + PolyHavenVariable + " to a Poly Haven download folder (one sub-folder per model) to run the cannon_01 morph test.");
        string file = Path.Combine(folder, "cannon_01", "cannon_01_2k.gltf");
        Assert.SkipUnless(File.Exists(file), "cannon_01 is not in " + folder + ".");
        gpu.RequireDevice();
        using Model cannon = gpu.Context.LoadModel(file);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        int morphNode = -1;
        for (int node = 0; node < cannon.Nodes.Count && morphNode < 0; node++)
        {
            if (cannon.Nodes[node].DefaultMorphWeights.Length > 0) morphNode = node;
        }

        morphNode.Should().BeGreaterThanOrEqualTo(0);
        ModelPrimitive primitive = cannon.Meshes[cannon.Nodes[morphNode].MeshIndex].Primitives[0];
        ModelVertex[] vertices = cannon.VertexBuffer.Read<ModelVertex>(cannon.VertexCount);
        ReadOnlySpan<Vector3> deltas = primitive.MorphTargets[0].PositionDeltas.Span;
        Vector3 sum = Vector3.Zero;
        int moved = 0;
        for (int vertex = 0; vertex < deltas.Length; vertex++)
        {
            if (deltas[vertex].LengthSquared() < 1e-8f) continue;
            sum += vertices[primitive.BaseVertex + vertex].Position;
            moved++;
        }

        Vector3 focus = Vector3.Transform(sum / moved, cannon.CreateInstance().NodeMatrices[morphNode]);
        Camera camera = new Camera { Position = focus + new Vector3(0.12f, 0.08f, 0.16f), Target = focus, NearPlane = 0.01f, FarPlane = 100 };
        ModelInstance rest = cannon.CreateInstance();
        ModelInstance morphed = cannon.CreateInstance();
        morphed.SetMorphWeight(morphNode, 0, 1f);

        //Act
        byte[] restPixels = ModelPbrTests.Render(gpu.Context, renderer, camera, null, 256, TextureFormat.RGBA8UnormSrgb, Color4.Black, rest);
        byte[] morphedPixels = ModelPbrTests.Render(gpu.Context, renderer, camera, null, 256, TextureFormat.RGBA8UnormSrgb, Color4.Black, morphed);

        //Assert
        moved.Should().BeGreaterThan(0);
        int changed = 0;
        for (int index = 0; index < restPixels.Length; index += 4)
        {
            if (Math.Abs(restPixels[index] - morphedPixels[index]) + Math.Abs(restPixels[index + 1] - morphedPixels[index + 1]) > 30) changed++;
        }

        changed.Should().BeGreaterThan(50);
    }
}
