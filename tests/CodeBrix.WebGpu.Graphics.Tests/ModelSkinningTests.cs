using System;
using System.IO;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// GPU skinning of the committed Kenney archer: the bind pose matches the unskinned draw, a pose moves exactly the
/// vertices a CPU skinning of the same palette moves (per instance, in one batched draw), and a joint rotation turns
/// the normals with the geometry.
/// </summary>
public class ModelSkinningTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Size = 128;
    private readonly GraphicsGpuFixture gpu;

    public ModelSkinningTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private Model LoadArcher() => gpu.Context.LoadModel(Path.Combine(AppContext.BaseDirectory, "Assets", "kenney-mini-forest", "character-archer.glb"));

    private byte[] Render(ModelRenderer renderer, Camera camera, params ModelInstance[] instances) =>
        ModelPbrTests.Render(gpu.Context, renderer, camera, new SceneLighting { LightDirection = new Vector3(-0.3f, -0.5f, -1f) }, Size, TextureFormat.RGBA8UnormSrgb, Color4.Black, instances);

    private static int LargestDifference(byte[] first, byte[] second)
    {
        int largest = 0;
        for (int index = 0; index < first.Length; index++) largest = Math.Max(largest, Math.Abs(first[index] - second[index]));
        return largest;
    }

    private static bool Covered(byte[] pixels, Vector3 screen) =>
        TestPixels.At(pixels, Size, (int)screen.X, (int)screen.Y) != ((byte)0, (byte)0, (byte)0, (byte)255);

    /// <summary>The model-space centroid of the body vertices bound (weight > 0.99) to one joint node, skinned on the CPU with an instance's palette.</summary>
    internal static Vector3 SkinnedCentroid(Model model, ModelInstance instance, int jointNode)
    {
        ModelVertex[] vertices = model.VertexBuffer.Read<ModelVertex>(model.VertexCount);
        ModelSkinVertex[] skin = model.SkinBuffer.Read<ModelSkinVertex>();
        Vector3 sum = Vector3.Zero;
        int count = 0;
        for (int node = 0; node < model.Nodes.Count; node++)
        {
            ModelNode data = model.Nodes[node];
            if (data.MeshIndex < 0 || data.SkinIndex < 0) continue;
            ReadOnlySpan<int> joints = model.Skins[data.SkinIndex].Joints.Span;
            ReadOnlySpan<Matrix4x4> palette = instance.GetJointPalette(data.SkinIndex);
            foreach (ModelPrimitive primitive in model.Meshes[data.MeshIndex].Primitives)
            {
                for (int vertex = 0; vertex < primitive.VertexCount; vertex++)
                {
                    ModelSkinVertex weights = skin[primitive.SkinBaseVertex + vertex];
                    if (joints[weights.Joint0] != jointNode || weights.Weights.X < 0.99f) continue;
                    Vector3 position = vertices[primitive.BaseVertex + vertex].Position;
                    Vector3 skinned = (Vector3.Transform(position, palette[weights.Joint0]) * weights.Weights.X)
                        + (Vector3.Transform(position, palette[weights.Joint1]) * weights.Weights.Y)
                        + (Vector3.Transform(position, palette[weights.Joint2]) * weights.Weights.Z)
                        + (Vector3.Transform(position, palette[weights.Joint3]) * weights.Weights.W);
                    sum += skinned;
                    count++;
                }
            }
        }

        count.Should().BeGreaterThan(0);
        return sum / count;
    }

    private static Camera FrontOf(Model model, float widthFactor) => new Camera
    {
        Projection = ProjectionKind.Orthographic,
        OrthographicHeight = (model.BoundsMax - model.BoundsMin).Length() * widthFactor,
        Position = model.BoundsCenter + new Vector3(0, 0, 20),
        Target = model.BoundsCenter,
        NearPlane = 0.1f,
        FarPlane = 100,
    };

    [Fact]
    public void bind_pose_matches_the_unskinned_draw()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        Camera camera = FrontOf(archer, 1f);
        ModelInstance instance = archer.CreateInstance();

        //Act
        byte[] skinned = Render(renderer, camera, instance);
        renderer.Path.SkinningDisabled = true;
        byte[] unskinned = Render(renderer, camera, instance);

        //Assert
        instance.GetJointPalette(0).Length.Should().Be(archer.Skins[0].JointCount);
        TestPixels.Luma(skinned, Size, Size / 2, Size / 2).Should().BeGreaterThan(10);
        LargestDifference(skinned, unskinned).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void a_posed_joint_moves_its_vertices_where_cpu_skinning_puts_them_per_instance()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        int arm = archer.FindNode("arm-left");
        Camera camera = FrontOf(archer, 2.2f);
        Matrix4x4 left = Matrix4x4.CreateTranslation(-archer.BoundsCenter.X - 0.6f * (archer.BoundsMax.X - archer.BoundsMin.X), 0, 0);
        Matrix4x4 right = Matrix4x4.CreateTranslation(-archer.BoundsCenter.X + 0.6f * (archer.BoundsMax.X - archer.BoundsMin.X), 0, 0);
        ModelInstance posed = archer.CreateInstance(left);
        ModelInstance rest = archer.CreateInstance(right);
        Vector3 restCentroid = SkinnedCentroid(archer, rest, arm);
        NodeTransform original = posed.GetLocalTransform(arm);
        float outward = MathF.Sign(restCentroid.X - archer.BoundsCenter.X) * 0.3f;
        posed.SetLocalTransform(arm, new NodeTransform(original.Translation + new Vector3(outward, 0, 0), Quaternion.Concatenate(original.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), original.Scale));
        Vector3 posedCentroid = SkinnedCentroid(archer, posed, arm);
        ModelInstance unposedAtLeft = archer.CreateInstance(left);

        //Act
        byte[] both = Render(renderer, camera, posed, rest);
        byte[] reference = Render(renderer, camera, unposedAtLeft, rest);

        //Assert
        Vector3 posedOnScreen = camera.WorldToScreen(Vector3.Transform(posedCentroid, left), Size, Size);
        Vector3 restOnScreen = camera.WorldToScreen(Vector3.Transform(restCentroid, right), Size, Size);
        renderer.LastDrawCallCount.Should().Be(archer.DrawItems.Length);
        Covered(both, posedOnScreen).Should().BeTrue("the posed arm is drawn where CPU skinning puts it");
        Covered(reference, posedOnScreen).Should().BeFalse("nothing is there without the pose");
        Covered(both, restOnScreen).Should().BeTrue("the other instance keeps its rest pose in the same draw");
        (posedCentroid - restCentroid + (right.Translation - left.Translation)).Length().Should().BeGreaterThan(0.1f);
    }

    [Fact]
    public void rotating_the_skeleton_root_turns_geometry_and_normals_like_the_same_world_rotation()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        int root = archer.FindNode("root");
        Camera camera = FrontOf(archer, 1.2f);
        ModelInstance posed = archer.CreateInstance();
        Matrix4x4 before = posed.NodeMatrices[root];
        NodeTransform local = posed.GetLocalTransform(root);
        posed.SetLocalTransform(root, new NodeTransform(local.Translation, Quaternion.Concatenate(local.Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.5f)), local.Scale));
        Matrix4x4.Invert(before, out Matrix4x4 inverse);
        Matrix4x4 equivalent = inverse * posed.NodeMatrices[root];
        ModelInstance turned = archer.CreateInstance(equivalent);

        //Act
        byte[] byJoint = Render(renderer, camera, posed);
        byte[] byWorld = Render(renderer, camera, turned);
        byte[] rest = Render(renderer, camera, archer.CreateInstance());

        //Assert
        LargestDifference(byJoint, byWorld).Should().BeLessThanOrEqualTo(2);
        LargestDifference(byJoint, rest).Should().BeGreaterThan(40);
    }

    [Fact]
    public void posed_skinned_and_sorted_blended_drawing_allocates_nothing_once_warm()
    {
        //Arrange
        gpu.RequireDevice();
        using Model archer = LoadArcher();
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.Material glass = TestGltf.Material(root, "glass", new Vector4(0.5f, 0.7f, 1f, 0.4f), 0f, 0.2f);
        glass.Alpha = Gltf.AlphaMode.BLEND;
        TestGltf.AddMesh(root, "pane", glass, TestGltf.Quad(0.4f));
        using Model pane = TestGltf.Load(gpu.Context, root, "pane");
        using ModelRenderer renderer = gpu.Context.CreateModelRenderer();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(Size, Size, TextureFormat.RGBA8UnormSrgb);
        ModelInstance[] instances = new ModelInstance[8];
        for (int index = 0; index < instances.Length; index++) instances[index] = (index % 2 == 0 ? archer : pane).CreateInstance(Matrix4x4.CreateTranslation(index * 0.3f, 0, index * -0.2f));
        int arm = archer.FindNode("arm-left");
        Camera camera = FrontOf(archer, 3f);
        SceneLighting lighting = new SceneLighting();
        long allocated = -1;

        //Act
        for (int round = 0; round < 4; round++)
        {
            using GraphicsFrame frame = gpu.Context.BeginFrame();
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                renderer.Begin(pass, camera, lighting);
                for (int index = 0; index < instances.Length; index++)
                {
                    if (index % 2 == 0)
                    {
                        NodeTransform local = instances[index].GetLocalTransform(arm);
                        local.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, round + index);
                        instances[index].SetLocalTransform(arm, local);
                    }

                    renderer.Draw(instances[index]);
                }

                renderer.End();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            frame.Submit();
        }

        //Assert
        allocated.Should().Be(0);
        renderer.LastDrawCallCount.Should().Be(archer.DrawItems.Length + 4);
        gpu.Context.TakeErrors().Should().BeEmpty();
    }
}
