using System;
using System.IO;
using System.Numerics;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Graphics.Models;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// The model renderer's metallic-roughness shading on the GPU, against known answers: a CPU reference of the glTF BRDF
/// for lit spheres, emissive, unlit, alpha mask and blend (sorted), normal-map orientation (file, generated and
/// derivative tangent frames), texture transforms, double-sided back faces, several lights, hemispheric ambient,
/// exposure, sRGB versus UNORM targets, the environment hook and the pipeline cache.
/// </summary>
public class ModelPbrTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 96;
    private readonly GraphicsGpuFixture gpu;

    public ModelPbrTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    internal static Camera Front(float height, Vector3 target) => new Camera
    {
        Projection = ProjectionKind.Orthographic,
        OrthographicHeight = height,
        Position = target + new Vector3(0, 0, 20),
        Target = target,
        NearPlane = 0.1f,
        FarPlane = 100,
    };

    internal static int Encode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double encoded = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
        return (int)Math.Round(encoded * 255);
    }

    internal static double Decode(byte value)
    {
        double encoded = value / 255.0;
        return encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
    }

    /// <summary>Renders instances into a fresh target and reads it back (RGBA, red first).</summary>
    internal static byte[] Render(GraphicsContext context, ModelRenderer renderer, Camera camera, SceneLighting lighting, int size, TextureFormat format, Color4 clear, params ModelInstance[] instances)
    {
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(size, size, format);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, clear))
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

    private byte[] Render(ModelRenderer renderer, Camera camera, SceneLighting lighting, params ModelInstance[] instances) =>
        Render(gpu.Context, renderer, camera, lighting, Size, TextureFormat.RGBA8UnormSrgb, Color4.Black, instances);

    /// <summary>The glTF reference BRDF for one directional light (radiance; light intensity 1 = a white matte surface facing it shows white).</summary>
    private static Vector3 Reference(Vector3 n, Vector3 v, Vector3 l, Vector3 light, Vector3 baseColor, float metallic, float roughness)
    {
        double nDotL = Vector3.Dot(n, l);
        if (nDotL <= 0) return Vector3.Zero;
        Vector3 h = Vector3.Normalize(l + v);
        double vDotH = Math.Clamp(Vector3.Dot(v, h), 0, 1);
        double nDotH = Math.Clamp(Vector3.Dot(n, h), 0, 1);
        double nDotV = Math.Clamp(Vector3.Dot(n, v), 0.0001, 1);
        double alpha = Math.Max(roughness * roughness, 0.0001);
        double alpha2 = alpha * alpha;
        double f = (nDotH * nDotH * (alpha2 - 1)) + 1;
        double d = alpha2 / (Math.PI * f * f);
        double vis = 0.5 / ((nDotL * Math.Sqrt((nDotV * nDotV * (1 - alpha2)) + alpha2)) + (nDotV * Math.Sqrt((nDotL * nDotL * (1 - alpha2)) + alpha2)));
        double schlick = Math.Pow(1 - vDotH, 5);
        Vector3 result = Vector3.Zero;
        for (int channel = 0; channel < 3; channel++)
        {
            double c = channel == 0 ? baseColor.X : channel == 1 ? baseColor.Y : baseColor.Z;
            double f0 = (0.04 * (1 - metallic)) + (c * metallic);
            double fresnel = f0 + ((1 - f0) * schlick);
            double diffuse = (1 - fresnel) * c * (1 - metallic) / Math.PI;
            double specular = fresnel * d * vis;
            double lightChannel = channel == 0 ? light.X : channel == 1 ? light.Y : light.Z;
            double value = (diffuse + specular) * lightChannel * Math.PI * nDotL;
            if (channel == 0) result.X = (float)value;
            else if (channel == 1) result.Y = (float)value;
            else result.Z = (float)value;
        }

        return result;
    }

    private Model Sphere(Vector4 baseColor, float metallic, float roughness)
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        TestGltf.AddMesh(root, "sphere", TestGltf.Material(root, "sphere", baseColor, metallic, roughness), TestGltf.Sphere(1f, 96, 48));
        return TestGltf.Load(gpu.Context, root, "sphere");
    }

    private Model Quad(Action<Gltf.ModelRoot, Gltf.Material> configure, TestGltf.Geometry geometry = null, ModelLoadOptions options = null)
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.Material material = TestGltf.Material(root, "quad", Vector4.One, 0f, 1f);
        configure?.Invoke(root, material);
        TestGltf.AddMesh(root, "quad", material, geometry ?? TestGltf.Quad());
        return TestGltf.Load(gpu.Context, root, "quad", options);
    }

    private static int CompareSphere(byte[] pixels, Camera camera, Vector3 lightDirection, Vector3 baseColor, float metallic, float roughness, int tolerance)
    {
        Vector3 l = -Vector3.Normalize(lightDirection);
        int checkedPixels = 0;
        float half = camera.OrthographicHeight / 2f;
        for (int y = 4; y < Size; y += 6)
        {
            for (int x = 4; x < Size; x += 6)
            {
                float wx = ((((x + 0.5f) / Size) * 2f) - 1f) * half;
                float wy = (1f - (((y + 0.5f) / Size) * 2f)) * half;
                float r2 = (wx * wx) + (wy * wy);
                if (r2 > 0.85f * 0.85f) continue;
                Vector3 position = new Vector3(wx, wy, MathF.Sqrt(1f - r2));
                Vector3 n = Vector3.Normalize(position);
                Vector3 v = Vector3.Normalize(camera.Position - position);
                Vector3 expected = Reference(n, v, l, Vector3.One, baseColor, metallic, roughness);
                (byte R, byte G, byte B, byte A) actual = TestPixels.At(pixels, Size, x, y);
                Math.Abs(actual.R - Encode(expected.X)).Should().BeLessThanOrEqualTo(tolerance, "pixel " + x + "," + y + " red " + actual.R + " vs " + Encode(expected.X));
                Math.Abs(actual.G - Encode(expected.Y)).Should().BeLessThanOrEqualTo(tolerance, "pixel " + x + "," + y + " green");
                Math.Abs(actual.B - Encode(expected.Z)).Should().BeLessThanOrEqualTo(tolerance, "pixel " + x + "," + y + " blue");
                checkedPixels++;
            }
        }

        return checkedPixels;
    }

    [Fact]
    public void white_rough_dielectric_sphere_under_one_light_matches_the_cpu_reference()
    {
        //Arrange
        gpu.RequireDevice();
        using Model sphere = Sphere(Vector4.One, 0f, 1f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = Front(2.4f, Vector3.Zero);
        Vector3 direction = new Vector3(-0.5f, -0.6f, -1f);
        SceneLighting lighting = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = direction, AmbientColor = Color4.Black };

        //Act
        byte[] pixels = Render(renderer, camera, lighting, sphere.CreateInstance());

        //Assert
        CompareSphere(pixels, camera, direction, Vector3.One, 0f, 1f, 3).Should().BeGreaterThan(80);
    }

    [Fact]
    public void gold_metal_sphere_with_medium_roughness_matches_the_cpu_reference()
    {
        //Arrange
        gpu.RequireDevice();
        Vector3 gold = new Vector3(1f, 0.766f, 0.336f);
        using Model sphere = Sphere(new Vector4(gold, 1f), 1f, 0.5f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = Front(2.4f, Vector3.Zero);
        Vector3 direction = new Vector3(0.3f, -0.4f, -1f);
        SceneLighting lighting = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = direction, AmbientColor = Color4.Black };

        //Act
        byte[] pixels = Render(renderer, camera, lighting, sphere.CreateInstance());

        //Assert
        CompareSphere(pixels, camera, direction, gold, 1f, 0.5f, 4).Should().BeGreaterThan(80);
    }

    [Fact]
    public void emissive_only_surface_shows_the_emissive_colour_exactly()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad((root, material) =>
        {
            Gltf.Toolkit.WithChannelColor(material, "BaseColor", new Vector4(0, 0, 0, 1));
            Gltf.Toolkit.WithChannelColor(material, "Emissive", new Vector4(1f, 0.5f, 0.25f, 1f));
        });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting dark = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightIntensity = 0f, AmbientColor = Color4.Black };

        //Act
        byte[] pixels = Render(renderer, Front(4, Vector3.Zero), dark, quad.CreateInstance());

        //Assert
        TestPixels.Near(TestPixels.At(pixels, Size, Size / 2, Size / 2), 255, Encode(0.5), Encode(0.25), 1).Should().BeTrue();
    }

    [Fact]
    public void unlit_material_shows_base_colour_times_vertex_colour_whatever_the_light()
    {
        //Arrange
        gpu.RequireDevice();
        TestGltf.Geometry geometry = TestGltf.Quad();
        geometry.Colors = new[] { new Vector4(1, 0.5f, 1, 1), new Vector4(1, 0.5f, 1, 1), new Vector4(1, 0.5f, 1, 1), new Vector4(1, 0.5f, 1, 1) };
        using Model quad = Quad(
            (root, material) =>
            {
                Gltf.Toolkit.WithUnlit(material);
                Gltf.Toolkit.WithChannelColor(material, "BaseColor", new Vector4(0.2f, 0.4f, 0.6f, 1f));
            },
            geometry);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting bright = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = -Vector3.UnitZ, LightIntensity = 5f, AmbientColor = Color4.White };

        //Act
        byte[] pixels = Render(renderer, Front(4, Vector3.Zero), bright, quad.CreateInstance());

        //Assert
        quad.Materials[0].Unlit.Should().BeTrue();
        TestPixels.Near(TestPixels.At(pixels, Size, Size / 2, Size / 2), Encode(0.2), Encode(0.2), Encode(0.6), 1).Should().BeTrue();
    }

    [Fact]
    public void alpha_mask_discards_texels_below_the_cutoff_and_draws_the_rest_opaque()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad((root, material) =>
        {
            Gltf.Toolkit.WithUnlit(material);
            material.Alpha = Gltf.AlphaMode.MASK;
            material.AlphaCutoff = 0.5f;
            material.FindChannel("BaseColor").Value.SetTexture(0, TestGltf.Texture(root, 2, 1, new Rgba32(255, 255, 255, 102), new Rgba32(255, 255, 255, 153)));
        });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] pixels = Render(renderer, Front(4, Vector3.Zero), null, quad.CreateInstance());

        //Assert
        TestPixels.At(pixels, Size, Size / 2 - 12, Size / 2).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        TestPixels.At(pixels, Size, Size / 2 + 12, Size / 2).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void blended_primitives_are_drawn_back_to_front_whatever_the_draw_order()
    {
        //Arrange
        gpu.RequireDevice();
        Model Blended(Vector4 color) => Quad((root, material) =>
        {
            Gltf.Toolkit.WithUnlit(material);
            material.Alpha = Gltf.AlphaMode.BLEND;
            Gltf.Toolkit.WithChannelColor(material, "BaseColor", color);
        });
        using Model red = Blended(new Vector4(1, 0, 0, 0.5f));
        using Model blue = Blended(new Vector4(0, 0, 1, 0.5f));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        ModelInstance far = red.CreateInstance(Matrix4x4.CreateTranslation(0, 0, -1));
        ModelInstance near = blue.CreateInstance(Matrix4x4.CreateTranslation(0, 0, 1));

        //Act
        byte[] nearFirst = Render(renderer, Front(4, Vector3.Zero), null, near, far);
        byte[] farFirst = Render(renderer, Front(4, Vector3.Zero), null, far, near);

        //Assert - black, then red at 50%, then blue at 50% on top (blending in linear light on the sRGB target).
        (int R, int G, int B) expected = (Encode(0.25), 0, Encode(0.5));
        TestPixels.Near(TestPixels.At(nearFirst, Size, Size / 2, Size / 2), expected.R, expected.G, expected.B, 2).Should().BeTrue();
        TestPixels.Near(TestPixels.At(farFirst, Size, Size / 2, Size / 2), expected.R, expected.G, expected.B, 2).Should().BeTrue();
        renderer.LastDrawCallCount.Should().Be(2);
    }

    /// <summary>Renders a normal-mapped white quad whose map tilts every normal by a fixed tangent-space vector, lit along a direction.</summary>
    private double NormalMappedLuma(Vector3 tangentNormal, Vector3 lightTravel, int tangentSource)
    {
        Vector3 encoded = (Vector3.Normalize(tangentNormal) * 0.5f) + new Vector3(0.5f);
        Rgba32 texel = new Rgba32((byte)Math.Round(encoded.X * 255), (byte)Math.Round(encoded.Y * 255), (byte)Math.Round(encoded.Z * 255), 255);
        TestGltf.Geometry geometry = TestGltf.Quad();
        if (tangentSource == 0) geometry.Tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
        ModelLoadOptions options = new ModelLoadOptions { GenerateTangents = tangentSource == 1 };
        using Model quad = Quad((root, material) => material.FindChannel("Normal").Value.SetTexture(0, TestGltf.Texture(root, 1, 1, texel)), geometry, options);
        quad.Meshes[0].Primitives[0].HasTangents.Should().Be(tangentSource != 2);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting lighting = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = lightTravel, AmbientColor = Color4.Black };
        byte[] pixels = Render(renderer, Front(4, Vector3.Zero), lighting, quad.CreateInstance());
        return TestPixels.Luma(pixels, Size, Size / 2, Size / 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void normal_map_green_points_up_the_texture_and_red_along_u_for_every_tangent_source(int tangentSource)
    {
        //Arrange
        gpu.RequireDevice();
        Vector3 up = new Vector3(0, 0.6f, 0.8f);
        Vector3 right = new Vector3(0.6f, 0, 0.8f);

        //Act - light arriving along the tilted normal is brightest, from the mirrored side darker.
        double litFromAbove = NormalMappedLuma(up, -up, tangentSource);
        double litFromBelow = NormalMappedLuma(up, -new Vector3(0, -0.6f, 0.8f), tangentSource);
        double litFromRight = NormalMappedLuma(right, -right, tangentSource);
        double litFromLeft = NormalMappedLuma(right, -new Vector3(-0.6f, 0, 0.8f), tangentSource);

        //Assert - N.L = 1 gives (1 - F) * white ~ 0.96 linear; the mirrored light gives N.L = 0.28.
        litFromAbove.Should().BeGreaterThan(240);
        litFromRight.Should().BeGreaterThan(240);
        litFromBelow.Should().BeLessThan(160);
        litFromLeft.Should().BeLessThan(160);
    }

    [Fact]
    public void texture_transform_applies_per_slot()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad((root, material) =>
        {
            Gltf.Toolkit.WithUnlit(material);
            Gltf.MaterialChannel channel = material.FindChannel("BaseColor").Value;
            channel.SetTexture(0, TestGltf.Texture(root, 2, 1, new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255)));
            channel.SetTransform(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f));
        });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();

        //Act
        byte[] pixels = Render(renderer, Front(4, Vector3.Zero), null, quad.CreateInstance());

        //Assert - u' = u * 0.5 + 0.5 samples only the green texel.
        quad.Materials[0].BaseColorTexture.HasTransform.Should().BeTrue();
        TestPixels.At(pixels, Size, Size / 2 - 20, Size / 2).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        TestPixels.At(pixels, Size, Size / 2 + 20, Size / 2).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
    }

    [Fact]
    public void double_sided_back_faces_shade_with_the_flipped_normal_and_single_sided_ones_are_culled()
    {
        //Arrange
        gpu.RequireDevice();
        using Model single = Quad(null);
        using Model both = Quad((root, material) => material.DoubleSided = true);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera behind = new Camera { Projection = ProjectionKind.Orthographic, OrthographicHeight = 4, Position = new Vector3(0, 0, -20), Target = Vector3.Zero, NearPlane = 0.1f, FarPlane = 100 };
        SceneLighting fromCamera = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = Vector3.UnitZ, AmbientColor = Color4.Black };
        SceneLighting fromFront = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = -Vector3.UnitZ, AmbientColor = Color4.Black };

        //Act
        byte[] culled = Render(renderer, behind, fromCamera, single.CreateInstance());
        byte[] litBack = Render(renderer, behind, fromCamera, both.CreateInstance());
        byte[] unlitBack = Render(renderer, behind, fromFront, both.CreateInstance());

        //Assert
        Vector3 expected = Reference(-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, Vector3.One, Vector3.One, 0f, 1f);
        TestPixels.At(culled, Size, Size / 2, Size / 2).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        TestPixels.Near(TestPixels.At(litBack, Size, Size / 2, Size / 2), Encode(expected.X), Encode(expected.Y), Encode(expected.Z), 2).Should().BeTrue();
        TestPixels.At(unlitBack, Size, Size / 2, Size / 2).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
    }

    [Fact]
    public void additional_lights_add_up_and_exposure_scales_the_result()
    {
        //Arrange
        gpu.RequireDevice();
        using Model quad = Quad(null);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Vector3 direction = new Vector3(0, 0, -1);
        SceneLighting one = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = direction, LightIntensity = 0.25f, AmbientColor = Color4.Black };
        SceneLighting two = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = direction, LightIntensity = 0.25f, AmbientColor = Color4.Black };
        two.AdditionalLights.Add(new DirectionalLight(direction, Color4.White, 0.25f));
        SceneLighting halved = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightDirection = direction, LightIntensity = 0.25f, AmbientColor = Color4.Black, Exposure = 0.5f };
        halved.AdditionalLights.Add(new DirectionalLight(direction, Color4.White, 0.25f));

        //Act
        double single = Decode(TestPixels.At(Render(renderer, Front(4, Vector3.Zero), one, quad.CreateInstance()), Size, Size / 2, Size / 2).R);
        double doubled = Decode(TestPixels.At(Render(renderer, Front(4, Vector3.Zero), two, quad.CreateInstance()), Size, Size / 2, Size / 2).R);
        double exposed = Decode(TestPixels.At(Render(renderer, Front(4, Vector3.Zero), halved, quad.CreateInstance()), Size, Size / 2, Size / 2).R);

        //Assert
        (doubled / single).Should().BeApproximately(2.0, 0.08);
        (exposed / single).Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void hemispheric_ambient_blends_sky_above_and_ground_below()
    {
        //Arrange
        gpu.RequireDevice();
        using Model sphere = Sphere(Vector4.One, 0f, 1f);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting ambientOnly = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightIntensity = 0f, AmbientColor = new Color4(0.5f, 0f, 0f), AmbientGroundColor = new Color4(0f, 0f, 0.5f) };

        //Act
        byte[] pixels = Render(renderer, Front(2.4f, Vector3.Zero), ambientOnly, sphere.CreateInstance());
        (byte R, byte G, byte B, byte A) top = TestPixels.At(pixels, Size, Size / 2, 12);
        (byte R, byte G, byte B, byte A) bottom = TestPixels.At(pixels, Size, Size / 2, Size - 13);

        //Assert
        ((int)top.R).Should().BeGreaterThan(top.B + 60);
        ((int)bottom.B).Should().BeGreaterThan(bottom.R + 60);
    }

    [Fact]
    public void srgb_and_unorm_targets_show_the_same_picture()
    {
        //Arrange
        gpu.RequireDevice();
        using Model tree = gpu.Context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "tree.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = Front((tree.BoundsMax - tree.BoundsMin).Length(), tree.BoundsCenter);
        SceneLighting lighting = new SceneLighting();

        //Act
        byte[] srgb = Render(gpu.Context, renderer, camera, lighting, Size, TextureFormat.RGBA8UnormSrgb, Color4.Black, tree.CreateInstance());
        byte[] unorm = Render(gpu.Context, renderer, camera, lighting, Size, TextureFormat.RGBA8Unorm, Color4.Black, tree.CreateInstance());

        //Assert
        int largest = 0;
        int lit = 0;
        for (int index = 0; index < srgb.Length; index++)
        {
            largest = Math.Max(largest, Math.Abs(srgb[index] - unorm[index]));
            if (index % 4 == 1 && srgb[index] > 20) lit++;
        }

        lit.Should().BeGreaterThan(200);
        largest.Should().BeLessThanOrEqualTo(2);
        PbrModelRenderPath.NeedsShaderEncoding(TextureFormat.RGBA8Unorm).Should().BeTrue();
        PbrModelRenderPath.NeedsShaderEncoding(TextureFormat.BGRA8UnormSrgb).Should().BeFalse();
        PbrModelRenderPath.NeedsShaderEncoding(TextureFormat.RGBA16Float).Should().BeFalse();
    }

    [Fact]
    public void environment_hook_replaces_the_hemispheric_ambient()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using Model sphere = Sphere(Vector4.One, 0f, 1f);
        using ModelRenderer renderer = context.CreateModelRenderer();
        using GpuTexture cubeTexture = context.CreateTextureArray(1, 1, 6, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        for (int layer = 0; layer < 6; layer++) cubeTexture.WriteRegion(new TextureRegion(0, 0, 1, 1, 0, layer), new byte[] { 128, 128, 128, 255 });
        using GpuTexture cube = cubeTexture.CreateCubeView();
        using GpuTexture lut = context.CreateTextureFromRgba(1, 1, new byte[] { 0, 0, 0, 255 });
        SceneLighting environment = new SceneLighting { LightIntensity = 0f };
        SceneLighting none = new SceneLighting { Ambient = AmbientLighting.Hemisphere, LightIntensity = 0f, AmbientColor = Color4.Black };

        //Act
        renderer.Path.SetEnvironment(cube, cube, lut, null, 1f);
        byte[] lit = Render(renderer, Front(2.4f, Vector3.Zero), environment, sphere.CreateInstance());
        byte[] hemisphere = Render(renderer, Front(2.4f, Vector3.Zero), none, sphere.CreateInstance());
        renderer.Path.SetEnvironment(null, null, null, null, 0f);
        byte[] dark = Render(renderer, Front(2.4f, Vector3.Zero), none, sphere.CreateInstance());
        byte[] sky = Render(renderer, Front(2.4f, Vector3.Zero), environment, sphere.CreateInstance());

        //Assert - irradiance 128/255 times a white diffuse colour; the LUT (0, 0) removes the specular term. The
        // hemispheric ambient ignores the environment; without the override the default sky lights the sphere.
        int expected = Encode(128 / 255.0);
        TestPixels.Near(TestPixels.At(lit, Size, Size / 2, Size / 2), expected, expected, expected, 2).Should().BeTrue();
        TestPixels.At(hemisphere, Size, Size / 2, Size / 2).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        TestPixels.At(dark, Size, Size / 2, Size / 2).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        TestPixels.Luma(sky, Size, Size / 2, Size / 2).Should().BeGreaterThan(40);
    }

    [Fact]
    public void approximated_extensions_render_without_errors()
    {
        //Arrange
        gpu.RequireDevice();
        using Model coated = Quad((root, material) =>
        {
            material.InitializePBRMetallicRoughness("ClearCoat", "Transmission");
            Gltf.Toolkit.WithChannelFactor(material, "ClearCoat", "ClearCoatFactor", 1f);
            Gltf.Toolkit.WithChannelFactor(material, "Transmission", "TransmissionFactor", 0.5f);
        });
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        SceneLighting lighting = new SceneLighting { LightDirection = new Vector3(0, -0.3f, -1f) };

        //Act
        byte[] pixels = Render(renderer, Front(4, Vector3.Zero), lighting, coated.CreateInstance());

        //Assert
        coated.Materials[0].ClearCoatFactor.Should().Be(1f);
        coated.Materials[0].ApproximatedFeatures.Should().Contain("KHR_materials_clearcoat");
        TestPixels.Luma(pixels, Size, Size / 2, Size / 2).Should().BeGreaterThan(100);
    }

    [Fact]
    public void pipelines_and_material_groups_are_cached()
    {
        //Arrange
        gpu.RequireDevice();
        using Model tree = gpu.Context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "tree.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = Front(4, tree.BoundsCenter);

        //Act
        Render(gpu.Context, renderer, camera, null, 32, TextureFormat.RGBA8UnormSrgb, Color4.Black, tree.CreateInstance());
        int pipelines = renderer.Path.PipelineCount;
        int shaders = renderer.Path.ShaderCount;
        Render(gpu.Context, renderer, camera, null, 32, TextureFormat.RGBA8UnormSrgb, Color4.Black, tree.CreateInstance(), tree.CreateInstance());
        int samePipelines = renderer.Path.PipelineCount;
        Render(gpu.Context, renderer, camera, null, 32, TextureFormat.RGBA8Unorm, Color4.Black, tree.CreateInstance());

        //Assert
        samePipelines.Should().Be(pipelines);
        renderer.Path.PipelineCount.Should().Be(pipelines * 2);
        renderer.Path.ShaderCount.Should().Be(shaders * 2);
        renderer.Path.ModelStateCount.Should().Be(1);
        renderer.Forget(tree);
        renderer.Path.ModelStateCount.Should().Be(0);
    }
}
