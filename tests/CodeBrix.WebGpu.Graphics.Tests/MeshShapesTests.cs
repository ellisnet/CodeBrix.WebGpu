using System.Numerics;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Generated shapes (no GPU): sizes, normals and counter-clockwise outward winding.</summary>
public class MeshShapesTests
{
    private static void AllTrianglesFaceOutward(MeshData data)
    {
        for (int index = 0; index < data.Indices.Length; index += 3)
        {
            MeshVertex a = data.Vertices[data.Indices[index]];
            MeshVertex b = data.Vertices[data.Indices[index + 1]];
            MeshVertex c = data.Vertices[data.Indices[index + 2]];
            Vector3 faceNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Vector3.Dot(faceNormal, a.Normal + b.Normal + c.Normal).Should().BeGreaterThan(0f);
        }
    }

    [Fact]
    public void Cube_has_24_vertices_36_indices_and_outward_faces()
    {
        //Arrange
        //Act
        MeshData cube = MeshShapes.Cube(2f, Color4.Red);
        cube.GetBounds(out Vector3 min, out Vector3 max);

        //Assert
        cube.Vertices.Should().HaveCount(24);
        cube.Indices.Should().HaveCount(36);
        min.Should().Be(new Vector3(-1));
        max.Should().Be(new Vector3(1));
        cube.Vertices[0].Color.Should().Be(new Vector4(1, 0, 0, 1));
        AllTrianglesFaceOutward(cube);
    }

    [Fact]
    public void Sphere_and_plane_face_outward()
    {
        //Arrange
        //Act
        MeshData sphere = MeshShapes.Sphere(1f, 12, 6);
        MeshData plane = MeshShapes.Plane(2, 3);

        //Assert
        AllTrianglesFaceOutward(sphere);
        AllTrianglesFaceOutward(plane);
        plane.Vertices.Should().OnlyContain(vertex => vertex.Normal == Vector3.UnitY);
    }

    [Fact]
    public void Transformed_and_Combine_move_positions_and_rebase_indices()
    {
        //Arrange
        MeshData plane = MeshShapes.Plane(2, 2);

        //Act
        MeshData moved = plane.Transformed(Matrix4x4.CreateTranslation(0, 5, 0));
        MeshData both = MeshData.Combine(new[] { plane, moved });

        //Assert
        moved.Vertices[0].Position.Y.Should().Be(5f);
        both.Vertices.Should().HaveCount(8);
        both.Indices.Should().HaveCount(12);
        both.Indices[6].Should().Be(4u);
    }
}
