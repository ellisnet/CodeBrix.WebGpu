using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Animation playback on model instances with known answers: clip lookup, loop / once / ping-pong, pause, speed,
/// seeking, STEP and CUBICSPLINE clips read from a glTF, cross-fade weights, layered and weighted blending (with
/// quaternion hemisphere correction), morph-weight channels, independent instances, the "finished" callback, zero
/// allocation per update and the pose-edit contract of <see cref="ModelInstance" />.
/// </summary>
public class AnimationPlayerTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Quad = 0;
    private const int Mover = 1;
    private const int Spinner = 2;
    private static readonly Quaternion Turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
    private readonly GraphicsGpuFixture gpu;

    public AnimationPlayerTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    /// <summary>
    /// A quad with two morph targets (node 0), a "mover" (node 1) and its child "spinner" (node 2, rest translation
    /// (0, 1, 0)), with clips: slide (mover x 0 -> 4 over 2 s), lift (mover y 0 -> 2 and spinner scale 1 -> 3 over
    /// 1 s), turn (spinner 0 -> 90 degrees about Y over 1 s), still (spinner held at identity), turnNegated (spinner
    /// held at -q(90 degrees), the same rotation), steps (STEP mover x 0, 1, 2 at 0, 0.5, 1 s), curve (CUBICSPLINE
    /// mover x = t^3 at keys 0 and 2 s) and blink (quad weights [0, 0] -> [1, 0.5] over 1 s).
    /// </summary>
    private Model BuildModel()
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.MeshPrimitive primitive = TestGltf.AddMesh(root, "quad", TestGltf.Material(root, "white", Vector4.One, 0f, 1f), TestGltf.Quad());
        primitive.SetMorphTargetAccessors(0, new Dictionary<string, Gltf.Accessor> { ["POSITION"] = Deltas(root, "lift", Vector3.UnitY) });
        primitive.SetMorphTargetAccessors(1, new Dictionary<string, Gltf.Accessor> { ["POSITION"] = Deltas(root, "push", Vector3.UnitX) });
        primitive.LogicalParent.SetMorphWeights(new[] { 0f, 0f });
        Gltf.Node quad = root.LogicalNodes[0];
        Gltf.Node mover = root.UseScene(0).CreateNode("mover");
        Gltf.Node spinner = mover.CreateNode("spinner");
        spinner.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);

        root.CreateAnimation("slide").CreateTranslationChannel(mover, new Dictionary<float, Vector3> { [0f] = Vector3.Zero, [2f] = new Vector3(4, 0, 0) });
        Gltf.Animation lift = root.CreateAnimation("lift");
        lift.CreateTranslationChannel(mover, new Dictionary<float, Vector3> { [0f] = Vector3.Zero, [1f] = new Vector3(0, 2, 0) });
        lift.CreateScaleChannel(spinner, new Dictionary<float, Vector3> { [0f] = Vector3.One, [1f] = new Vector3(3) });
        root.CreateAnimation("turn").CreateRotationChannel(spinner, new Dictionary<float, Quaternion> { [0f] = Quaternion.Identity, [1f] = Turned });
        root.CreateAnimation("still").CreateRotationChannel(spinner, new Dictionary<float, Quaternion> { [0f] = Quaternion.Identity, [1f] = Quaternion.Identity });
        root.CreateAnimation("turnNegated").CreateRotationChannel(spinner, new Dictionary<float, Quaternion> { [0f] = -Turned, [1f] = -Turned });
        root.CreateAnimation("steps").CreateTranslationChannel(mover, new Dictionary<float, Vector3> { [0f] = Vector3.Zero, [0.5f] = Vector3.UnitX, [1f] = new Vector3(2, 0, 0) }, false);
        root.CreateAnimation("curve").CreateTranslationChannel(mover, new Dictionary<float, (Vector3, Vector3, Vector3)>
        {
            [0f] = (Vector3.Zero, Vector3.Zero, Vector3.Zero),
            [2f] = (new Vector3(12, 0, 0), new Vector3(8, 0, 0), new Vector3(12, 0, 0)),
        });
        root.CreateAnimation("blink").CreateMorphChannel(quad, new Dictionary<float, float[]> { [0f] = new[] { 0f, 0f }, [1f] = new[] { 1f, 0.5f } }, 2);
        return TestGltf.Load(gpu.Context, root, "animated");
    }

    private static Gltf.Accessor Deltas(Gltf.ModelRoot root, string name, Vector3 delta)
    {
        Gltf.Accessor accessor = root.CreateAccessor(name);
        accessor.SetZeros(4, CodeBrix.Graphics3D.Gltf2.Memory.AttributeFormat.Float3);
        accessor.CreateSparseData(new Dictionary<int, Vector3> { [0] = delta, [1] = delta, [2] = delta, [3] = delta });
        return accessor;
    }

    private Model LoadArcher() => gpu.Context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "character-archer.glb"));

    private static void ShouldBe(Vector3 actual, Vector3 expected, float tolerance = 1e-4f) =>
        (actual - expected).Length().Should().BeLessThan(tolerance, "expected " + expected + " but found " + actual);

    private static void ShouldBeRotation(Quaternion actual, Quaternion expected, float tolerance = 1e-4f) =>
        Math.Abs(Quaternion.Dot(actual, expected)).Should().BeApproximately(1f, tolerance, "expected " + expected + " but found " + actual);

    [Fact]
    public void clips_are_listed_with_names_and_durations_and_play_by_name_or_index()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();

        //Act
        AnimationState byName = instance.Animation.Play("lift");
        AnimationState byIndex = instance.Animation.Play(model.FindAnimation("slide"));
        Action unknown = () => instance.Animation.Play("missing");

        //Assert
        model.Animations.Count.Should().Be(8);
        model.Animations[model.FindAnimation("slide")].Duration.Should().Be(2f);
        model.Animations[model.FindAnimation("lift")].Duration.Should().Be(1f);
        byName.Should().BeSameAs(byIndex, "the stopped state is reused for the next clip");
        byIndex.Name.Should().Be("slide");
        instance.Animation.StateCount.Should().Be(1);
        instance.Animation.FindState(model.FindAnimation("slide")).Should().BeSameAs(byIndex);
        unknown.Should().Throw<ArgumentException>();
        instance.HasAnimationPlayer.Should().BeTrue();
        model.CreateInstance().HasAnimationPlayer.Should().BeFalse();
    }

    [Fact]
    public void looping_clip_poses_the_node_and_wraps()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationState state = instance.Animation.Play("slide");

        //Act
        instance.Animation.Update(0.5f);
        Vector3 early = instance.LocalTransforms[Mover].Translation;
        Matrix4x4 matrix = instance.NodeMatrices[Spinner];
        instance.Animation.Update(1.75f);

        //Assert
        ShouldBe(early, new Vector3(1, 0, 0));
        ShouldBe(matrix.Translation, new Vector3(1, 1, 0));
        state.Time.Should().BeApproximately(0.25f, 1e-5f);
        state.LoopCount.Should().Be(1);
        state.LoopedThisUpdate.Should().BeTrue();
        ShouldBe(instance.LocalTransforms[Mover].Translation, new Vector3(0.5f, 0, 0));
        ShouldBe(instance.NodeMatrices[Spinner].Translation, new Vector3(0.5f, 1, 0));
    }

    [Fact]
    public void once_holds_the_last_frame_and_reports_finishing_once()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        int finished = 0;
        instance.Animation.ClipFinished = _ => finished++;
        AnimationState state = instance.Animation.Play("lift", AnimationLoopMode.Once);

        //Act
        instance.Animation.Update(0.6f);
        bool finishedEarly = state.IsFinished;
        instance.Animation.Update(0.6f);
        bool finishedNow = state.FinishedThisUpdate;
        instance.Animation.Update(0.6f);

        //Assert
        finishedEarly.Should().BeFalse();
        finishedNow.Should().BeTrue();
        state.FinishedThisUpdate.Should().BeFalse();
        state.IsFinished.Should().BeTrue();
        state.Time.Should().Be(1f);
        finished.Should().Be(1);
        ShouldBe(instance.LocalTransforms[Mover].Translation, new Vector3(0, 2, 0));
        ShouldBe(instance.LocalTransforms[Spinner].Scale, new Vector3(3));
    }

    [Fact]
    public void ping_pong_plays_forward_then_back()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationState state = instance.Animation.Play("lift", AnimationLoopMode.PingPong);

        //Act
        instance.Animation.Update(1.25f);
        float backwards = instance.LocalTransforms[Mover].Translation.Y;
        instance.Animation.Update(1f);

        //Assert
        backwards.Should().BeApproximately(1.5f, 1e-4f);
        state.Time.Should().BeApproximately(0.25f, 1e-5f);
        state.LoopCount.Should().Be(1);
        instance.LocalTransforms[Mover].Translation.Y.Should().BeApproximately(0.5f, 1e-4f);
    }

    [Fact]
    public void pause_speed_seek_and_normalized_time_control_the_playhead()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationPlayer player = instance.Animation;
        AnimationState state = player.Play("slide", AnimationLoopMode.Loop, 0.5f);
        player.Speed = 2f;

        //Act / Assert
        player.Update(0.5f);
        state.Time.Should().BeApproximately(0.5f, 1e-6f);
        state.Paused = true;
        player.Update(0.5f);
        state.Time.Should().BeApproximately(0.5f, 1e-6f);
        state.Paused = false;
        player.Paused = true;
        player.Update(0.5f);
        state.Time.Should().BeApproximately(0.5f, 1e-6f);
        player.Paused = false;
        state.NormalizedTime = 0.5f;
        player.Evaluate();
        state.Time.Should().Be(1f);
        ShouldBe(instance.LocalTransforms[Mover].Translation, new Vector3(2, 0, 0));
        state.Speed = -0.5f;
        player.Update(0.25f);
        state.Time.Should().BeApproximately(0.75f, 1e-6f);
        state.Time = 5f;
        state.Time.Should().BeApproximately(1f, 1e-6f, "seeking wraps a looping clip");
        state.LoopMode = AnimationLoopMode.Once;
        state.Time = 5f;
        state.Time.Should().Be(2f);
        state.IsFinished.Should().BeTrue();
    }

    [Fact]
    public void step_and_cubic_spline_clips_from_a_gltf_give_known_answers()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationPlayer player = instance.Animation;
        float X(AnimationState state, float time)
        {
            state.Time = time;
            player.Evaluate();
            return instance.LocalTransforms[Mover].Translation.X;
        }

        //Act
        AnimationState steps = player.Play("steps", AnimationLoopMode.Once);
        float[] stepped = { X(steps, 0.49f), X(steps, 0.5f), X(steps, 0.99f), X(steps, 1f) };
        AnimationState curve = player.Play("curve", AnimationLoopMode.Once);
        float[] curved = { X(curve, 0.5f), X(curve, 1f), X(curve, 1.5f), X(curve, 2f) };

        //Assert
        model.Animations[model.FindAnimation("steps")].Channels[0].Interpolation.Should().Be(AnimationInterpolation.Step);
        model.Animations[model.FindAnimation("curve")].Channels[0].Interpolation.Should().Be(AnimationInterpolation.CubicSpline);
        stepped.Should().Equal(0f, 1f, 1f, 2f);
        curved[0].Should().BeApproximately(0.125f, 1e-4f);
        curved[1].Should().BeApproximately(1f, 1e-4f);
        curved[2].Should().BeApproximately(3.375f, 1e-4f);
        curved[3].Should().BeApproximately(8f, 1e-4f);
    }

    [Fact]
    public void cross_fade_weights_ramp_linearly_and_blend_the_pose()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationPlayer player = instance.Animation;
        AnimationState slide = player.Play("slide");
        player.Update(1f);

        //Act
        AnimationState lift = player.CrossFade("lift", 1f, AnimationLoopMode.Once);
        float startWeight = lift.Weight;
        player.Update(0.5f);
        Vector3 halfway = instance.LocalTransforms[Mover].Translation;
        Vector3 halfwayScale = instance.LocalTransforms[Spinner].Scale;
        (float Slide, float Lift) weights = (slide.Weight, lift.Weight);
        player.Update(0.5f);

        //Assert - slide at 1.5 s is x = 3, lift at 0.5 s is y = 1: half of each; the spinner's scale (animated by lift
        // only) blends from its rest scale by lift's weight.
        startWeight.Should().Be(0f);
        weights.Slide.Should().BeApproximately(0.5f, 1e-5f);
        weights.Lift.Should().BeApproximately(0.5f, 1e-5f);
        ShouldBe(halfway, new Vector3(1.5f, 0.5f, 0));
        ShouldBe(halfwayScale, new Vector3(1.5f));
        player.StateCount.Should().Be(1);
        slide.IsActive.Should().BeFalse();
        lift.Weight.Should().Be(1f);
        lift.IsFading.Should().BeFalse();
        ShouldBe(instance.LocalTransforms[Mover].Translation, new Vector3(0, 2, 0));
        ShouldBe(instance.LocalTransforms[Spinner].Scale, new Vector3(3));
    }

    [Fact]
    public void higher_layer_overrides_only_its_properties_by_its_weight()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationPlayer player = instance.Animation;
        player.Play("slide");
        player.Update(1f);

        //Act
        AnimationState turn = player.Blend("turn", 0.5f, AnimationLoopMode.Once, 1f, 1f, 1);
        player.Evaluate();
        Quaternion half = instance.LocalTransforms[Spinner].Rotation;
        turn.Weight = 1f;
        player.Evaluate();

        //Assert
        ShouldBe(instance.LocalTransforms[Mover].Translation, new Vector3(2, 0, 0));
        ShouldBeRotation(half, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        ShouldBeRotation(instance.LocalTransforms[Spinner].Rotation, Turned);
        ShouldBe(instance.LocalTransforms[Spinner].Translation, new Vector3(0, 1, 0));
    }

    [Fact]
    public void weighted_rotation_blend_corrects_the_quaternion_hemisphere()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationPlayer player = instance.Animation;

        //Act - the second clip stores the 90 degree turn as -q; a naive weighted sum would give 135 degrees.
        player.Blend("still", 0.5f);
        player.Blend("turnNegated", 0.5f);
        player.Evaluate();
        Quaternion even = instance.LocalTransforms[Spinner].Rotation;
        player.GetState(0).Weight = 0.25f;
        player.GetState(1).Weight = 0.75f;
        player.Evaluate();

        //Assert
        ShouldBeRotation(even, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        even.Length().Should().BeApproximately(1f, 1e-5f);
        float angle = 2f * MathF.Acos(Math.Min(1f, Math.Abs(instance.LocalTransforms[Spinner].Rotation.W)));
        angle.Should().BeGreaterThan(MathF.PI / 4f);
        angle.Should().BeLessThan(MathF.PI / 2f);
    }

    [Fact]
    public void stopping_returns_the_animated_properties_to_the_rest_pose()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        instance.Animation.Play("lift");
        instance.Animation.Update(0.5f);
        Vector3 moved = instance.LocalTransforms[Mover].Translation;

        //Act
        instance.Animation.Stop();

        //Assert
        ShouldBe(moved, new Vector3(0, 1, 0));
        instance.Animation.IsPlaying.Should().BeFalse();
        ShouldBe(instance.LocalTransforms[Mover].Translation, Vector3.Zero);
        ShouldBe(instance.LocalTransforms[Spinner].Scale, Vector3.One);
        ShouldBe(instance.NodeMatrices[Spinner].Translation, new Vector3(0, 1, 0));
    }

    [Fact]
    public void morph_weight_channels_drive_the_instance_weights()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        int version = instance.PoseVersion;

        //Act
        instance.Animation.Play("blink");
        instance.Animation.Update(0.5f);

        //Assert
        instance.GetMorphWeights(Quad).ToArray().Should().Equal(0.5f, 0.25f);
        instance.PoseVersion.Should().BeGreaterThan(version);
    }

    [Fact]
    public void instances_share_the_clips_but_play_independently()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance[] instances = { model.CreateInstance(), model.CreateInstance(), model.CreateInstance() };
        instances[0].Animation.Play("slide");
        instances[1].Animation.Play("slide", AnimationLoopMode.Loop, 1f, 1f);

        //Act
        AnimationPlayer.UpdateAll(instances, 0.5f);

        //Assert
        ShouldBe(instances[0].LocalTransforms[Mover].Translation, new Vector3(1, 0, 0));
        ShouldBe(instances[1].LocalTransforms[Mover].Translation, new Vector3(3, 0, 0));
        ShouldBe(instances[2].LocalTransforms[Mover].Translation, Vector3.Zero);
        instances[2].HasAnimationPlayer.Should().BeFalse("UpdateAll skips instances that never animated");
    }

    [Fact]
    public void finished_callback_may_start_the_next_clip()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        AnimationPlayer player = instance.Animation;
        player.ClipFinished = state => state.Player.Play("slide");
        player.Play("lift", AnimationLoopMode.Once);

        //Act
        player.Update(1.5f);
        player.Update(0.5f);

        //Assert
        player.StateCount.Should().Be(1);
        player.GetState(0).Name.Should().Be("slide");
        ShouldBe(instance.LocalTransforms[Mover].Translation, new Vector3(1, 0, 0));
    }

    [Fact]
    public void archer_clip_poses_match_the_channel_samples()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        ModelInstance instance = archer.CreateInstance();
        ModelAnimation walk = archer.Animations[archer.FindAnimation("walk")];
        float[] value = new float[4];

        //Act
        instance.Animation.Play("walk");
        instance.Animation.Update(0.3f);

        //Assert
        walk.Channels.Count.Should().Be(7);
        foreach (ModelAnimationChannel channel in walk.Channels)
        {
            channel.Sample(0.3f, value);
            NodeTransform local = instance.LocalTransforms[channel.Node];
            if (channel.Path == AnimationPath.Rotation) ShouldBeRotation(local.Rotation, new Quaternion(value[0], value[1], value[2], value[3]), 1e-6f);
            else if (channel.Path == AnimationPath.Translation) ShouldBe(local.Translation, new Vector3(value[0], value[1], value[2]), 1e-6f);
        }
    }

    [Fact]
    public void updating_many_animated_instances_allocates_nothing_once_warm()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        string[] clips = { "walk", "idle", "sprint", "attack-melee-right", "emote-yes", "jump" };
        ModelInstance[] instances = new ModelInstance[50];
        Action<AnimationState> onFinished = state => state.Player.Play("idle");
        for (int index = 0; index < instances.Length; index++)
        {
            instances[index] = archer.CreateInstance();
            instances[index].Animation.ClipFinished = onFinished;
            instances[index].Animation.Play(clips[index % clips.Length], index % 4 == 0 ? AnimationLoopMode.Once : AnimationLoopMode.Loop, 1f, index * 0.07f);
        }

        void Frame(int round)
        {
            for (int index = 0; index < instances.Length; index += 5)
            {
                if (round % 7 == index % 7) instances[index].Animation.CrossFade(clips[(round + index) % clips.Length], 0.2f);
            }

            AnimationPlayer.UpdateAll(instances, 1f / 60f);
            for (int index = 0; index < instances.Length; index++) instances[index].GetJointPalette(0);
        }

        for (int round = 0; round < 240; round++) Frame(round);

        //Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 240; round < 360; round++) Frame(round);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        //Assert
        allocated.Should().Be(0);
    }

    [Fact]
    public void pose_edit_writes_are_seen_once_the_edit_ends()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        ModelInstance instance = model.CreateInstance();
        Matrix4x4 before = instance.NodeMatrices[Spinner];
        int version = instance.PoseVersion;

        //Act
        using (ModelPoseEdit pose = instance.BeginPoseEdit())
        {
            pose.LocalTransforms[Mover].Translation = new Vector3(5, 0, 0);
            pose.GetMorphWeights(Quad)[1] = 0.75f;
        }

        Matrix4x4 after = instance.NodeMatrices[Spinner];

        //Assert
        ShouldBe(before.Translation, new Vector3(0, 1, 0));
        ShouldBe(after.Translation, new Vector3(5, 1, 0));
        instance.GetMorphWeights(Quad).ToArray().Should().Equal(0f, 0.75f);
        instance.PoseVersion.Should().BeGreaterThan(version);
        instance.IsEditingPose.Should().BeFalse();
    }

    [Fact]
    public void reading_drawing_or_animating_during_a_pose_edit_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildModel();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(16, 16, CodeBrix.WebGpu.Native.TextureFormat.RGBA8UnormSrgb);
        ModelInstance instance = model.CreateInstance();
        instance.Animation.Play("slide");
        List<Type> thrown = new List<Type>();
        void Expect(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                thrown.Add(exception.GetType());
            }
        }

        //Act
        ModelPoseEdit pose = instance.BeginPoseEdit();
        pose.LocalTransforms[Mover].Translation = Vector3.One;
        Expect(() => _ = instance.NodeMatrices[0]);
        Expect(() => instance.GetNodeWorldMatrix(0));
        Expect(() => instance.Animation.Update(0.1f));
        Expect(() => instance.BeginPoseEdit());
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                renderer.Begin(pass, ModelPbrTests.Front(4, Vector3.Zero));
                Expect(() => renderer.Draw(instance));
                renderer.End();
            }

            frame.Submit();
        }

        ModelPoseEdit copy = pose;
        pose.Dispose();
        ModelPoseEdit second = instance.BeginPoseEdit();
        copy.Dispose();
        bool stillEditing = instance.IsEditingPose;
        second.Dispose();

        //Assert
        thrown.Should().Equal(typeof(InvalidOperationException), typeof(InvalidOperationException), typeof(InvalidOperationException), typeof(InvalidOperationException), typeof(InvalidOperationException));
        stillEditing.Should().BeTrue("disposing a stale copy does not end a newer edit");
        instance.IsEditingPose.Should().BeFalse();
        ShouldBe(instance.NodeMatrices[Mover].Translation, Vector3.One);
        gpu.Context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void setters_mark_the_pose_changed_so_palettes_follow()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        ModelInstance instance = archer.CreateInstance();
        int arm = archer.FindNode("arm-left");
        int joint = Array.IndexOf(archer.Skins[0].Joints.ToArray(), arm);
        Matrix4x4 rest = instance.GetJointPalette(0)[joint];
        NodeTransform local = instance.GetLocalTransform(arm);

        //Act
        local.Translation += new Vector3(0, 1, 0);
        instance.SetLocalTransform(arm, local);
        Matrix4x4 moved = instance.GetJointPalette(0)[joint];

        //Assert
        joint.Should().BeGreaterThanOrEqualTo(0);
        (moved.Translation - rest.Translation).Length().Should().BeGreaterThan(0.5f);
    }
}
