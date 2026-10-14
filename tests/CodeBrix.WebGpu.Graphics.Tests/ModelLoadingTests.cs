using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// glTF models loaded onto the GPU: the committed CC0 assets (Kenney Mini Forest tree and archer, Prototype Kit
/// indicator, Retro Urban Kit grass, Poly Haven wet floor sign) and a model built in the test (texture transform with a
/// TEXCOORD_1 override, alpha mask, sparse morph target with a default weight), drawn with the model renderer.
/// </summary>
public class ModelLoadingTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 64;
    private static readonly Color4 Background = Color4.FromBytes(20, 30, 40);
    private readonly GraphicsGpuFixture gpu;

    public ModelLoadingTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    /// <summary>True when a pixel is the clear colour as an sRGB target stores it (the clear value is encoded).</summary>
    private static bool IsBackground((byte R, byte G, byte B, byte A) pixel) =>
        TestPixels.Near(pixel, Encode(Background.R), Encode(Background.G), Encode(Background.B), 2);

    private static int Encode(float linear) => (int)Math.Round(255 * (linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f));

    private static string Asset(string relative) => Path.Combine(AppContext.BaseDirectory, "Assets", relative);

    private static Camera Front(float height, Vector3 target) => new Camera
    {
        Projection = ProjectionKind.Orthographic,
        OrthographicHeight = height,
        Position = target + new Vector3(0, 0, 20),
        Target = target,
        NearPlane = 0.1f,
        FarPlane = 100,
    };

    private byte[] Render(Camera camera, SceneLighting lighting, ModelRenderer renderer, params ModelInstance[] instances)
    {
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Background))
            {
                renderer.Begin(pass, camera, lighting);
                foreach (ModelInstance instance in instances) renderer.Draw(instance);
                renderer.End();
            }

            frame.Submit();
        }

        context.TakeErrors().Should().BeEmpty();
        return target.ReadRgba();
    }

    /// <summary>A 2 x 2 quad (z = 0) whose base-colour texture is sampled through TEXCOORD_1 with an alpha mask, plus a sparse morph target that lifts it by 1 at default weight 0.5.</summary>
    private static byte[] BuildQuadGlb()
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        using Image<Rgba32> image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(255, 0, 0, 255);
        image[1, 0] = new Rgba32(0, 255, 0, 0);
        using MemoryStream png = new MemoryStream();
        image.SaveAsPng(png);
        Gltf.Image gltfImage = root.UseImage(new CodeBrix.Graphics3D.Gltf2.Memory.MemoryImage(png.ToArray()));
        Gltf.TextureSampler sampler = root.UseTextureSampler(Gltf.TextureWrapMode.CLAMP_TO_EDGE, Gltf.TextureWrapMode.CLAMP_TO_EDGE, Gltf.TextureMipMapFilter.NEAREST, Gltf.TextureInterpolationFilter.NEAREST);
        Gltf.Texture texture = root.UseTexture(gltfImage, sampler);
        Gltf.Material material = Gltf.Toolkit.WithPBRMetallicRoughness(root.CreateMaterial("masked"));
        material.Alpha = Gltf.AlphaMode.MASK;
        material.AlphaCutoff = 0.5f;
        material.DoubleSided = true;
        Gltf.MaterialChannel channel = material.FindChannel("BaseColor").Value;
        channel.SetTexture(0, texture);
        channel.SetTransform(Vector2.Zero, Vector2.One, 0f, 1);

        Gltf.Mesh mesh = root.CreateMesh("quad");
        Gltf.MeshPrimitive primitive = mesh.CreatePrimitive();
        Gltf.Toolkit.WithVertexAccessor(primitive, "POSITION", new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) });
        Gltf.Toolkit.WithVertexAccessor(primitive, "NORMAL", new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ });
        Gltf.Toolkit.WithVertexAccessor(primitive, "TEXCOORD_0", new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero });
        Gltf.Toolkit.WithVertexAccessor(primitive, "TEXCOORD_1", new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) });
        Gltf.Toolkit.WithIndicesAccessor(primitive, Gltf.PrimitiveType.TRIANGLES, new[] { 0, 1, 2, 0, 2, 3 });
        Gltf.Toolkit.WithMaterial(primitive, material);
        Gltf.Accessor delta = root.CreateAccessor("lift");
        delta.SetZeros(4, CodeBrix.Graphics3D.Gltf2.Memory.AttributeFormat.Float3);
        delta.CreateSparseData(new Dictionary<int, Vector3> { [0] = Vector3.UnitY, [1] = Vector3.UnitY, [2] = Vector3.UnitY, [3] = Vector3.UnitY });
        primitive.SetMorphTargetAccessors(0, new Dictionary<string, Gltf.Accessor> { ["POSITION"] = delta });
        mesh.SetMorphWeights(new[] { 0.5f });
        root.UseScene(0).CreateNode("quad").Mesh = mesh;
        return root.WriteGLB().ToArray();
    }

    [Fact]
    public void kenney_tree_loads_with_a_shared_pool_a_transformed_srgb_colormap_and_draws()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using Model tree = gpu.Context.LoadModel(Asset("kenney-mini-forest/tree.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        ModelInstance instance = tree.CreateInstance(Matrix4x4.CreateTranslation(-tree.BoundsCenter));
        float height = (tree.BoundsMax - tree.BoundsMin).Length() * 1.2f;
        byte[] pixels = Render(Front(height, Vector3.Zero), new SceneLighting(), renderer, instance);

        //Assert
        tree.Name.Should().Be("tree");
        tree.Warnings.Should().BeEmpty();
        tree.IndexFormat.Should().Be(IndexFormat.Uint16);
        tree.TriangleCount.Should().BeGreaterThan(10);
        tree.Textures.Count.Should().Be(1);
        tree.Textures[0].Format.Should().Be(TextureFormat.RGBA8UnormSrgb);
        tree.Textures[0].MipLevelCount.Should().BeGreaterThan(1);
        ModelMaterial material = tree.Meshes[0].Primitives[0].Material;
        material.BaseColorTexture.IsPresent.Should().BeTrue();
        material.BaseColorTexture.HasTransform.Should().BeTrue();
        material.BaseColorTexture.Sampler.MinFilter.Should().Be(FilterMode.Linear);
        material.BaseColorTexture.Sampler.AddressMode.Should().Be(AddressMode.Repeat);
        tree.Meshes[0].Primitives[0].HasTangents.Should().BeTrue();
        IsBackground(TestPixels.At(pixels, Size, Size / 2, Size / 2)).Should().BeFalse();
        IsBackground(TestPixels.At(pixels, Size, 0, 0)).Should().BeTrue();
        renderer.LastDrawCallCount.Should().Be(tree.DrawItems.Length);
    }

    [Fact]
    public void instances_of_one_model_are_batched_into_one_draw_per_primitive()
    {
        //Arrange
        gpu.RequireDevice();
        using Model tree = gpu.Context.LoadModel(Asset("kenney-mini-forest/tree.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        ModelInstance[] instances = new ModelInstance[5];
        for (int index = 0; index < instances.Length; index++) instances[index] = tree.CreateInstance(Matrix4x4.CreateTranslation(index * 2 - 4, 0, 0));
        instances[4].Visible = false;

        //Act
        Render(Front(12, Vector3.Zero), null, renderer, instances);

        //Assert
        renderer.LastDrawCallCount.Should().Be(tree.DrawItems.Length);
        renderer.LastInstanceCount.Should().Be(tree.DrawItems.Length * 4);
    }

    [Fact]
    public void skinned_archer_keeps_joints_inverse_bind_matrices_texcoord1_and_clips()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        byte[] pixels = Render(Front((archer.BoundsMax - archer.BoundsMin).Length(), archer.BoundsCenter), new SceneLighting(), renderer, archer.CreateInstance());

        //Assert
        archer.Skins.Count.Should().BeGreaterThan(0);
        ModelSkin skin = archer.Skins[0];
        skin.JointCount.Should().BeGreaterThan(1);
        skin.InverseBindMatrices.Length.Should().Be(skin.JointCount);
        bool anyNonIdentity = false;
        foreach (Matrix4x4 matrix in skin.InverseBindMatrices.Span) anyNonIdentity |= !matrix.IsIdentity;
        anyNonIdentity.Should().BeTrue();
        archer.SkinBuffer.Should().NotBeNull();
        bool skinned = false;
        bool texCoord1 = false;
        foreach (ModelMesh mesh in archer.Meshes)
        {
            foreach (ModelPrimitive primitive in mesh.Primitives)
            {
                skinned |= primitive.IsSkinned;
                texCoord1 |= primitive.HasTexCoord1;
            }
        }

        skinned.Should().BeTrue();
        texCoord1.Should().BeTrue();
        archer.Animations.Count.Should().Be(32);
        archer.FindAnimation(archer.Animations[3].Name).Should().Be(3);
        archer.Animations[0].Duration.Should().BeGreaterThan(0f);
        foreach (ModelAnimation clip in archer.Animations)
        {
            foreach (ModelAnimationChannel channel in clip.Channels)
            {
                channel.Values.Length.Should().Be(channel.KeyCount * channel.ComponentCount * (channel.Interpolation == AnimationInterpolation.CubicSpline ? 3 : 1));
            }
        }

        IsBackground(TestPixels.At(pixels, Size, Size / 2, Size / 2)).Should().BeFalse();
    }

    [Fact]
    public void node_animation_channels_are_decoded_and_instances_pose_independently()
    {
        //Arrange
        gpu.RequireDevice();
        using Model indicator = gpu.Context.LoadModel(Asset("kenney-prototype-kit/indicator-round-b.glb"));
        ModelInstance first = indicator.CreateInstance();
        ModelInstance second = indicator.CreateInstance();
        ModelAnimationChannel channel = indicator.Animations[0].Channels[0];

        //Act
        Matrix4x4 before = second.NodeMatrices[channel.Node];
        using (ModelPoseEdit pose = first.BeginPoseEdit()) pose.LocalTransforms[channel.Node].Translation += new Vector3(0, 5, 0);
        Matrix4x4 moved = first.NodeMatrices[channel.Node];
        first.ResetPose();

        //Assert
        indicator.Animations.Count.Should().Be(1);
        channel.Interpolation.Should().Be(AnimationInterpolation.Linear);
        channel.KeyCount.Should().BeGreaterThan(1);
        channel.Values.Length.Should().Be(channel.KeyCount * channel.ComponentCount);
        (moved.M42 - before.M42).Should().BeApproximately(5f, 1e-4f);
        second.NodeMatrices[channel.Node].Should().Be(before);
        first.NodeMatrices[channel.Node].Should().Be(before);
    }

    [Fact]
    public void unlit_grass_ignores_the_light()
    {
        //Arrange
        gpu.RequireDevice();
        using Model grass = gpu.Context.LoadModel(Asset("kenney-retro-urban-kit/grass.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Vector3 center = grass.BoundsCenter;
        Camera above = new Camera { Projection = ProjectionKind.Orthographic, OrthographicHeight = 4, Position = center + new Vector3(0, 10, 0.001f), Target = center, NearPlane = 0.1f, FarPlane = 50 };
        SceneLighting fromBelow = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = new Vector3(0, 1, 0), AmbientColor = Color4.Black };

        //Act
        byte[] pixels = Render(above, fromBelow, renderer, grass.CreateInstance());

        //Assert
        grass.Materials[0].Unlit.Should().BeTrue();
        TestPixels.Luma(pixels, Size, Size / 2, Size / 2).Should().BeGreaterThan(40);
    }

    [Fact]
    public void polyhaven_sign_gets_srgb_and_linear_textures_generated_tangents_and_full_material_parameters()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using Model sign = gpu.Context.LoadModel(Asset("polyhaven-wet-floor-sign/WetFloorSign_01_2k.gltf"));

        //Assert
        sign.Warnings.Should().BeEmpty();
        ModelMaterial material = sign.Materials[0];
        material.BaseColorTexture.Texture.Format.Should().Be(TextureFormat.RGBA8UnormSrgb);
        material.NormalTexture.Texture.Format.Should().Be(TextureFormat.RGBA8Unorm);
        material.MetallicRoughnessTexture.Texture.Format.Should().Be(TextureFormat.RGBA8Unorm);
        material.BaseColorTexture.Texture.Width.Should().Be(2048);
        sign.Textures.Count.Should().Be(3);
        ModelPrimitive primitive = sign.Meshes[0].Primitives[0];
        primitive.TangentsGenerated.Should().BeTrue();
        primitive.HasTangents.Should().BeTrue();
        sign.SourcePath.Should().EndWith("WetFloorSign_01_2k.gltf");
    }

    [Fact]
    public void texture_cache_shares_a_colormap_between_models_and_outlives_them()
    {
        //Arrange
        gpu.RequireDevice();
        using ModelTextureCache cache = new ModelTextureCache();
        ModelLoadOptions options = new ModelLoadOptions { TextureCache = cache };

        //Act
        Model tree = gpu.Context.LoadModel(Asset("kenney-mini-forest/tree.glb"), options);
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"), options);
        GpuTexture shared = tree.Textures[0];
        tree.Dispose();

        //Assert
        cache.Count.Should().Be(1);
        archer.Textures[0].Should().BeSameAs(shared);
        shared.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void built_model_applies_texcoord1_override_alpha_mask_and_default_sparse_morph_weight()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] glb = BuildQuadGlb();
        using MemoryStream stream = new MemoryStream(glb);

        //Act
        using Model quad = gpu.Context.LoadModel(stream, null, new ModelLoadOptions { Label = "quad" });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = Front(4, Vector3.Zero);
        byte[] pixels = Render(camera, new SceneLighting { Ambient = AmbientLighting.Hemisphere, AmbientColor = Color4.White }, renderer, quad.CreateInstance());
        Vector3 left = camera.WorldToScreen(new Vector3(-0.5f, 1f, 0), Size, Size);
        Vector3 right = camera.WorldToScreen(new Vector3(0.5f, 1f, 0), Size, Size);
        Vector3 below = camera.WorldToScreen(new Vector3(-0.5f, -0.8f, 0), Size, Size);

        //Assert
        ModelPrimitive primitive = quad.Meshes[0].Primitives[0];
        primitive.Material.BaseColorTexture.TexCoord.Should().Be(1);
        primitive.Material.AlphaMode.Should().Be(MaterialAlphaMode.Mask);
        primitive.MorphTargets.Count.Should().Be(1);
        primitive.MorphTargets[0].PositionDeltas.Span[2].Should().Be(Vector3.UnitY);
        quad.Meshes[0].DefaultMorphWeights.Span[0].Should().Be(0.5f);
        primitive.BoundsMin.Y.Should().BeApproximately(-0.5f, 1e-5f);
        TestPixels.At(pixels, Size, (int)left.X, (int)left.Y).R.Should().BeGreaterThan(200);
        IsBackground(TestPixels.At(pixels, Size, (int)right.X, (int)right.Y)).Should().BeTrue();
        IsBackground(TestPixels.At(pixels, Size, (int)below.X, (int)below.Y)).Should().BeTrue();
        ModelInstance instance = quad.CreateInstance();
        instance.GetMorphWeights(0).ToArray().Should().Equal(0.5f);
    }

    [Fact]
    public void stream_loading_resolves_external_files_only_with_a_base_directory()
    {
        //Arrange
        gpu.RequireDevice();
        string gltf = Asset("polyhaven-wet-floor-sign/WetFloorSign_01_2k.gltf");

        //Act
        using FileStream withBase = File.OpenRead(gltf);
        using Model loaded = gpu.Context.LoadModel(withBase, Path.GetDirectoryName(gltf), new ModelLoadOptions { LoadTextures = false });
        using FileStream withoutBase = File.OpenRead(gltf);
        Action missing = () => gpu.Context.LoadModel(withoutBase);

        //Assert
        loaded.Textures.Count.Should().Be(0);
        loaded.TriangleCount.Should().BeGreaterThan(0);
        missing.Should().Throw<InvalidDataException>().WithMessage("*could not be read*");
    }

    [Fact]
    public void files_that_are_not_gltf_fail_with_a_clear_reason()
    {
        //Arrange
        gpu.RequireDevice();
        string file = Path.Combine(Path.GetTempPath(), "webgpu-not-a-model-" + Guid.NewGuid().ToString("N") + ".glb");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        try
        {
            //Act
            Action load = () => gpu.Context.LoadModel(file);

            //Assert
            load.Should().Throw<InvalidDataException>().WithMessage("*could not be read*");
            Action missingFile = () => gpu.Context.LoadModel("/nonexistent/model.glb");
            missingFile.Should().Throw<FileNotFoundException>();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void drawing_a_disposed_model_or_outside_begin_is_refused()
    {
        //Arrange
        gpu.RequireDevice();
        Model tree = gpu.Context.LoadModel(Asset("kenney-mini-forest/tree.glb"), new ModelLoadOptions { LoadTextures = false });
        ModelInstance instance = tree.CreateInstance();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        tree.Dispose();

        //Act
        Action outside = () => renderer.Draw(instance);
        Action disposed = () => Render(Front(4, Vector3.Zero), null, renderer, instance);

        //Assert
        outside.Should().Throw<InvalidOperationException>();
        disposed.Should().Throw<ObjectDisposedException>();
        tree.VertexBuffer.IsDisposed.Should().BeTrue();
    }
    [Fact]
    public void drawing_model_instances_allocates_nothing_once_warm()
    {
        //Arrange
        gpu.RequireDevice();
        using Model tree = gpu.Context.LoadModel(Asset("kenney-mini-forest/tree.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        ModelInstance[] instances = { tree.CreateInstance(), tree.CreateInstance(Matrix4x4.CreateTranslation(2, 0, 0)) };
        Camera camera = Front(8, Vector3.Zero);
        SceneLighting lighting = new SceneLighting();
        long allocated = -1;

        //Act
        for (int round = 0; round < 3; round++)
        {
            using GraphicsFrame frame = gpu.Context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Background))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                renderer.Begin(pass, camera, lighting);
                using (ModelPoseEdit pose = instances[0].BeginPoseEdit()) pose.LocalTransforms[0].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, round);
                foreach (ModelInstance instance in instances) renderer.Draw(instance);
                renderer.End();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            frame.Submit();
        }

        //Assert
        allocated.Should().Be(0);
        gpu.Context.TakeErrors().Should().BeEmpty();
    }
    [Fact]
    public void try_fix_reads_a_file_that_fails_validation_without_it_and_says_so()
    {
        //Arrange
        gpu.RequireDevice();
        float[] positions = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        byte[] bytes = new byte[positions.Length * 4];
        System.Buffer.BlockCopy(positions, 0, bytes, 0, bytes.Length);
        string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[1]}],"
            + "\"nodes\":[{\"name\":\"parent\",\"children\":[1],\"translation\":[0,2,0]},{\"name\":\"child\",\"mesh\":0}],"
            + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],"
            + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}],"
            + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36}],"
            + "\"buffers\":[{\"byteLength\":36,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]}";
        string file = Path.Combine(Path.GetTempPath(), "webgpu-root-child-" + Guid.NewGuid().ToString("N") + ".gltf");
        File.WriteAllText(file, json);
        try
        {
            //Act
            using Model lenient = gpu.Context.LoadModel(file);
            Action strict = () => gpu.Context.LoadModel(file, new ModelLoadOptions { Validation = ModelValidation.Strict });

            //Assert
            lenient.Warnings.Should().ContainSingle(warning => warning.Contains("failed glTF validation"));
            lenient.TriangleCount.Should().Be(1);
            lenient.Meshes[0].Primitives[0].NormalsGenerated.Should().BeTrue();
            lenient.Meshes[0].Primitives[0].Material.Index.Should().Be(-1);
            lenient.BoundsMax.Y.Should().BeApproximately(3f, 1e-5f);
            strict.Should().Throw<InvalidDataException>().WithMessage("*could not be read*");
        }
        finally
        {
            File.Delete(file);
        }
    }
    [Fact]
    public void a_required_extension_the_reader_does_not_support_fails_even_under_try_fix()
    {
        //Arrange
        gpu.RequireDevice();
        string json = "{\"asset\":{\"version\":\"2.0\"},\"extensionsUsed\":[\"KHR_draco_mesh_compression\"],\"extensionsRequired\":[\"KHR_draco_mesh_compression\"],"
            + "\"scenes\":[{\"name\":\"empty\"}]}";
        string file = Path.Combine(Path.GetTempPath(), "webgpu-draco-" + Guid.NewGuid().ToString("N") + ".gltf");
        File.WriteAllText(file, json);
        try
        {
            //Act
            Action load = () => gpu.Context.LoadModel(file);

            //Assert
            load.Should().Throw<InvalidDataException>().WithMessage("*KHR_draco_mesh_compression*");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
