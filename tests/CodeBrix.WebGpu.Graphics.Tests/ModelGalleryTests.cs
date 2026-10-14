using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// OPT-IN visual and timing checks of the model renderer, skipped by default:
/// CODEBRIX_WEBGPU_RENDER_OUTPUT names a folder that receives PNG renders of the committed test models (and, with
/// CODEBRIX_WEBGPU_POLYHAVEN naming a Poly Haven download folder, a few Poly Haven models read in place);
/// CODEBRIX_WEBGPU_MODEL_TIMING names a text file that receives per-frame costs for many instances;
/// CODEBRIX_WEBGPU_ANIMATION_TIMING names a text file that receives animation update costs for many instances. The
/// render folder also receives animation frame strips (four frames side by side).
/// </summary>
public class ModelGalleryTests : IClassFixture<GraphicsGpuFixture>
{
    private const string OutputVariable = "CODEBRIX_WEBGPU_RENDER_OUTPUT";
    private const string PolyHavenVariable = "CODEBRIX_WEBGPU_POLYHAVEN";
    private const string TimingVariable = "CODEBRIX_WEBGPU_MODEL_TIMING";
    private const string AnimationTimingVariable = "CODEBRIX_WEBGPU_ANIMATION_TIMING";
    private const int Size = 512;
    private static readonly Color4 Backdrop = new Color4(0.18f, 0.2f, 0.23f);
    private readonly GraphicsGpuFixture gpu;

    public ModelGalleryTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static string Asset(string relative) => Path.Combine(AppContext.BaseDirectory, "Assets", relative);

    private static SceneLighting StudioLighting()
    {
        SceneLighting lighting = new SceneLighting
        {
            Ambient = AmbientLighting.Hemisphere,
            LightDirection = new Vector3(-0.5f, -0.8f, -0.6f),
            LightColor = new Color4(1f, 0.96f, 0.9f),
            LightIntensity = 2.2f,
            AmbientColor = new Color4(0.32f, 0.36f, 0.42f),
            AmbientGroundColor = new Color4(0.12f, 0.1f, 0.08f),
        };
        lighting.AdditionalLights.Add(new DirectionalLight(new Vector3(0.7f, -0.3f, 0.4f), new Color4(0.6f, 0.7f, 1f), 0.6f));
        return lighting;
    }

    private static Camera ThreeQuarter(Model model)
    {
        float radius = Math.Max((model.BoundsMax - model.BoundsMin).Length() * 0.5f, 0.001f);
        Camera camera = new Camera { FieldOfView = MathF.PI / 5f, Target = model.BoundsCenter, NearPlane = radius * 0.05f, FarPlane = radius * 20f };
        float distance = radius / MathF.Sin(camera.FieldOfView / 2f) * 1.05f;
        camera.Position = model.BoundsCenter + (Vector3.Normalize(new Vector3(0.75f, 0.45f, 1f)) * distance);
        return camera;
    }

    private void Save(string folder, string name, ModelRenderer renderer, Camera camera, params ModelInstance[] instances)
    {
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Backdrop))
            {
                renderer.Begin(pass, camera, StudioLighting());
                foreach (ModelInstance instance in instances) renderer.Draw(instance);
                renderer.End();
            }

            frame.Submit();
        }

        context.TakeErrors().Should().BeEmpty();
        target.SaveAsPng(Path.Combine(folder, name + ".png"));
    }

    [Fact]
    public void renders_the_test_models_for_a_visual_check()
    {
        //Arrange
        string folder = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "Opt-in: set " + OutputVariable + " to a folder to receive PNG renders of the test models.");
        gpu.RequireDevice();
        Directory.CreateDirectory(folder);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        List<string> written = new List<string>();

        //Act
        foreach ((string name, string file) in new[]
        {
            ("wet-floor-sign", Asset("polyhaven-wet-floor-sign/WetFloorSign_01_2k.gltf")),
            ("kenney-tree", Asset("kenney-mini-forest/tree.glb")),
            ("kenney-grass-unlit", Asset("kenney-retro-urban-kit/grass.glb")),
        })
        {
            using Model model = gpu.Context.LoadModel(file);
            Save(folder, name, renderer, ThreeQuarter(model), model.CreateInstance());
            renderer.Forget(model);
            written.Add(name);
        }

        using (Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb")))
        {
            Camera camera = ThreeQuarter(archer);
            Save(folder, "kenney-archer-bind-pose", renderer, camera, archer.CreateInstance());
            ModelInstance posed = archer.CreateInstance();
            Rotate(posed, archer.FindNode("arm-left"), Vector3.UnitZ, 1.2f);
            Rotate(posed, archer.FindNode("arm-right"), Vector3.UnitX, -1.0f);
            Rotate(posed, archer.FindNode("head"), Vector3.UnitY, 0.6f);
            Rotate(posed, archer.FindNode("leg-left"), Vector3.UnitX, 0.5f);
            Rotate(posed, archer.FindNode("leg-right"), Vector3.UnitX, -0.5f);
            Save(folder, "kenney-archer-posed", renderer, camera, posed);
            written.Add("kenney-archer-bind-pose");
            written.Add("kenney-archer-posed");
        }

        string polyHaven = Environment.GetEnvironmentVariable(PolyHavenVariable);
        if (!string.IsNullOrWhiteSpace(polyHaven))
        {
            foreach (string name in new[] { "GreenChair_01", "Television_01", "brass_pot_02", "cannon_01" })
            {
                string file = Path.Combine(polyHaven, name, name + "_2k.gltf");
                if (!File.Exists(file)) continue;
                using Model model = gpu.Context.LoadModel(file);
                Save(folder, "polyhaven-" + name, renderer, ThreeQuarter(model), model.CreateInstance());
                renderer.Forget(model);
                written.Add("polyhaven-" + name);
            }
        }

        //Assert
        foreach (string name in written) File.Exists(Path.Combine(folder, name + ".png")).Should().BeTrue();
    }

    private static void Rotate(ModelInstance instance, int node, Vector3 axis, float angle)
    {
        NodeTransform local = instance.GetLocalTransform(node);
        instance.SetLocalTransform(node, new NodeTransform(local.Translation, Quaternion.Concatenate(local.Rotation, Quaternion.CreateFromAxisAngle(axis, angle)), local.Scale));
    }

    [Fact]
    public void reports_per_frame_cost_for_many_instances()
    {
        //Arrange
        string report = Environment.GetEnvironmentVariable(TimingVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(report), "Opt-in: set " + TimingVariable + " to a file that receives per-frame costs of the model renderer.");
        gpu.RequireDevice();
        StringBuilder text = new StringBuilder();
        text.AppendLine("Model renderer per-frame cost, " + Size + " x " + Size + " RGBA8UnormSrgb + Depth32Float, " + gpu.Context.AdapterInfo.Name + " (" + gpu.Context.Backend + ")");
        text.AppendLine("record = Begin + Draw x N + End on the CPU (incl. palettes and uploads); frame = record + submit + wait for the GPU");
        using Model tree = gpu.Context.LoadModel(Asset("kenney-mini-forest/tree.glb"));
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"));

        //Act
        foreach (int count in new[] { 1, 100, 1000 }) text.AppendLine(Measure("tree (static)", tree, count, false));
        foreach (int count in new[] { 1, 50, 500 }) text.AppendLine(Measure("archer (skinned, posed every frame)", archer, count, true));
        File.WriteAllText(report, text.ToString());

        //Assert
        File.Exists(report).Should().BeTrue();
    }

    private string Measure(string name, Model model, int count, bool animate)
    {
        GraphicsContext context = gpu.Context;
        using ModelRenderer renderer = context.CreateModelRenderer();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        float spacing = (model.BoundsMax - model.BoundsMin).Length();
        ModelInstance[] instances = new ModelInstance[count];
        for (int index = 0; index < count; index++) instances[index] = model.CreateInstance(Matrix4x4.CreateTranslation(((index % side) - (side / 2f)) * spacing, 0, -(index / side) * spacing));
        Camera camera = new Camera { Position = new Vector3(0, side * spacing * 0.6f, side * spacing * 0.4f + spacing * 2), Target = new Vector3(0, 0, -side * spacing * 0.4f), NearPlane = 0.05f, FarPlane = side * spacing * 4 + 50 };
        SceneLighting lighting = StudioLighting();
        int arm = model.FindNode("arm-left");
        const int Warm = 20;
        const int Frames = 200;
        double record = 0;
        Stopwatch total = new Stopwatch();
        for (int frameIndex = 0; frameIndex < Warm + Frames; frameIndex++)
        {
            if (frameIndex == Warm) total.Restart();
            using GraphicsFrame frame = context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Backdrop))
            {
                long start = Stopwatch.GetTimestamp();
                renderer.Begin(pass, camera, lighting);
                for (int index = 0; index < count; index++)
                {
                    ModelInstance instance = instances[index];
                    if (animate && arm >= 0)
                    {
                        using ModelPoseEdit pose = instance.BeginPoseEdit();
                        pose.LocalTransforms[arm].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Sin((frameIndex * 0.1f) + index));
                    }

                    renderer.Draw(instance);
                }

                renderer.End();
                if (frameIndex >= Warm) record += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            frame.Submit();
            context.WhenSubmittedWorkDoneAsync().GetAwaiter().GetResult();
        }

        double frameMs = total.Elapsed.TotalMilliseconds / Frames;
        context.TakeErrors().Should().BeEmpty();
        return string.Create(CultureInfo.InvariantCulture, $"{name,-38} N={count,5}: record {record / Frames,7:F3} ms, frame {frameMs,7:F3} ms, draw calls {renderer.LastDrawCallCount}, primitive instances {renderer.LastInstanceCount}, triangles {(long)model.TriangleCount * count}");
    }

    [Fact]
    public void renders_animation_frame_strips_for_a_visual_check()
    {
        //Arrange
        string folder = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(folder), "Opt-in: set " + OutputVariable + " to a folder to receive animation frame strips.");
        gpu.RequireDevice();
        Directory.CreateDirectory(folder);
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"));
        using Model indicator = gpu.Context.LoadModel(Asset("kenney-prototype-kit/indicator-round-b.glb"));
        List<string> written = new List<string>();

        //Act
        foreach (string clip in new[] { "walk", "sprint", "attack-melee-right", "jump" })
        {
            ModelInstance instance = archer.CreateInstance();
            instance.Animation.Play(clip);
            float step = archer.Animations[archer.FindAnimation(clip)].Duration / 4f;
            SaveStrip(folder, "kenney-archer-" + clip, renderer, ThreeQuarter(archer), instance, step);
            written.Add("kenney-archer-" + clip);
        }

        ModelInstance fading = archer.CreateInstance();
        fading.Animation.Play("walk");
        fading.Animation.CrossFade("attack-melee-right", 0.3f);
        SaveStrip(folder, "kenney-archer-crossfade-walk-to-attack", renderer, ThreeQuarter(archer), fading, 0.1f);
        written.Add("kenney-archer-crossfade-walk-to-attack");

        ModelInstance spinning = indicator.CreateInstance();
        spinning.Animation.Play(0);
        SaveStrip(folder, "kenney-indicator", renderer, ThreeQuarter(indicator), spinning, indicator.Animations[0].Duration / 4f);
        written.Add("kenney-indicator");

        //Assert
        foreach (string name in written) File.Exists(Path.Combine(folder, name + ".png")).Should().BeTrue();
    }

    private void SaveStrip(string folder, string name, ModelRenderer renderer, Camera camera, ModelInstance instance, float step)
    {
        const int Frames = 4;
        const int Cell = 256;
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Cell, Cell, TextureFormat.RGBA8UnormSrgb);
        using Image<Rgba32> strip = new Image<Rgba32>(Cell * Frames, Cell);
        instance.Animation.Evaluate();
        for (int frameIndex = 0; frameIndex < Frames; frameIndex++)
        {
            if (frameIndex > 0) instance.Animation.Update(step);
            using (GraphicsFrame frame = context.BeginFrame())
            {
                using (RenderPass pass = frame.BeginRenderPass(target.View, Backdrop))
                {
                    renderer.Begin(pass, camera, StudioLighting());
                    renderer.Draw(instance);
                    renderer.End();
                }

                frame.Submit();
            }

            byte[] pixels = target.ReadRgba();
            for (int y = 0; y < Cell; y++)
            {
                for (int x = 0; x < Cell; x++)
                {
                    int at = ((y * Cell) + x) * 4;
                    strip[(frameIndex * Cell) + x, y] = new Rgba32(pixels[at], pixels[at + 1], pixels[at + 2], 255);
                }
            }
        }

        context.TakeErrors().Should().BeEmpty();
        using FileStream file = File.Create(Path.Combine(folder, name + ".png"));
        strip.SaveAsPng(file);
    }

    [Fact]
    public void reports_animation_update_cost_for_many_instances()
    {
        //Arrange
        string report = Environment.GetEnvironmentVariable(AnimationTimingVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(report), "Opt-in: set " + AnimationTimingVariable + " to a file that receives animation update costs.");
        gpu.RequireDevice();
        StringBuilder text = new StringBuilder();
        text.AppendLine("Animation update cost per frame, " + gpu.Context.AdapterInfo.Name + " (" + gpu.Context.Backend + "), " + Size + " x " + Size + " RGBA8UnormSrgb + depth");
        text.AppendLine("update = AnimationPlayer.UpdateAll (advance + sample + write the pose); pose = update + node matrices + joint palettes;");
        text.AppendLine("frame = update + record + submit + wait for the GPU (the renderer computes matrices/palettes while recording)");
        using Model indicator = gpu.Context.LoadModel(Asset("kenney-prototype-kit/indicator-round-b.glb"));
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"));
        string[] clips = { "walk", "idle", "sprint", "attack-melee-right", "emote-yes", "jump", "interact-left", "attack-kick-right" };

        //Act
        foreach (int count in new[] { 1, 100, 1000 }) text.AppendLine(MeasureAnimation("indicator (1 node channel)", indicator, count, new[] { (string)null }, false));
        foreach (int count in new[] { 1, 50, 500 }) text.AppendLine(MeasureAnimation("archer (8 clips, varied times, skinned)", archer, count, clips, false));
        foreach (int count in new[] { 1, 50, 500 }) text.AppendLine(MeasureAnimation("archer (as above, always cross-fading)", archer, count, clips, true));
        File.WriteAllText(report, text.ToString());

        //Assert
        File.Exists(report).Should().BeTrue();
    }

    private string MeasureAnimation(string name, Model model, int count, string[] clips, bool crossFade)
    {
        GraphicsContext context = gpu.Context;
        using ModelRenderer renderer = context.CreateModelRenderer();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        float spacing = (model.BoundsMax - model.BoundsMin).Length();
        ModelInstance[] instances = new ModelInstance[count];
        for (int index = 0; index < count; index++)
        {
            instances[index] = model.CreateInstance(Matrix4x4.CreateTranslation(((index % side) - (side / 2f)) * spacing, 0, -(index / side) * spacing));
            string clip = clips[index % clips.Length];
            if (clip == null) instances[index].Animation.Play(0, AnimationLoopMode.Loop, 1f, index * 0.013f);
            else instances[index].Animation.Play(clip, AnimationLoopMode.Loop, 1f, index * 0.013f);
        }

        Camera camera = new Camera { Position = new Vector3(0, side * spacing * 0.6f, side * spacing * 0.4f + spacing * 2), Target = new Vector3(0, 0, -side * spacing * 0.4f), NearPlane = 0.05f, FarPlane = side * spacing * 4 + 50 };
        SceneLighting lighting = StudioLighting();
        const int Warm = 20;
        const int Frames = 200;
        const float Delta = 1f / 60f;
        double update = 0;
        double pose = 0;
        Stopwatch total = new Stopwatch();
        for (int frameIndex = 0; frameIndex < Warm + Frames; frameIndex++)
        {
            if (frameIndex == Warm) total.Restart();
            if (crossFade)
            {
                for (int index = frameIndex % 10; index < count; index += 10) instances[index].Animation.CrossFade(clips[(frameIndex + index) % clips.Length], 0.25f);
            }

            long start = Stopwatch.GetTimestamp();
            AnimationPlayer.UpdateAll(instances, Delta);
            double updated = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            for (int index = 0; index < count; index++)
            {
                _ = instances[index].NodeMatrices;
                if (model.Skins.Count > 0) _ = instances[index].GetJointPalette(0);
            }

            double posed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frameIndex >= Warm)
            {
                update += updated;
                pose += posed;
            }

            using GraphicsFrame frame = context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Backdrop))
            {
                renderer.Begin(pass, camera, lighting);
                for (int index = 0; index < count; index++) renderer.Draw(instances[index]);
                renderer.End();
            }

            frame.Submit();
            context.WhenSubmittedWorkDoneAsync().GetAwaiter().GetResult();
        }

        double frameMs = total.Elapsed.TotalMilliseconds / Frames;
        context.TakeErrors().Should().BeEmpty();
        return string.Create(CultureInfo.InvariantCulture, $"{name,-40} N={count,5}: update {update / Frames,7:F3} ms ({update / Frames / count * 1000,6:F2} us/instance), pose {pose / Frames,7:F3} ms, frame {frameMs,7:F3} ms, draw calls {renderer.LastDrawCallCount}");
    }
}
