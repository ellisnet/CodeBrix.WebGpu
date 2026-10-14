using System;
using System.Numerics;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The 3D path rendered to a headless target: lighting, instancing, depth, textures, translucency.</summary>
public class MeshRendererTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 96;
    private static readonly Color4 Background = Color4.FromBytes(20, 30, 40);
    private readonly GraphicsGpuFixture gpu;

    public MeshRendererTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static Camera FrontOrthographic() => new Camera
    {
        Projection = ProjectionKind.Orthographic,
        OrthographicHeight = 10,
        Position = new Vector3(0, 0, 10),
        Target = Vector3.Zero,
        NearPlane = 0.1f,
        FarPlane = 100,
    };

    private byte[] Render(Camera camera, SceneLighting lighting, Action<MeshRenderer> draw, out MeshRenderer stats)
    {
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Size, Size);
        MeshRenderer renderer = new MeshRenderer(context);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Background))
            {
                renderer.Begin(pass, camera, lighting);
                draw(renderer);
                renderer.End();
            }

            frame.Submit();
        }

        context.TakeErrors().Should().BeEmpty();
        stats = renderer;
        return target.ReadRgba();
    }

    [Fact]
    public void Lit_cube_has_a_bright_lit_face_a_dim_shaded_face_and_the_clear_colour_around_it()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh cube = gpu.Context.CreateMesh(MeshShapes.Cube(2f), "cube");
        Camera camera = new Camera { Position = new Vector3(4, 3, 5), Target = Vector3.Zero, FieldOfView = MathF.PI / 3 };
        SceneLighting lighting = new SceneLighting { LightDirection = new Vector3(-1, 0, 0), AmbientColor = new Color4(0.15f, 0.15f, 0.15f) };
        Vector3 litFace = camera.WorldToScreen(new Vector3(1, 0, 0), Size, Size);
        Vector3 shadedFace = camera.WorldToScreen(new Vector3(0, 0, 1), Size, Size);

        //Act
        MeshRenderer renderer = null;
        byte[] rgba = Render(camera, lighting, r =>
        {
            renderer = r;
            LitMaterial material = r.CreateMaterial(Color4.White);
            material.SpecularStrength = 0;
            r.Draw(cube, material, Matrix4x4.Identity);
        }, out _);
        renderer.Dispose();
        double lit = TestPixels.Luma(rgba, Size, (int)litFace.X, (int)litFace.Y);
        double shaded = TestPixels.Luma(rgba, Size, (int)shadedFace.X, (int)shadedFace.Y);

        //Assert
        lit.Should().BeGreaterThan(200);
        shaded.Should().BeLessThan(60);
        shaded.Should().BeGreaterThan(20);
        TestPixels.At(rgba, Size, 0, 0).Should().Be(((byte)20, (byte)30, (byte)40, (byte)255));
        TestPixels.At(rgba, Size, Size - 1, Size - 1).Should().Be(((byte)20, (byte)30, (byte)40, (byte)255));
    }

    [Fact]
    public void DrawInstanced_draws_every_instance_in_one_call()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh cube = gpu.Context.CreateMesh(MeshShapes.Cube(1f));
        Matrix4x4[] worlds = { Matrix4x4.CreateTranslation(-3, 0, 0), Matrix4x4.CreateTranslation(0, 0, 0), Matrix4x4.CreateTranslation(3, 0, 0) };

        //Act
        MeshRenderer stats = null;
        byte[] rgba = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial material = r.CreateMaterial(Color4.Red);
            material.Unlit = true;
            r.DrawInstanced(cube, material, worlds);
        }, out stats);

        //Assert
        stats.LastDrawCallCount.Should().Be(1);
        stats.LastInstanceCount.Should().Be(3);
        TestPixels.At(rgba, Size, (int)(48 - (3 * 9.6)), 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Size, 48, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Size, (int)(48 + (3 * 9.6)), 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Size, 48 + 15, 48).Should().Be(((byte)20, (byte)30, (byte)40, (byte)255));
        stats.Dispose();
    }

    [Fact]
    public void Consecutive_draws_of_one_mesh_and_material_merge_into_one_call()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh cube = gpu.Context.CreateMesh(MeshShapes.Cube(1f));

        //Act
        MeshRenderer stats = null;
        Render(FrontOrthographic(), null, r =>
        {
            LitMaterial first = r.CreateMaterial(Color4.Red);
            LitMaterial second = r.CreateMaterial(Color4.Green);
            r.Draw(cube, first, Matrix4x4.CreateTranslation(-2, 0, 0));
            r.Draw(cube, first, Matrix4x4.CreateTranslation(0, 0, 0));
            r.Draw(cube, second, Matrix4x4.CreateTranslation(2, 0, 0));
        }, out stats);

        //Assert
        stats.LastDrawCallCount.Should().Be(2);
        stats.LastInstanceCount.Should().Be(3);
        stats.Dispose();
    }

    [Fact]
    public void Depth_test_keeps_the_nearer_surface_whatever_the_draw_order()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh slab = gpu.Context.CreateMesh(MeshShapes.Box(new Vector3(4, 4, 0.2f)));

        //Act
        byte[] nearFirst = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial near = r.CreateMaterial(Color4.Red);
            LitMaterial far = r.CreateMaterial(Color4.Green);
            near.Unlit = true;
            far.Unlit = true;
            r.Draw(slab, near, Matrix4x4.CreateTranslation(0, 0, 2));
            r.Draw(slab, far, Matrix4x4.CreateTranslation(0.5f, 0, -2));
        }, out MeshRenderer first);
        byte[] farFirst = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial near = r.CreateMaterial(Color4.Red);
            LitMaterial far = r.CreateMaterial(Color4.Green);
            near.Unlit = true;
            far.Unlit = true;
            r.Draw(slab, far, Matrix4x4.CreateTranslation(0.5f, 0, -2));
            r.Draw(slab, near, Matrix4x4.CreateTranslation(0, 0, 2));
        }, out MeshRenderer second);
        first.Dispose();
        second.Dispose();

        //Assert
        TestPixels.At(nearFirst, Size, 48, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(farFirst, Size, 48, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(farFirst, Size, 48 + 22, 48).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
    }

    [Fact]
    public void Base_colour_texture_and_translucency_are_applied()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh slab = gpu.Context.CreateMesh(MeshShapes.Box(new Vector3(4, 4, 0.2f)));
        using GpuTexture green = gpu.Context.CreateTextureFromRgba(1, 1, new byte[] { 0, 255, 0, 255 });

        //Act
        byte[] rgba = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial textured = r.CreateMaterial(Color4.White, green);
            LitMaterial glass = r.CreateMaterial(new Color4(1, 0, 0, 0.5f));
            textured.Unlit = true;
            glass.Unlit = true;
            r.Draw(slab, glass, Matrix4x4.CreateTranslation(-1, 0, 2));
            r.Draw(slab, textured, Matrix4x4.CreateTranslation(1, 0, 0));
        }, out MeshRenderer stats);
        stats.Dispose();

        //Assert
        TestPixels.At(rgba, Size, 48 + 25, 48).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        TestPixels.Near(TestPixels.At(rgba, Size, 48, 48), 128, 128, 0, 2).Should().BeTrue();
        TestPixels.Near(TestPixels.At(rgba, Size, 48 - 25, 48), 138, 15, 20, 2).Should().BeTrue();
    }

    [Fact]
    public void Back_faces_are_culled_unless_the_material_is_double_sided()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh plane = gpu.Context.CreateMesh(MeshShapes.Plane(4, 4));
        Matrix4x4 facingAway = Matrix4x4.CreateRotationX(-MathF.PI / 2);

        //Act
        byte[] single = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial material = r.CreateMaterial(Color4.Red);
            material.Unlit = true;
            r.Draw(plane, material, facingAway);
        }, out MeshRenderer first);
        byte[] both = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial material = r.CreateMaterial(Color4.Red, null, doubleSided: true);
            material.Unlit = true;
            r.Draw(plane, material, facingAway);
        }, out MeshRenderer second);
        first.Dispose();
        second.Dispose();

        //Assert
        TestPixels.At(single, Size, 48, 48).Should().Be(((byte)20, (byte)30, (byte)40, (byte)255));
        TestPixels.At(both, Size, 48, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
    }

    [Fact]
    public void mirroring_world_matrices_keep_single_sided_faces_visible()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh plane = gpu.Context.CreateMesh(MeshShapes.Plane(2, 2));
        Matrix4x4 facing = Matrix4x4.CreateRotationX(MathF.PI / 2);
        Matrix4x4[] worlds =
        {
            facing * Matrix4x4.CreateTranslation(-3, 0, 0),
            facing * Matrix4x4.CreateScale(-1, 1, 1),
            facing * Matrix4x4.CreateTranslation(3, 0, 0),
        };

        //Act
        byte[] pixels = Render(FrontOrthographic(), null, r =>
        {
            LitMaterial material = r.CreateMaterial(Color4.Red);
            material.Unlit = true;
            r.DrawInstanced(plane, material, worlds);
        }, out MeshRenderer stats);
        int drawCalls = stats.LastDrawCallCount;
        stats.Dispose();

        //Assert - one draw per run of matrices with the same winding; every quad is drawn.
        drawCalls.Should().Be(3);
        TestPixels.At(pixels, Size, 48 - 29, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(pixels, Size, 48, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(pixels, Size, 48 + 29, 48).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        MeshRenderer.IsMirroring(worlds[1]).Should().BeTrue();
        MeshRenderer.IsMirroring(worlds[0]).Should().BeFalse();
    }

    [Fact]
    public void Material_from_another_renderer_is_refused()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh cube = gpu.Context.CreateMesh(MeshShapes.Cube());
        using MeshRenderer other = new MeshRenderer(gpu.Context);
        using LitMaterial foreign = other.CreateMaterial(Color4.Red);
        Exception caught = null;

        //Act
        Render(FrontOrthographic(), null, r => caught = Record.Exception(() => r.Draw(cube, foreign, Matrix4x4.Identity)), out MeshRenderer stats);
        stats.Dispose();

        //Assert
        caught.Should().BeOfType<ArgumentException>();
    }

    [Fact]
    public void CreateMesh_validates_indices()
    {
        //Arrange
        gpu.RequireDevice();
        MeshVertex[] vertices = { new MeshVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero) };

        //Act
        Action outOfRange = () => gpu.Context.CreateMesh(vertices, new uint[] { 0, 0, 1 });
        Action notTriangles = () => gpu.Context.CreateMesh(vertices, new uint[] { 0, 0 });

        //Assert
        outOfRange.Should().Throw<ArgumentException>();
        notTriangles.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void alpha_modes_and_texture_transform_shape_the_lit_material()
    {
        //Arrange
        gpu.RequireDevice();
        using Mesh quad = gpu.Context.CreateMesh(MeshShapes.Box(new Vector3(4, 4, 0.01f)), "quad");
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(2, 1, new byte[] { 255, 0, 0, 255, 0, 255, 0, 0 });
        using GpuSampler nearest = gpu.Context.CreateSampler(SamplerDescription.NearestRepeat);
        LitMaterial masked = null;
        LitMaterial opaque = null;

        //Act
        byte[] maskedPixels = Render(FrontOrthographic(), new SceneLighting { AmbientColor = Color4.White }, r =>
        {
            masked = r.CreateMaterial(Color4.White, texture, false, "masked", nearest);
            masked.Unlit = true;
            masked.AlphaMode = MaterialAlphaMode.Mask;
            masked.TextureTransform = Matrix3x2.CreateTranslation(0.5f, 0);
            r.Draw(quad, masked, Matrix4x4.Identity);
        }, out _);
        byte[] opaquePixels = Render(FrontOrthographic(), null, r =>
        {
            opaque = r.CreateMaterial(Color4.White.WithAlpha(0.25f));
            opaque.Unlit = true;
            opaque.AlphaMode = MaterialAlphaMode.Opaque;
            r.Draw(quad, opaque, Matrix4x4.Identity);
        }, out _);

        //Assert
        masked.IsTranslucent.Should().BeFalse();
        opaque.IsTranslucent.Should().BeFalse();
        // u + 0.5 (repeating): the left half samples the transparent texel (discarded), the right half wraps to red.
        TestPixels.Near(TestPixels.At(maskedPixels, Size, (Size / 2) - 4, Size / 2), 20, 30, 40).Should().BeTrue();
        TestPixels.At(maskedPixels, Size, (Size / 2) + 4, Size / 2).R.Should().BeGreaterThan(200);
        TestPixels.At(maskedPixels, Size, (Size / 2) + 4, Size / 2).G.Should().BeLessThan(10);
        TestPixels.At(opaquePixels, Size, Size / 2, Size / 2).A.Should().Be(255);
        TestPixels.At(opaquePixels, Size, Size / 2, Size / 2).R.Should().BeGreaterThan(250);
    }
}
