using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// OPT-IN visual and timing checks of image-based lighting, skipped by default: CODEBRIX_WEBGPU_RENDER_OUTPUT names a
/// folder that receives PNGs (the committed models and a metal / roughness sphere grid under the Poly Haven
/// small_cathedral HDRI with its skybox, and under the procedural sky; with CODEBRIX_WEBGPU_POLYHAVEN naming a Poly Haven
/// download folder, a few Poly Haven models read in place); CODEBRIX_WEBGPU_ENVIRONMENT_TIMING names a text file that
/// receives environment build times and per-frame costs.
/// </summary>
public class EnvironmentGalleryTests : IClassFixture<GraphicsGpuFixture>
{
    private const string OutputVariable = "CODEBRIX_WEBGPU_RENDER_OUTPUT";
    private const string PolyHavenVariable = "CODEBRIX_WEBGPU_POLYHAVEN";
    private const string TimingVariable = "CODEBRIX_WEBGPU_ENVIRONMENT_TIMING";
    private const int Size = 512;
    private readonly GraphicsGpuFixture gpu;

    public EnvironmentGalleryTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static string Asset(string relative) => Path.Combine(AppContext.BaseDirectory, "Assets", relative);

    private static Camera ThreeQuarter(Vector3 min, Vector3 max, float elevation = 0.35f, float fieldOfView = MathF.PI / 5f)
    {
        Vector3 center = (min + max) * 0.5f;
        float radius = Math.Max((max - min).Length() * 0.5f, 0.001f);
        Camera camera = new Camera { FieldOfView = fieldOfView, Target = center, NearPlane = radius * 0.05f, FarPlane = radius * 40f };
        float distance = radius / MathF.Sin(camera.FieldOfView / 2f) * 1.05f;
        camera.Position = center + (Vector3.Normalize(new Vector3(0.75f, elevation, 1f)) * distance);
        return camera;
    }

    /// <summary>The HDR path: models + skybox into RGBA16Float, then a tone map into an sRGB target saved as PNG.</summary>
    private void Save(string path, ModelRenderer renderer, SkyboxRenderer sky, ToneMapper toneMapper, Camera camera, SceneLighting lighting, params ModelInstance[] instances)
    {
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget hdr = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA16Float);
        using HeadlessRenderTarget final = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb, false);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(hdr.View, Color4.Black))
            {
                renderer.Begin(pass, camera, lighting);
                foreach (ModelInstance instance in instances) renderer.Draw(instance);
                renderer.End();
                sky.Draw(pass, camera, lighting);
            }

            toneMapper.Apply(frame, hdr.Color, final.View);
            frame.Submit();
        }

        context.TakeErrors().Should().BeEmpty();
        final.SaveAsPng(path);
    }

    private static SceneLighting Cathedral(EnvironmentMap cathedral) => new SceneLighting { Environment = cathedral, LightIntensity = 0f, EnvironmentRotation = 0.6f };

    private static SceneLighting Sky() => new SceneLighting { LightIntensity = 2.2f, LightColor = new Color4(1f, 0.95f, 0.86f) };

    [Fact]
    public void renders_models_and_spheres_under_the_hdri_and_the_procedural_sky()
    {
        //Arrange
        string folder = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "Opt-in: set " + OutputVariable + " to a folder to receive PNG renders under environments.");
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        Directory.CreateDirectory(folder);
        using EnvironmentMap cathedral = context.CreateEnvironment(TestEnvironments.SmallCathedral);
        using ModelRenderer renderer = context.CreateModelRenderer();
        using SkyboxRenderer sky = context.CreateSkyboxRenderer();
        using ToneMapper toneMapper = context.CreateToneMapper();
        toneMapper.Operator = ToneMapOperator.KhronosPbrNeutral;
        List<string> written = new List<string>();

        List<(string Name, string File)> models = new List<(string, string)> { ("WetFloorSign_01", Asset("polyhaven-wet-floor-sign/WetFloorSign_01_2k.gltf")) };
        string polyHaven = Environment.GetEnvironmentVariable(PolyHavenVariable);
        if (!string.IsNullOrWhiteSpace(polyHaven))
        {
            foreach (string name in new[] { "GreenChair_01", "Television_01", "brass_pot_02", "cannon_01" })
            {
                string file = Path.Combine(polyHaven, name, name + "_2k.gltf");
                if (File.Exists(file)) models.Add((name, file));
            }
        }

        //Act
        foreach ((string name, string file) in models)
        {
            using Model model = context.LoadModel(file);
            Camera camera = ThreeQuarter(model.BoundsMin, model.BoundsMax);
            toneMapper.Exposure = 1.6f;
            Save(Path.Combine(folder, "polyhaven-" + name + "-cathedral.png"), renderer, sky, toneMapper, camera, Cathedral(cathedral), model.CreateInstance());
            toneMapper.Exposure = 1f;
            Save(Path.Combine(folder, "polyhaven-" + name + "-procedural-sky.png"), renderer, sky, toneMapper, ThreeQuarter(model.BoundsMin, model.BoundsMax, 0.12f), Sky(), model.CreateInstance());
            renderer.Forget(model);
            written.Add("polyhaven-" + name + "-cathedral");
            written.Add("polyhaven-" + name + "-procedural-sky");
        }

        foreach ((string name, string file) in new[] { ("archer", Asset("kenney-mini-forest/character-archer.glb")), ("tree", Asset("kenney-mini-forest/tree.glb")) })
        {
            using Model model = context.LoadModel(file);
            ModelInstance instance = model.CreateInstance();
            if (model.Animations.Count > 0)
            {
                instance.Animation.Play("walk", AnimationLoopMode.Loop, 1f, 0.2f);
                instance.Animation.Evaluate();
            }

            Save(Path.Combine(folder, "kenney-" + name + "-procedural-sky.png"), renderer, sky, toneMapper, ThreeQuarter(model.BoundsMin, model.BoundsMax, 0.12f), Sky(), instance);
            renderer.Forget(model);
            written.Add("kenney-" + name + "-procedural-sky");
        }

        using (Model grid = SphereGrid())
        {
            Camera camera = new Camera { Position = new Vector3(0f, 0f, 17f), Target = Vector3.Zero, FieldOfView = 0.62f, NearPlane = 0.5f, FarPlane = 100f };
            toneMapper.Exposure = 1.6f;
            Save(Path.Combine(folder, "spheres-metal-roughness-cathedral.png"), renderer, sky, toneMapper, camera, Cathedral(cathedral), grid.CreateInstance());
            toneMapper.Exposure = 1f;
            Save(Path.Combine(folder, "spheres-metal-roughness-procedural-sky.png"), renderer, sky, toneMapper, camera, Sky(), grid.CreateInstance());
            written.Add("spheres-metal-roughness-cathedral");
            written.Add("spheres-metal-roughness-procedural-sky");
        }

        //Assert
        foreach (string name in written) File.Exists(Path.Combine(folder, name + ".png")).Should().BeTrue();
    }

    /// <summary>Three rows of five spheres: white dielectric, red dielectric, gold metal; roughness 0, 0.25, 0.5, 0.75, 1 left to right.</summary>
    private Model SphereGrid()
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Vector4[] colours = { new Vector4(0.9f, 0.9f, 0.9f, 1f), new Vector4(0.8f, 0.08f, 0.06f, 1f), new Vector4(1f, 0.77f, 0.34f, 1f) };
        float[] metals = { 0f, 0f, 1f };
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 5; column++)
            {
                float roughness = column / 4f;
                Gltf.Material material = TestGltf.Material(root, "r" + row + "c" + column, colours[row], metals[row], roughness);
                TestGltf.AddMesh(root, "sphere r" + row + "c" + column, material, TestGltf.Sphere(0.9f, 64, 32), new Vector3((column - 2) * 2.1f, (1 - row) * 2.1f, 0f));
            }
        }

        return TestGltf.Load(gpu.Context, root, "sphere grid");
    }

    [Fact]
    public void reports_environment_build_times_and_per_frame_costs()
    {
        //Arrange
        string report = Environment.GetEnvironmentVariable(TimingVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(report), "Opt-in: set " + TimingVariable + " to a file that receives environment build times and per-frame costs.");
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        StringBuilder text = new StringBuilder();
        text.AppendLine("Environment build times and per-frame costs, " + context.AdapterInfo.Name + " (" + context.Backend + ")");
        context.WaitIdle();

        //Act - the LUT once, then decode and bakes (each: create + wait for the GPU)
        GpuTexture lut = context.BrdfLut;
        using (GraphicsContext fresh = GraphicsContext.Create())
        {
            Stopwatch lutWatch = Stopwatch.StartNew();
            GpuTexture freshLut = fresh.BrdfLut;
            fresh.WaitIdle();
            text.AppendLine(Line("BRDF LUT 128 x 128 (4096 samples), once per context", lutWatch.Elapsed.TotalMilliseconds));
            freshLut.IsDisposed.Should().BeFalse();
        }

        Stopwatch watch = Stopwatch.StartNew();
        FloatImage panorama = RadianceHdrDecoder.DecodeFile(TestEnvironments.SmallCathedral);
        text.AppendLine(Line("decode small_cathedral_1k.hdr (1024 x 512, CPU)", watch.Elapsed.TotalMilliseconds));
        foreach (int cube in new[] { 256, 512, 1024 })
        {
            Bake("small_cathedral cube " + cube + " (defaults otherwise)", text, () => context.CreateEnvironment(panorama, new EnvironmentOptions { CubeSize = cube }));
        }

        Bake("small_cathedral cube 1024, prefiltered 512, 256 samples", text, () => context.CreateEnvironment(panorama, new EnvironmentOptions { CubeSize = 1024, PrefilteredSize = 512, SpecularSampleCount = 256 }));
        Bake("procedural sky cube 256 (the default sky)", text, () => context.CreateProceduralSky(null, new EnvironmentOptions { CubeSize = 256, PrefilteredSize = 128 }));
        Bake("procedural sky cube 512", text, () => context.CreateProceduralSky());

        using Model tree = context.LoadModel(Asset("kenney-mini-forest/tree.glb"));
        using EnvironmentMap cathedral = context.CreateEnvironment(panorama);
        foreach (int count in new[] { 1, 100, 1000 })
        {
            text.AppendLine(Frame("tree, hemispheric ambient", tree, count, new SceneLighting { Ambient = AmbientLighting.Hemisphere }, false));
            text.AppendLine(Frame("tree, static environment", tree, count, new SceneLighting { Environment = cathedral }, false));
            text.AppendLine(Frame("tree, environment turning every frame", tree, count, new SceneLighting { Environment = cathedral }, true));
        }

        text.AppendLine(Frame("skybox only (512 x 512)", null, 0, new SceneLighting { Environment = cathedral }, false));
        File.WriteAllText(report, text.ToString());

        //Assert
        lut.IsDisposed.Should().BeFalse();
        File.Exists(report).Should().BeTrue();
    }

    private static string Line(string name, double milliseconds) => string.Create(CultureInfo.InvariantCulture, $"{name,-62} {milliseconds,9:F1} ms");

    private void Bake(string name, StringBuilder text, Func<EnvironmentMap> build)
    {
        // Warm once (pipelines), then time the median of five.
        build().Dispose();
        gpu.Context.WaitIdle();
        double[] times = new double[5];
        for (int run = 0; run < times.Length; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            using EnvironmentMap environment = build();
            gpu.Context.WaitIdle();
            times[run] = watch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        text.AppendLine(Line("bake " + name, times[2]));
    }

    private string Frame(string name, Model model, int count, SceneLighting lighting, bool turn)
    {
        GraphicsContext context = gpu.Context;
        using ModelRenderer renderer = context.CreateModelRenderer();
        using SkyboxRenderer sky = context.CreateSkyboxRenderer();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        ModelInstance[] instances = new ModelInstance[count];
        float spacing = model == null ? 1f : (model.BoundsMax - model.BoundsMin).Length();
        int side = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
        for (int index = 0; index < count; index++) instances[index] = model.CreateInstance(Matrix4x4.CreateTranslation(((index % side) - (side / 2f)) * spacing, 0, -(index / side) * spacing));
        Camera camera = new Camera { Position = new Vector3(0, side * spacing * 0.6f, (side * spacing * 0.4f) + (spacing * 2)), Target = new Vector3(0, 0, -side * spacing * 0.4f), NearPlane = 0.05f, FarPlane = (side * spacing * 4) + 50 };
        const int Warm = 20;
        const int Frames = 200;
        double record = 0;
        Stopwatch total = new Stopwatch();
        for (int frameIndex = 0; frameIndex < Warm + Frames; frameIndex++)
        {
            if (frameIndex == Warm) total.Restart();
            if (turn) lighting.EnvironmentRotation = frameIndex * 0.01f;
            using GraphicsFrame frame = context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                long start = Stopwatch.GetTimestamp();
                if (model != null)
                {
                    renderer.Begin(pass, camera, lighting);
                    foreach (ModelInstance instance in instances) renderer.Draw(instance);
                    renderer.End();
                }
                else
                {
                    sky.Draw(pass, camera, lighting);
                }

                if (frameIndex >= Warm) record += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            frame.Submit();
            context.WhenSubmittedWorkDoneAsync().GetAwaiter().GetResult();
        }

        context.TakeErrors().Should().BeEmpty();
        return string.Create(CultureInfo.InvariantCulture, $"{name,-40} N={count,5}: record {record / Frames,7:F3} ms, frame {total.Elapsed.TotalMilliseconds / Frames,7:F3} ms");
    }
}
