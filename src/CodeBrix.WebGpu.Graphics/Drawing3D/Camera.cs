using System;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How a <see cref="Camera" /> projects.</summary>
public enum ProjectionKind
{
    /// <summary>Perspective: far things look smaller.</summary>
    Perspective,

    /// <summary>Orthographic: no foreshortening.</summary>
    Orthographic,
}

/// <summary>
/// A right-handed camera (y up, looking down -z in view space) with a perspective or orthographic projection whose
/// depth range is WebGPU's 0..1. Matrices follow System.Numerics' row-vector convention
/// (<c>world * View * Projection</c>).
/// </summary>
public sealed class Camera
{
    /// <summary>Where the camera is. Default (0, 0, 5).</summary>
    public Vector3 Position { get; set; } = new Vector3(0, 0, 5);

    /// <summary>The point it looks at. Default the origin.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>The up direction. Default +Y.</summary>
    public Vector3 Up { get; set; } = Vector3.UnitY;

    /// <summary>The projection. Default perspective.</summary>
    public ProjectionKind Projection { get; set; } = ProjectionKind.Perspective;

    /// <summary>The vertical field of view in radians (perspective). Default 60 degrees.</summary>
    public float FieldOfView { get; set; } = MathF.PI / 3f;

    /// <summary>The visible height in world units (orthographic). Default 10.</summary>
    public float OrthographicHeight { get; set; } = 10f;

    /// <summary>The near plane distance. Default 0.1.</summary>
    public float NearPlane { get; set; } = 0.1f;

    /// <summary>The far plane distance. Default 1000.</summary>
    public float FarPlane { get; set; } = 1000f;

    /// <summary>The view matrix.</summary>
    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Target, Up);

    /// <summary>The unit vector from <see cref="Position" /> towards <see cref="Target" />.</summary>
    public Vector3 Forward => Vector3.Normalize(Target - Position);

    /// <summary>The projection matrix for an aspect ratio (width / height).</summary>
    /// <param name="aspectRatio">The aspect ratio.</param>
    /// <returns>The matrix.</returns>
    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        float aspect = aspectRatio > 0 ? aspectRatio : 1f;
        return Projection == ProjectionKind.Perspective
            ? Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, NearPlane, FarPlane)
            : Matrix4x4.CreateOrthographic(OrthographicHeight * aspect, OrthographicHeight, NearPlane, FarPlane);
    }

    /// <summary>View times projection for an aspect ratio.</summary>
    /// <param name="aspectRatio">The aspect ratio.</param>
    /// <returns>The matrix.</returns>
    public Matrix4x4 GetViewProjectionMatrix(float aspectRatio) => ViewMatrix * GetProjectionMatrix(aspectRatio);

    /// <summary>Projects a world position to pixel coordinates (top-left origin, y down) and depth (0..1).</summary>
    /// <param name="world">The world position.</param>
    /// <param name="width">The target width in pixels.</param>
    /// <param name="height">The target height in pixels.</param>
    /// <returns>x, y in pixels and z the depth; z outside 0..1 means clipped.</returns>
    public Vector3 WorldToScreen(Vector3 world, int width, int height)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1), GetViewProjectionMatrix((float)width / Math.Max(1, height)));
        Vector3 ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        return new Vector3((ndc.X + 1) * 0.5f * width, (1 - ndc.Y) * 0.5f * height, ndc.Z);
    }
}

/// <summary>
/// An orbit (turntable) controller: a target point, a distance and two angles. Apply it to a <see cref="Camera" />
/// each frame; drive it from mouse drags, a timer or keys.
/// </summary>
public sealed class OrbitCamera
{
    private float pitch = 0.4f;
    private float distance = 10f;

    /// <summary>The point orbited. Default the origin.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>The angle around the vertical axis, in radians; 0 puts the camera on +Z looking towards -Z.</summary>
    public float Yaw { get; set; }

    /// <summary>The elevation angle in radians, clamped to just short of straight up or down. Default 0.4.</summary>
    public float Pitch
    {
        get => pitch;
        set => pitch = Math.Clamp(value, -MaxPitch, MaxPitch);
    }

    /// <summary>The distance from the target, at least <see cref="MinDistance" />. Default 10.</summary>
    public float Distance
    {
        get => distance;
        set => distance = Math.Max(MinDistance, value);
    }

    /// <summary>The smallest distance allowed. Default 0.01.</summary>
    public float MinDistance { get; set; } = 0.01f;

    /// <summary>The largest pitch magnitude: just under 90 degrees.</summary>
    public static float MaxPitch => (MathF.PI / 2f) - 0.001f;

    /// <summary>The camera position the controller describes.</summary>
    public Vector3 Position => OrbitPosition(Target, Distance, Yaw, Pitch);

    /// <summary>Turns the camera around the target.</summary>
    /// <param name="deltaYaw">Added to <see cref="Yaw" />.</param>
    /// <param name="deltaPitch">Added to <see cref="Pitch" />.</param>
    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch += deltaPitch;
    }

    /// <summary>Multiplies the distance (below 1 zooms in).</summary>
    /// <param name="factor">The factor.</param>
    public void Zoom(float factor) => Distance *= factor;

    /// <summary>Points a camera at the target from the orbit position (up = +Y).</summary>
    /// <param name="camera">The camera.</param>
    public void Apply(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        camera.Target = Target;
        camera.Position = Position;
        camera.Up = Vector3.UnitY;
    }

    /// <summary>The position on a sphere around a target: <c>target + distance * (cos p sin y, sin p, cos p cos y)</c>.</summary>
    /// <param name="target">The centre.</param>
    /// <param name="distance">The radius.</param>
    /// <param name="yaw">The angle around +Y in radians (0 = on +Z).</param>
    /// <param name="pitch">The elevation in radians.</param>
    /// <returns>The position.</returns>
    public static Vector3 OrbitPosition(Vector3 target, float distance, float yaw, float pitch) =>
        target + (distance * new Vector3(MathF.Cos(pitch) * MathF.Sin(yaw), MathF.Sin(pitch), MathF.Cos(pitch) * MathF.Cos(yaw)));
}
