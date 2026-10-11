using System;
using System.Numerics;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Camera and orbit maths (no GPU).</summary>
public class CameraTests
{
    private static void ShouldBeNear(Vector3 actual, Vector3 expected) =>
        Vector3.Distance(actual, expected).Should().BeLessThan(1e-4f, "expected " + expected + " but was " + actual);

    [Fact]
    public void OrbitPosition_follows_yaw_and_pitch()
    {
        //Arrange
        Vector3 target = new Vector3(1, 2, 3);

        //Act
        Vector3 front = OrbitCamera.OrbitPosition(target, 10, 0, 0);
        Vector3 right = OrbitCamera.OrbitPosition(target, 10, MathF.PI / 2, 0);
        Vector3 above = OrbitCamera.OrbitPosition(target, 10, 0, MathF.PI / 2);

        //Assert
        ShouldBeNear(front, new Vector3(1, 2, 13));
        ShouldBeNear(right, new Vector3(11, 2, 3));
        ShouldBeNear(above, new Vector3(1, 12, 3));
    }

    [Fact]
    public void OrbitCamera_clamps_pitch_and_distance_and_applies_to_a_camera()
    {
        //Arrange
        OrbitCamera orbit = new OrbitCamera { Target = Vector3.Zero, Distance = 5 };
        Camera camera = new Camera();

        //Act
        orbit.Rotate(MathF.PI, 10f);
        orbit.Zoom(0.5f);
        orbit.Distance = -3;
        orbit.Distance = 2;
        orbit.Apply(camera);

        //Assert
        orbit.Pitch.Should().Be(OrbitCamera.MaxPitch);
        orbit.Yaw.Should().BeApproximately(MathF.PI, 1e-6f);
        camera.Target.Should().Be(Vector3.Zero);
        Vector3.Distance(camera.Position, Vector3.Zero).Should().BeApproximately(2f, 1e-4f);
        camera.Position.Y.Should().BeGreaterThan(1.99f);
    }

    [Fact]
    public void WorldToScreen_puts_the_target_in_the_middle_and_depth_in_0_to_1()
    {
        //Arrange
        Camera camera = new Camera { Position = new Vector3(0, 0, 5), Target = Vector3.Zero, NearPlane = 1, FarPlane = 100 };

        //Act
        Vector3 center = camera.WorldToScreen(Vector3.Zero, 200, 100);
        Vector3 near = camera.WorldToScreen(new Vector3(0, 0, 3.999f), 200, 100);
        Vector3 up = camera.WorldToScreen(new Vector3(0, 1, 0), 200, 100);

        //Assert
        center.X.Should().BeApproximately(100f, 1e-3f);
        center.Y.Should().BeApproximately(50f, 1e-3f);
        center.Z.Should().BeInRange(0f, 1f);
        near.Z.Should().BeLessThan(center.Z);
        near.Z.Should().BeGreaterThanOrEqualTo(-1e-4f);
        up.Y.Should().BeLessThan(50f);
    }

    [Fact]
    public void Orthographic_projection_keeps_sizes_regardless_of_distance()
    {
        //Arrange
        Camera camera = new Camera { Projection = ProjectionKind.Orthographic, OrthographicHeight = 4, Position = new Vector3(0, 0, 10) };

        //Act
        Vector3 near = camera.WorldToScreen(new Vector3(1, 1, 5), 100, 100);
        Vector3 far = camera.WorldToScreen(new Vector3(1, 1, -5), 100, 100);

        //Assert
        near.X.Should().BeApproximately(75f, 1e-3f);
        far.X.Should().BeApproximately(75f, 1e-3f);
        near.Y.Should().BeApproximately(25f, 1e-3f);
    }
}
