using System;
using System.IO;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Animated Kenney models on the GPU: the prototype-kit indicator's node animation and the archer's skinned clips move
/// the expected pixels over time, and each animated frame is exactly the frame of the same pose set by hand.
/// </summary>
public class ModelAnimationRenderTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 128;
    private readonly GraphicsGpuFixture gpu;

    public ModelAnimationRenderTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static string Asset(string relative) => Path.Combine(AppContext.BaseDirectory, "Assets", relative);

    private byte[] Render(ModelRenderer renderer, Camera camera, params ModelInstance[] instances) =>
        ModelPbrTests.Render(gpu.Context, renderer, camera, new SceneLighting { LightDirection = new Vector3(-0.4f, -0.7f, -0.6f) }, Size, TextureFormat.RGBA8UnormSrgb, Color4.Black, instances);

    private static int LargestDifference(byte[] first, byte[] second)
    {
        int largest = 0;
        for (int index = 0; index < first.Length; index++) largest = Math.Max(largest, Math.Abs(first[index] - second[index]));
        return largest;
    }

    private static int ChangedPixels(byte[] first, byte[] second)
    {
        int changed = 0;
        for (int index = 0; index < first.Length; index += 4)
        {
            int difference = Math.Abs(first[index] - second[index]) + Math.Abs(first[index + 1] - second[index + 1]) + Math.Abs(first[index + 2] - second[index + 2]);
            if (difference > 24) changed++;
        }

        return changed;
    }

    /// <summary>Poses an instance by hand from a clip's channel samples (what the player should produce).</summary>
    private static void PoseByHand(ModelInstance instance, ModelAnimation clip, float time)
    {
        float[] value = new float[4];
        instance.ResetPose();
        foreach (ModelAnimationChannel channel in clip.Channels)
        {
            channel.Sample(time, value);
            NodeTransform local = instance.GetLocalTransform(channel.Node);
            if (channel.Path == AnimationPath.Translation) local.Translation = new Vector3(value[0], value[1], value[2]);
            else if (channel.Path == AnimationPath.Rotation) local.Rotation = new Quaternion(value[0], value[1], value[2], value[3]);
            else if (channel.Path == AnimationPath.Scale) local.Scale = new Vector3(value[0], value[1], value[2]);
            instance.SetLocalTransform(channel.Node, local);
        }
    }

    private static Camera ThreeQuarter(Model model, float distanceFactor)
    {
        float radius = Math.Max((model.BoundsMax - model.BoundsMin).Length() * 0.5f, 0.001f);
        Camera camera = new Camera { FieldOfView = MathF.PI / 5f, Target = model.BoundsCenter, NearPlane = radius * 0.05f, FarPlane = radius * 20f };
        float distance = radius / MathF.Sin(camera.FieldOfView / 2f) * distanceFactor;
        camera.Position = model.BoundsCenter + (Vector3.Normalize(new Vector3(0.3f, 0.9f, 0.5f)) * distance);
        return camera;
    }

    [Fact]
    public void animated_indicator_turns_over_time_exactly_like_the_sampled_pose()
    {
        //Arrange
        gpu.RequireDevice();
        using Model indicator = gpu.Context.LoadModel(Asset("kenney-prototype-kit/indicator-round-b.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = ThreeQuarter(indicator, 1.1f);
        ModelInstance animated = indicator.CreateInstance();
        ModelInstance byHand = indicator.CreateInstance();
        ModelAnimation clip = indicator.Animations[0];
        animated.Animation.Play(0);
        animated.Animation.Evaluate();
        byte[][] frames = new byte[4][];

        //Act
        for (int step = 0; step < frames.Length; step++)
        {
            if (step > 0) animated.Animation.Update(clip.Duration / 8f);
            frames[step] = Render(renderer, camera, animated);
            PoseByHand(byHand, clip, step * clip.Duration / 8f);
            byte[] reference = Render(renderer, camera, byHand);
            LargestDifference(frames[step], reference).Should().Be(0, "frame " + step + " matches the hand-set pose");
        }

        //Assert - the rotation moves pixels between consecutive frames.
        for (int step = 1; step < frames.Length; step++) ChangedPixels(frames[step - 1], frames[step]).Should().BeGreaterThan(20, "frame " + step + " moved");
        int covered = 0;
        for (int index = 0; index < frames[0].Length; index += 4)
        {
            if (frames[0][index] + frames[0][index + 1] + frames[0][index + 2] > 0) covered++;
        }

        covered.Should().BeGreaterThan(500);
    }

    [Fact]
    public void archer_walk_cycle_moves_the_arm_where_cpu_skinning_puts_it()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        int arm = archer.FindNode("arm-left");
        Camera camera = new Camera
        {
            Projection = ProjectionKind.Orthographic,
            OrthographicHeight = (archer.BoundsMax - archer.BoundsMin).Length() * 1.1f,
            Position = archer.BoundsCenter + new Vector3(20, 0, 0),
            Target = archer.BoundsCenter,
            NearPlane = 0.1f,
            FarPlane = 100,
        };
        ModelInstance walker = archer.CreateInstance();
        ModelInstance byHand = archer.CreateInstance();
        ModelAnimation walk = archer.Animations[archer.FindAnimation("walk")];
        walker.Animation.Play(walk.Index);
        walker.Animation.Evaluate();
        float quarter = walk.Duration / 4f;
        byte[][] frames = new byte[4][];
        Vector3[] arms = new Vector3[4];

        //Act
        for (int step = 0; step < frames.Length; step++)
        {
            if (step > 0) walker.Animation.Update(quarter);
            frames[step] = Render(renderer, camera, walker);
            arms[step] = ModelSkinningTests.SkinnedCentroid(archer, walker, arm);
            PoseByHand(byHand, walk, step * quarter);
            LargestDifference(frames[step], Render(renderer, camera, byHand)).Should().BeLessThanOrEqualTo(1, "frame " + step + " matches the hand-set pose");
        }

        //Assert - seen from the side the arm swings: its skinned centroid is drawn in every frame and moves.
        for (int step = 0; step < frames.Length; step++)
        {
            Vector3 screen = camera.WorldToScreen(arms[step], Size, Size);
            (TestPixels.At(frames[step], Size, (int)screen.X, (int)screen.Y) != ((byte)0, (byte)0, (byte)0, (byte)255)).Should().BeTrue("the arm is drawn where CPU skinning puts it in frame " + step);
        }

        (arms[1] - arms[3]).Length().Should().BeGreaterThan(0.01f, "the arm swings one way at a quarter cycle and the other way at three quarters");
        ChangedPixels(frames[1], frames[3]).Should().BeGreaterThan(20);
    }

    [Fact]
    public void many_archers_playing_different_clips_draw_in_one_batch_like_one_at_a_time()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = gpu.Context.LoadModel(Asset("kenney-mini-forest/character-archer.glb"));
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        float width = archer.BoundsMax.X - archer.BoundsMin.X;
        Camera camera = ModelPbrTests.Front((archer.BoundsMax - archer.BoundsMin).Length() * 1.6f, archer.BoundsCenter);
        string[] clips = { "walk", "attack-melee-right", "emote-yes" };
        ModelInstance[] instances = new ModelInstance[clips.Length];
        for (int index = 0; index < instances.Length; index++)
        {
            instances[index] = archer.CreateInstance(Matrix4x4.CreateTranslation((index - 1) * width * 1.3f, 0, 0));
            instances[index].Animation.Play(clips[index], AnimationLoopMode.Loop, 1f, index * 0.11f);
        }

        //Act
        AnimationPlayer.UpdateAll(instances, 0.2f);
        byte[] together = Render(renderer, camera, instances);
        int drawCalls = renderer.LastDrawCallCount;
        byte[] reference = new byte[together.Length];
        for (int index = 0; index < instances.Length; index++)
        {
            byte[] alone = Render(renderer, camera, instances[index]);
            for (int pixel = 0; pixel < alone.Length; pixel += 4)
            {
                if (alone[pixel] + alone[pixel + 1] + alone[pixel + 2] == 0) continue;
                Array.Copy(alone, pixel, reference, pixel, 4);
            }
        }

        for (int pixel = 3; pixel < reference.Length; pixel += 4) reference[pixel] = 255;

        //Assert
        drawCalls.Should().Be(2, "one instanced draw per draw item for all three archers");
        LargestDifference(together, reference).Should().BeLessThanOrEqualTo(1);
    }
}
