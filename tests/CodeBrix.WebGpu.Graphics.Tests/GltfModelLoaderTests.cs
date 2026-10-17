using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SilverAssertions;
using Xunit;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// The glTF loader on documents built in-test for one feature each: a rotation (quaternion) animation channel whose
/// output accessor is SPARSE (dense identity keys, two keys overridden by the sparse substitution).
/// </summary>
public class GltfModelLoaderTests : IClassFixture<GraphicsGpuFixture>
{
    private static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
    private static readonly Quaternion Half = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

    private readonly GraphicsGpuFixture gpu;

    public GltfModelLoaderTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    /// <summary>
    /// A quad (node 0) and a "spinner" node (node 1) with one clip "spin": keys at 0, 1 and 2 s whose output accessor
    /// holds identity three times densely and replaces keys 1 and 2 sparsely with 90 and 180 degrees about Y.
    /// </summary>
    /// <param name="sparse">True when the output accessor came out sparse in the document (checked by the test).</param>
    private Model BuildSparseRotationModel(out bool sparse)
    {
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        TestGltf.AddMesh(root, "quad", TestGltf.Material(root, "white", Vector4.One, 0f, 1f), TestGltf.Quad());
        Gltf.Node spinner = root.UseScene(0).CreateNode("spinner");
        int before = root.LogicalAccessors.Count;
        root.CreateAnimation("spin").CreateRotationChannel(spinner, new Dictionary<float, Quaternion>
        {
            [0f] = Quaternion.Identity,
            [1f] = Quaternion.Identity,
            [2f] = Quaternion.Identity,
        });
        Gltf.Accessor output = root.LogicalAccessors.Skip(before).Single(accessor => accessor.Dimensions == Gltf.DimensionType.VEC4);
        output.CreateSparseData(new Dictionary<int, Quaternion> { [1] = Quarter, [2] = Half });
        Model model = TestGltf.Load(gpu.Context, root, "sparse-rotation");
        sparse = output.IsSparse;
        return model;
    }

    private static void ShouldBeRotation(Quaternion actual, Quaternion expected, float tolerance = 1e-4f) =>
        Math.Abs(Quaternion.Dot(actual, expected)).Should().BeApproximately(1f, tolerance, "expected " + expected + " but found " + actual);

    [Fact]
    public void sparse_rotation_channel_loads_with_the_substituted_keys()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using Model model = BuildSparseRotationModel(out bool sparse);

        //Assert
        sparse.Should().BeTrue();
        ModelAnimation spin = model.Animations[model.FindAnimation("spin")];
        spin.Channels.Count.Should().Be(1);
        ModelAnimationChannel channel = spin.Channels[0];
        channel.Path.Should().Be(AnimationPath.Rotation);
        channel.Interpolation.Should().Be(AnimationInterpolation.Linear);
        channel.ComponentCount.Should().Be(4);
        channel.KeyCount.Should().Be(3);
        spin.Duration.Should().Be(2f);
        float[] values = channel.Values.ToArray();
        ShouldBeRotation(new Quaternion(values[0], values[1], values[2], values[3]), Quaternion.Identity);
        ShouldBeRotation(new Quaternion(values[4], values[5], values[6], values[7]), Quarter);
        ShouldBeRotation(new Quaternion(values[8], values[9], values[10], values[11]), Half);
    }

    [Fact]
    public void sparse_rotation_channel_samples_between_the_substituted_keys()
    {
        //Arrange
        gpu.RequireDevice();
        using Model model = BuildSparseRotationModel(out _);
        ModelAnimationChannel channel = model.Animations[model.FindAnimation("spin")].Channels[0];
        ModelInstance instance = model.CreateInstance();
        int spinner = channel.Node;
        Span<float> sampled = stackalloc float[4];

        //Act
        channel.Sample(1.5f, sampled);
        instance.Animation.Play("spin");
        instance.Animation.Update(0.5f);
        Quaternion atHalfSecond = instance.LocalTransforms[spinner].Rotation;
        instance.Animation.Update(1f);
        Quaternion atOneAndAHalf = instance.LocalTransforms[spinner].Rotation;

        //Assert
        ShouldBeRotation(new Quaternion(sampled[0], sampled[1], sampled[2], sampled[3]), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.75f));
        ShouldBeRotation(atHalfSecond, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        ShouldBeRotation(atOneAndAHalf, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.75f));
    }
}
