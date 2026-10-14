using System;
using System.IO;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Image-based lighting in the model renderer: white furnaces (energy conservation for dielectrics and metals at every
/// roughness), a mirror sphere reflecting the environment, environment rotation and intensity, clear coat, the default
/// sky, the per-environment frame bind group cache, zero per-frame allocation, disposal, and the HDR path (float target +
/// tone map) against the direct LDR path.
/// </summary>
public class ModelEnvironmentTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 96;
    private static readonly EnvironmentOptions Small = new EnvironmentOptions { CubeSize = 128, IrradianceSize = 16, IrradianceSourceSize = 16, PrefilteredSize = 64, PrefilteredMipLevels = 5, SpecularSampleCount = 128 };
    private readonly GraphicsGpuFixture gpu;

    public ModelEnvironmentTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private Model Sphere(Vector4 baseColor, float metallic, float roughness, Action<Gltf.ModelRoot, Gltf.Material> configure = null)
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.Material material = TestGltf.Material(root, "sphere", baseColor, metallic, roughness);
        configure?.Invoke(root, material);
        TestGltf.AddMesh(root, "sphere", material, TestGltf.Sphere(1f, 96, 48));
        return TestGltf.Load(gpu.Context, root, "sphere");
    }

    private byte[] Render(ModelRenderer renderer, SceneLighting lighting, TextureFormat format, params ModelInstance[] instances) =>
        ModelPbrTests.Render(gpu.Context, renderer, ModelPbrTests.Front(2.4f, Vector3.Zero), lighting, Size, format, Color4.Black, instances);

    /// <summary>Every sphere pixel inside 85 % of the radius.</summary>
    private static int CheckSphere(byte[] pixels, Func<(byte R, byte G, byte B, byte A), bool> check)
    {
        int checkedPixels = 0;
        for (int y = 2; y < Size; y += 4)
        {
            for (int x = 2; x < Size; x += 4)
            {
                float wx = ((((x + 0.5f) / Size) * 2f) - 1f) * 1.2f;
                float wy = (1f - (((y + 0.5f) / Size) * 2f)) * 1.2f;
                if ((wx * wx) + (wy * wy) > 0.85f * 0.85f) continue;
                (byte R, byte G, byte B, byte A) pixel = TestPixels.At(pixels, Size, x, y);
                check(pixel).Should().BeTrue("pixel " + x + "," + y + " = " + pixel);
                checkedPixels++;
            }
        }

        return checkedPixels;
    }

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(0f, 0.5f)]
    [InlineData(0f, 0.1f)]
    [InlineData(1f, 1f)]
    [InlineData(1f, 0.5f)]
    [InlineData(1f, 0.15f)]
    public void a_white_sphere_in_a_constant_white_furnace_shows_the_furnace_value(float metallic, float roughness)
    {
        //Arrange - environment radiance 0.5 everywhere, no lights: energy conservation says every pixel shows 0.5
        gpu.RequireDevice();
        using EnvironmentMap furnace = gpu.Context.CreateEnvironment(TestEnvironments.Constant(new Vector3(0.5f)), Small);
        using Model sphere = Sphere(Vector4.One, metallic, roughness);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting lighting = new SceneLighting { LightIntensity = 0f, Environment = furnace };

        //Act
        byte[] pixels = Render(renderer, lighting, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());

        //Assert - encode(0.5) = 188
        int expected = ModelPbrTests.Encode(0.5);
        CheckSphere(pixels, p => TestPixels.Near(p, expected, expected, expected, 3)).Should().BeGreaterThan(200);
    }

    [Fact]
    public void a_mirror_sphere_reflects_the_sky_above_and_the_ground_below()
    {
        //Arrange - red sky (y > 0), green ground
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(d => d.Y > 0f ? new Vector3(0.8f, 0f, 0f) : new Vector3(0f, 0.8f, 0f), 512), Small);
        using Model sphere = Sphere(Vector4.One, 1f, 0f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting lighting = new SceneLighting { LightIntensity = 0f, Environment = environment };

        //Act
        byte[] pixels = Render(renderer, lighting, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());

        //Assert - near the top the reflection points up (red), near the bottom down (green)
        (byte R, byte G, byte B, byte A) top = TestPixels.At(pixels, Size, Size / 2, Size / 2 - 28);
        (byte R, byte G, byte B, byte A) bottom = TestPixels.At(pixels, Size, Size / 2, Size / 2 + 28);
        TestPixels.Near(top, ModelPbrTests.Encode(0.8), 0, 0, 6).Should().BeTrue("top " + top);
        TestPixels.Near(bottom, 0, ModelPbrTests.Encode(0.8), 0, 6).Should().BeTrue("bottom " + bottom);
    }

    [Fact]
    public void rotating_the_environment_turns_the_reflections_and_intensity_scales_them()
    {
        //Arrange - +X half red, -X half blue; a mirror sphere's left edge reflects -X
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(d => d.X > 0f ? new Vector3(0.6f, 0f, 0f) : new Vector3(0f, 0f, 0.6f), 512), Small);
        using Model sphere = Sphere(Vector4.One, 1f, 0f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] plain = Render(renderer, new SceneLighting { LightIntensity = 0f, Environment = environment }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());
        byte[] turned = Render(renderer, new SceneLighting { LightIntensity = 0f, Environment = environment, EnvironmentRotation = MathF.PI }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());
        byte[] brighter = Render(renderer, new SceneLighting { LightIntensity = 0f, Environment = environment, EnvironmentIntensity = 1.5f }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());

        //Assert
        int left = (Size / 2) - 28;
        TestPixels.Near(TestPixels.At(plain, Size, left, Size / 2), 0, 0, ModelPbrTests.Encode(0.6), 6).Should().BeTrue();
        TestPixels.Near(TestPixels.At(turned, Size, left, Size / 2), ModelPbrTests.Encode(0.6), 0, 0, 6).Should().BeTrue();
        TestPixels.Near(TestPixels.At(brighter, Size, left, Size / 2), 0, 0, ModelPbrTests.Encode(0.9), 6).Should().BeTrue();
    }

    [Fact]
    public void a_quarter_turn_moves_what_was_ahead_to_the_side_the_documentation_says()
    {
        //Arrange - only -Z (the panorama centre) is bright; a positive rotation turns it towards -X
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(d => d.Z < -0.7f ? new Vector3(0.7f) : Vector3.Zero, 512), Small);
        using Model sphere = Sphere(Vector4.One, 1f, 0f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] pixels = Render(renderer, new SceneLighting { LightIntensity = 0f, Environment = environment, EnvironmentRotation = MathF.PI / 2f }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());

        //Assert - the sphere's left edge reflects -X (bright now), the right edge +X (dark)
        TestPixels.Luma(pixels, Size, (Size / 2) - 30, Size / 2).Should().BeGreaterThan(150);
        TestPixels.Luma(pixels, Size, (Size / 2) + 30, Size / 2).Should().BeLessThan(10);
    }

    [Fact]
    public void the_default_sky_lights_models_when_no_environment_is_set()
    {
        //Arrange
        gpu.RequireDevice();
        using Model sphere = Sphere(Vector4.One, 0f, 1f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] withDefault = Render(renderer, new SceneLighting { LightIntensity = 0f }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());
        byte[] explicitDefault = Render(renderer, new SceneLighting { LightIntensity = 0f, Environment = gpu.Context.DefaultEnvironment }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());

        //Assert - the top of a white sphere is lit by the bluish sky, the bottom by the darker ground
        withDefault.Should().Equal(explicitDefault);
        (byte R, byte G, byte B, byte A) top = TestPixels.At(withDefault, Size, Size / 2, (Size / 2) - 30);
        (byte R, byte G, byte B, byte A) bottom = TestPixels.At(withDefault, Size, Size / 2, (Size / 2) + 30);
        top.B.Should().BeGreaterThan(top.R);
        TestPixels.Luma(withDefault, Size, Size / 2, (Size / 2) - 30).Should().BeGreaterThan(TestPixels.Luma(withDefault, Size, Size / 2, (Size / 2) + 30) + 20);
        TestPixels.Luma(withDefault, Size, Size / 2, (Size / 2) - 30).Should().BeInRange(90, 200);
        bottom.A.Should().Be(255);
    }

    [Fact]
    public void a_clear_coat_reflects_the_environment_too()
    {
        //Arrange - a black rough dielectric with a full clear coat under a red sky / green ground
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(d => d.Y > 0f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f), 512), Small);
        using Model coated = Sphere(new Vector4(0f, 0f, 0f, 1f), 0f, 1f, (root, material) =>
        {
            material.InitializePBRMetallicRoughness("ClearCoat");
            Gltf.Toolkit.WithChannelFactor(material, "ClearCoat", "ClearCoatFactor", 1f);
        });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] pixels = Render(renderer, new SceneLighting { LightIntensity = 0f, Environment = environment }, TextureFormat.RGBA8UnormSrgb, coated.CreateInstance());

        //Assert - grazing top edge: the coat's Fresnel reflection of the red sky dominates
        (byte R, byte G, byte B, byte A) edge = TestPixels.At(pixels, Size, Size / 2, (Size / 2) - 37);
        edge.R.Should().BeGreaterThan((byte)(edge.G + 30));
    }

    [Fact]
    public void the_hdr_path_with_a_clamp_tone_map_equals_drawing_straight_into_the_srgb_target()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using Model tree = context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "tree.glb"));
        using ModelRenderer renderer = context.CreateModelRenderer();
        using SkyboxRenderer sky = context.CreateSkyboxRenderer();
        using ToneMapper toneMapper = context.CreateToneMapper();
        toneMapper.Operator = ToneMapOperator.Clamp;
        Camera camera = new Camera { Position = new Vector3(2f, 1.5f, 3f), Target = tree.BoundsCenter, FieldOfView = 0.9f };
        SceneLighting lighting = new SceneLighting { Exposure = 0.8f };
        using HeadlessRenderTarget direct = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        using HeadlessRenderTarget hdr = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA16Float);
        using HeadlessRenderTarget final = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb, false);

        //Act
        foreach (HeadlessRenderTarget target in new[] { direct, hdr })
        {
            using GraphicsFrame frame = context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                renderer.Begin(pass, camera, lighting);
                renderer.Draw(tree.CreateInstance());
                renderer.End();
                sky.Draw(pass, camera, lighting);
            }

            if (target == hdr) toneMapper.Apply(frame, hdr.Color, final.View);
            frame.Submit();
        }

        //Assert - the same picture within rounding (the float target keeps more precision before encoding)
        byte[] a = direct.ReadRgba();
        byte[] b = final.ReadRgba();
        int worst = 0;
        for (int index = 0; index < a.Length; index++) worst = Math.Max(worst, Math.Abs(a[index] - b[index]));
        worst.Should().BeLessThanOrEqualTo(2);
        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void one_frame_bind_group_per_environment_and_no_per_frame_allocation_once_warm()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using EnvironmentMap first = context.CreateEnvironment(TestEnvironments.Constant(new Vector3(0.3f)), Small);
        using EnvironmentMap second = context.CreateProceduralSky(null, Small);
        using Model tree = context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "tree.glb"));
        using ModelRenderer renderer = context.CreateModelRenderer();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(32, 32, TextureFormat.RGBA8UnormSrgb);
        Camera camera = ModelPbrTests.Front(4, tree.BoundsCenter);
        ModelInstance instance = tree.CreateInstance();
        SceneLighting a = new SceneLighting { Environment = first };
        SceneLighting b = new SceneLighting { Environment = second, EnvironmentRotation = 1f };
        SceneLighting hemisphere = new SceneLighting { Ambient = AmbientLighting.Hemisphere };
        long allocated = -1;
        int resources = 0;

        //Act - alternate environments within and across frames
        for (int round = 0; round < 6; round++)
        {
            using GraphicsFrame frame = context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                foreach (SceneLighting lighting in new[] { a, b, hemisphere })
                {
                    lighting.EnvironmentRotation = round * 0.3f;
                    renderer.Begin(pass, camera, lighting);
                    renderer.Draw(instance);
                    renderer.End();
                }

                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            frame.Submit();
            if (round == 2) resources = context.LiveResourceCount;
        }

        //Assert - (the array of three lightings is allocated by the test itself: 48 bytes)
        renderer.Path.FrameGroupCount.Should().Be(3);
        allocated.Should().BeLessThanOrEqualTo(64);
        context.LiveResourceCount.Should().Be(resources);
        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void a_disposed_environment_cannot_be_drawn_with_and_its_bind_group_is_dropped()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        EnvironmentMap environment = context.CreateEnvironment(TestEnvironments.Constant(new Vector3(0.3f)), Small);
        using Model sphere = Sphere(Vector4.One, 0f, 1f);
        using ModelRenderer renderer = context.CreateModelRenderer();
        Render(renderer, new SceneLighting { Environment = environment }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());
        renderer.Path.FrameGroupCount.Should().Be(1);

        //Act
        environment.Dispose();
        Action draw = () => Render(renderer, new SceneLighting { Environment = environment }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());
        Render(renderer, new SceneLighting { Ambient = AmbientLighting.Hemisphere }, TextureFormat.RGBA8UnormSrgb, sphere.CreateInstance());

        //Assert
        draw.Should().Throw<ObjectDisposedException>();
        renderer.Path.FrameGroupCount.Should().Be(1);
    }
}
