using System;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// The skybox on the GPU: each view direction shows its cube face, rotation / intensity / exposure, depth (geometry stays
/// in front), blur, sRGB vs UNORM vs float targets, the default sky and allocation-free drawing.
/// </summary>
public class SkyboxRendererTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 48;
    private static readonly EnvironmentOptions Small = new EnvironmentOptions { CubeSize = 64, IrradianceSize = 8, IrradianceSourceSize = 8, PrefilteredSize = 32, PrefilteredMipLevels = 4, SpecularSampleCount = 64 };
    private readonly GraphicsGpuFixture gpu;

    public SkyboxRendererTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    /// <summary>Half-intensity primaries per dominant axis: +X red, -X green, +Y blue, -Y yellow, +Z magenta, -Z cyan.</summary>
    internal static Vector3 AxisColour(Vector3 d)
    {
        Vector3 a = Vector3.Abs(d);
        if (a.X >= a.Y && a.X >= a.Z) return d.X > 0 ? new Vector3(0.5f, 0f, 0f) : new Vector3(0f, 0.5f, 0f);
        if (a.Y >= a.Z) return d.Y > 0 ? new Vector3(0f, 0f, 0.5f) : new Vector3(0.5f, 0.5f, 0f);
        return d.Z > 0 ? new Vector3(0.5f, 0f, 0.5f) : new Vector3(0f, 0.5f, 0.5f);
    }

    private static Camera Looking(Vector3 direction, float fieldOfView = 0.5f) => new Camera
    {
        Position = Vector3.Zero,
        Target = direction,
        Up = MathF.Abs(direction.Y) > 0.9f ? -Vector3.UnitZ : Vector3.UnitY,
        FieldOfView = fieldOfView,
        NearPlane = 0.1f,
        FarPlane = 100f,
    };

    private byte[] Draw(SkyboxRenderer sky, Camera camera, EnvironmentMap environment, TextureFormat format = TextureFormat.RGBA8UnormSrgb, float intensity = 1f, float rotation = 0f, float exposure = 1f)
    {
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, format);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                sky.Draw(pass, camera, environment, intensity, rotation, exposure);
            }

            frame.Submit();
        }

        context.TakeErrors().Should().BeEmpty();
        return format == TextureFormat.RGBA16Float ? target.ReadPixels() : target.ReadRgba();
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0f, -1f)]
    public void each_view_direction_shows_the_environment_in_that_direction(float x, float y, float z)
    {
        //Arrange
        gpu.RequireDevice();
        Vector3 direction = new Vector3(x, y, z);
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(AxisColour, 512), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();

        //Act
        byte[] pixels = Draw(sky, Looking(direction), environment);

        //Assert
        Vector3 expected = AxisColour(direction);
        TestPixels.Near(TestPixels.At(pixels, Size, Size / 2, Size / 2), ModelPbrTests.Encode(expected.X), ModelPbrTests.Encode(expected.Y), ModelPbrTests.Encode(expected.Z), 2).Should().BeTrue();
        sky.DrawCount.Should().Be(1);
    }

    [Fact]
    public void up_in_the_picture_is_up_in_the_world_and_right_is_right()
    {
        //Arrange - looking along -Z: the top of the picture sees +Y (blue), the right edge +X (red) with a wide view
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(AxisColour, 512), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();

        //Act
        byte[] pixels = Draw(sky, Looking(-Vector3.UnitZ, 2.4f), environment);

        //Assert
        TestPixels.At(pixels, Size, Size / 2, 1).B.Should().BeGreaterThan(150);
        TestPixels.At(pixels, Size, Size - 2, Size / 2).R.Should().BeGreaterThan(150);
        TestPixels.At(pixels, Size, 1, Size / 2).G.Should().BeGreaterThan(150);
    }

    [Fact]
    public void rotation_intensity_and_exposure_apply_like_the_model_lighting()
    {
        //Arrange
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(AxisColour, 512), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();

        //Act - a quarter turn brings +X to -Z; intensity 1.5 x exposure 0.5 = 0.75 of 0.5
        byte[] turned = Draw(sky, Looking(-Vector3.UnitZ), environment, rotation: MathF.PI / 2f);
        byte[] scaled = Draw(sky, Looking(-Vector3.UnitZ), environment, intensity: 1.5f, exposure: 0.5f);

        //Assert
        TestPixels.Near(TestPixels.At(turned, Size, Size / 2, Size / 2), ModelPbrTests.Encode(0.5), 0, 0, 2).Should().BeTrue();
        TestPixels.Near(TestPixels.At(scaled, Size, Size / 2, Size / 2), 0, ModelPbrTests.Encode(0.375), ModelPbrTests.Encode(0.375), 2).Should().BeTrue();
    }

    [Fact]
    public void scene_lighting_supplies_environment_rotation_intensity_and_exposure()
    {
        //Arrange
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(AxisColour, 512), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();
        SceneLighting lighting = new SceneLighting { Environment = environment, EnvironmentRotation = MathF.PI / 2f, EnvironmentIntensity = 1.5f, Exposure = 0.5f };
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black)) sky.Draw(pass, Looking(-Vector3.UnitZ), lighting);
            frame.Submit();
        }

        //Assert - +X red x 0.75
        TestPixels.Near(TestPixels.At(target.ReadRgba(), Size, Size / 2, Size / 2), ModelPbrTests.Encode(0.375), 0, 0, 2).Should().BeTrue();
    }

    [Fact]
    public void geometry_drawn_first_stays_in_front_and_the_sky_fills_the_rest()
    {
        //Arrange - an unlit red cube in the middle of the view, then the sky
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using EnvironmentMap environment = context.CreateEnvironment(TestEnvironments.Panorama(AxisColour, 512), Small);
        using SkyboxRenderer sky = context.CreateSkyboxRenderer();
        using MeshRenderer meshes = context.CreateMeshRenderer();
        using Mesh cube = context.CreateMesh(MeshShapes.Cube(1f));
        LitMaterial red = meshes.CreateMaterial(Color4.Red);
        red.Unlit = true;
        Camera camera = Looking(-Vector3.UnitZ, 1f);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                meshes.Begin(pass, camera);
                meshes.Draw(cube, red, Matrix4x4.CreateTranslation(0f, 0f, -5f));
                meshes.End();
                sky.Draw(pass, camera, environment);
            }

            frame.Submit();
        }

        //Assert
        byte[] pixels = target.ReadRgba();
        TestPixels.At(pixels, Size, Size / 2, Size / 2).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.Near(TestPixels.At(pixels, Size, 2, 2), 0, ModelPbrTests.Encode(0.5), ModelPbrTests.Encode(0.5), 2).Should().BeTrue();
        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void blur_softens_a_hard_horizon()
    {
        //Arrange - white sky, black ground; look at the horizon
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(d => d.Y > 0f ? Vector3.One : Vector3.Zero, 512), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();

        //Act
        byte[] sharp = Draw(sky, Looking(-Vector3.UnitZ, 1f), environment);
        sky.Blur = 0.6f;
        byte[] soft = Draw(sky, Looking(-Vector3.UnitZ, 1f), environment);
        sky.Blur = 7f;

        //Assert - the largest step between neighbouring rows of the centre column shrinks
        static int Steepest(byte[] pixels)
        {
            int steepest = 0;
            for (int y = 1; y < Size; y++) steepest = Math.Max(steepest, Math.Abs(TestPixels.At(pixels, Size, Size / 2, y).R - TestPixels.At(pixels, Size, Size / 2, y - 1).R));
            return steepest;
        }

        Steepest(sharp).Should().BeGreaterThan(100);
        Steepest(soft).Should().BeLessThan(Steepest(sharp) / 3);
        sky.Blur.Should().Be(1f);
    }

    [Fact]
    public void unorm_srgb_and_float_targets_get_the_same_colours()
    {
        //Arrange
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Panorama(AxisColour, 512), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();
        Camera camera = Looking(Vector3.Normalize(new Vector3(0.5f, 0.3f, -1f)), 1.2f);

        //Act
        byte[] srgb = Draw(sky, camera, environment, TextureFormat.RGBA8UnormSrgb);
        byte[] unorm = Draw(sky, camera, environment, TextureFormat.RGBA8Unorm);
        byte[] linear = Draw(sky, camera, environment, TextureFormat.RGBA16Float);

        //Assert
        int worst = 0;
        for (int index = 0; index < srgb.Length; index++) worst = Math.Max(worst, Math.Abs(srgb[index] - unorm[index]));
        worst.Should().BeLessThanOrEqualTo(2);
        ReadOnlySpan<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(linear);
        int centre = ((Size / 2 * Size) + (Size / 2)) * 4;
        for (int channel = 0; channel < 3; channel++)
        {
            ModelPbrTests.Encode((float)halves[centre + channel]).Should().BeInRange(srgb[centre + channel] - 1, srgb[centre + channel] + 1);
        }
    }

    [Fact]
    public void drawing_without_lighting_shows_the_default_sky_and_allocates_nothing_once_warm()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using SkyboxRenderer sky = context.CreateSkyboxRenderer();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        Camera camera = Looking(Vector3.Normalize(new Vector3(0f, 0.6f, -1f)), 1f);
        long allocated = -1;

        //Act
        for (int round = 0; round < 4; round++)
        {
            using GraphicsFrame frame = context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int draw = 0; draw < 3; draw++) sky.Draw(pass, camera);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            frame.Submit();
        }

        //Assert - a blue sky
        (byte R, byte G, byte B, byte A) centre = TestPixels.At(target.ReadRgba(), Size, Size / 2, Size / 2);
        centre.B.Should().BeGreaterThan(centre.R);
        centre.B.Should().BeGreaterThan((byte)60);
        allocated.Should().Be(0);
        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void a_disposed_environment_cannot_be_drawn()
    {
        //Arrange
        gpu.RequireDevice();
        EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Constant(Vector3.One), Small);
        using SkyboxRenderer sky = gpu.Context.CreateSkyboxRenderer();
        environment.Dispose();

        //Act
        Action draw = () => Draw(sky, Looking(-Vector3.UnitZ), environment);

        //Assert
        draw.Should().Throw<ObjectDisposedException>();
    }
}
