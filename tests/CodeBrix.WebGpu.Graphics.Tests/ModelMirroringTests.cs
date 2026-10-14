using System;
using System.IO;
using System.Numerics;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Mirroring (negative-determinant) node and instance transforms flip the front-face winding as glTF requires:
/// single-sided surfaces stay visible and shade like their unmirrored originals, mirrored and plain instances share
/// one batch, normal maps keep their bitangent, and a mirrored skinned archer is the mirror image of the original.
/// </summary>
public class ModelMirroringTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 96;
    private static readonly Matrix4x4 MirrorX = Matrix4x4.CreateScale(-1f, 1f, 1f);
    private readonly GraphicsGpuFixture gpu;

    public ModelMirroringTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private byte[] Render(ModelRenderer renderer, Camera camera, SceneLighting lighting, params ModelInstance[] instances) =>
        ModelPbrTests.Render(gpu.Context, renderer, camera, lighting, Size, TextureFormat.RGBA8UnormSrgb, Color4.Black, instances);

    private static SceneLighting FromCamera() => new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = -Vector3.UnitZ, AmbientColor = Color4.Black };

    private Model Quad(Matrix4x4 nodeTransform, Action<Gltf.ModelRoot, Gltf.Material> configure = null, TestGltf.Geometry geometry = null)
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.Material material = TestGltf.Material(root, "quad", Vector4.One, 0f, 1f);
        configure?.Invoke(root, material);
        TestGltf.AddMesh(root, "quad", material, geometry ?? TestGltf.Quad());
        root.LogicalNodes[0].LocalTransform = nodeTransform;
        return TestGltf.Load(gpu.Context, root, "quad");
    }

    private static int Coverage(byte[] pixels)
    {
        int covered = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] + pixels[index + 1] + pixels[index + 2] > 0) covered++;
        }

        return covered;
    }

    [Fact]
    public void mirrored_instance_world_draws_the_front_faces_and_shades_like_the_original()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad(Matrix4x4.Identity);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = ModelPbrTests.Front(4, Vector3.Zero);
        int shaders = -1;

        //Act
        byte[] plain = Render(renderer, camera, FromCamera(), quad.CreateInstance());
        shaders = renderer.Path.ShaderCount;
        int pipelines = renderer.Path.PipelineCount;
        byte[] mirrored = Render(renderer, camera, FromCamera(), quad.CreateInstance(MirrorX));

        //Assert - glTF 3.7.4: a negative determinant makes clockwise triangles the front faces.
        Coverage(plain).Should().BeGreaterThan(1000);
        Coverage(mirrored).Should().Be(Coverage(plain));
        TestPixels.At(mirrored, Size, Size / 2, Size / 2).Should().Be(TestPixels.At(plain, Size, Size / 2, Size / 2));
        renderer.Path.ShaderCount.Should().Be(shaders, "mirroring is a pipeline variant, not a shader variant");
        renderer.Path.PipelineCount.Should().Be(pipelines + 1);
    }

    [Fact]
    public void mirroring_node_transform_from_the_file_draws_the_front_faces()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad(Matrix4x4.CreateScale(1f, -1f, 1f));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] pixels = Render(renderer, ModelPbrTests.Front(4, Vector3.Zero), FromCamera(), quad.CreateInstance());

        //Assert
        PbrModelRenderPathDeterminant(quad.CreateInstance().NodeMatrices[0]).Should().BeLessThan(0f);
        TestPixels.Luma(pixels, Size, Size / 2, Size / 2).Should().BeGreaterThan(200);
    }

    private static float PbrModelRenderPathDeterminant(Matrix4x4 matrix) => CodeBrix.WebGpu.Graphics.Models.PbrModelRenderPath.Determinant3(matrix);

    [Fact]
    public void mirrored_and_plain_instances_share_one_batch_with_one_draw_per_winding()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad(Matrix4x4.Identity);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = ModelPbrTests.Front(8, Vector3.Zero);
        ModelInstance left = quad.CreateInstance(Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateTranslation(-2.5f, 0, 0));
        ModelInstance middle = quad.CreateInstance(Matrix4x4.CreateScale(0.8f) * MirrorX);
        ModelInstance right = quad.CreateInstance(Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateTranslation(2.5f, 0, 0));

        //Act
        byte[] pixels = Render(renderer, camera, FromCamera(), left, middle, right);

        //Assert
        renderer.LastDrawCallCount.Should().Be(2);
        renderer.LastInstanceCount.Should().Be(3);
        foreach (float x in new[] { -2.5f, 0f, 2.5f })
        {
            Vector3 screen = camera.WorldToScreen(new Vector3(x, 0, 0), Size, Size);
            TestPixels.Luma(pixels, Size, (int)screen.X, (int)screen.Y).Should().BeGreaterThan(200, "the quad at x = " + x + " is drawn");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void mirrored_normal_mapped_surface_keeps_its_bitangent(int tangentSource)
    {
        //Arrange - the normal map tilts the normal up the texture's v axis, which stays world +Y under an x mirror.
        gpu.RequireDevice();
        Vector3 up = new Vector3(0, 0.6f, 0.8f);
        Vector3 encoded = (up * 0.5f) + new Vector3(0.5f);
        Rgba32 texel = new Rgba32((byte)Math.Round(encoded.X * 255), (byte)Math.Round(encoded.Y * 255), (byte)Math.Round(encoded.Z * 255), 255);
        TestGltf.Geometry geometry = TestGltf.Quad();
        if (tangentSource == 0) geometry.Tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.Material material = TestGltf.Material(root, "bumpy", Vector4.One, 0f, 1f);
        material.FindChannel("Normal").Value.SetTexture(0, TestGltf.Texture(root, 1, 1, texel));
        TestGltf.AddMesh(root, "quad", material, geometry);
        using Model quad = TestGltf.Load(gpu.Context, root, "bumpy quad", new ModelLoadOptions { GenerateTangents = tangentSource == 1 });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = ModelPbrTests.Front(4, Vector3.Zero);

        //Act
        double fromAbove = TestPixels.Luma(Render(renderer, camera, new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = -up, AmbientColor = Color4.Black }, quad.CreateInstance(MirrorX)), Size, Size / 2, Size / 2);
        double fromBelow = TestPixels.Luma(Render(renderer, camera, new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = -new Vector3(0, -0.6f, 0.8f), AmbientColor = Color4.Black }, quad.CreateInstance(MirrorX)), Size, Size / 2, Size / 2);

        //Assert
        fromAbove.Should().BeGreaterThan(240);
        fromBelow.Should().BeLessThan(160);
    }

    [Fact]
    public void mirrored_skinned_archer_is_the_mirror_image_of_the_original()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = gpu.Context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "character-archer.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Vector3 center = new Vector3(0, archer.BoundsCenter.Y, archer.BoundsCenter.Z);
        Camera camera = ModelPbrTests.Front((archer.BoundsMax - archer.BoundsMin).Length() * 1.1f, center);
        ModelInstance plain = archer.CreateInstance();
        ModelInstance mirrored = archer.CreateInstance(MirrorX);
        plain.Animation.Play("walk", AnimationLoopMode.Loop, 1f, 0.2f);
        mirrored.Animation.Play("walk", AnimationLoopMode.Loop, 1f, 0.2f);
        plain.Animation.Evaluate();
        mirrored.Animation.Evaluate();

        //Act
        byte[] original = Render(renderer, camera, FromCamera(), plain);
        byte[] reflected = Render(renderer, camera, FromCamera(), mirrored);

        //Assert - flip the mirrored picture back: it matches the original except along rasterisation edges.
        int covered = 0;
        int different = 0;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                (byte R, byte G, byte B, byte A) a = TestPixels.At(original, Size, x, y);
                (byte R, byte G, byte B, byte A) b = TestPixels.At(reflected, Size, Size - 1 - x, y);
                if (a.R + a.G + a.B > 0) covered++;
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 12) different++;
            }
        }

        covered.Should().BeGreaterThan(500);
        different.Should().BeLessThan(covered / 25, "covered " + covered);
    }
}
