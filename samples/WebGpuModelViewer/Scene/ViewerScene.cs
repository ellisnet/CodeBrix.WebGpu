using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeBrix.Graphics3D.Gltf2.Runtime;
using CodeBrix.Graphics3D.Gltf2.Transforms;
using CodeBrix.WebGpu.Graphics;
using Mesh = CodeBrix.WebGpu.Graphics.Mesh;

namespace WebGpuModelViewer.Scene;

/// <summary>
/// The little scene: a grass plane with trees, rocks, barrels and a camp fire; Cube Pets animals walking a ring and
/// idling, eating and dancing around a chest that opens and closes; the Avian flying circles overhead. Each actor is
/// one animated glTF instance placed by a world matrix.
/// </summary>
internal sealed class ViewerScene
{
    private static readonly string[] Animals = { "animal-cat", "animal-dog", "animal-fox", "animal-pig", "animal-cow", "animal-panda", "animal-penguin", "animal-bunny" };
    private static readonly string[] StationaryTracks = { "idle", "dance", "eat", "dance" };

    private readonly List<Actor> actors = new List<Actor>();
    private readonly Dictionary<string, GltfModel> models = new Dictionary<string, GltfModel>(StringComparer.Ordinal);
    private readonly Mesh ground;
    private readonly LitMaterial groundMaterial;

    /// <summary>Loads every model and lays out the scene.</summary>
    /// <param name="context">The graphics context.</param>
    /// <param name="renderer">The mesh renderer.</param>
    /// <param name="assets">The Assets folder.</param>
    public ViewerScene(GraphicsContext context, MeshRenderer renderer, string assets)
    {
        Cache = new AssetCache();
        ground = context.CreateMesh(MeshShapes.Plane(48, 48), "ground");
        groundMaterial = renderer.CreateMaterial(Color4.FromBytes(110, 168, 84), label: "ground");
        groundMaterial.SpecularStrength = 0;

        GltfModel Load(string pack, string name)
        {
            if (models.TryGetValue(name, out GltfModel loaded)) return loaded;
            GltfModel model = GltfModel.Load(Path.Combine(assets, pack, name + ".glb"), context, renderer, Cache);
            models[name] = model;
            return model;
        }

        // The chest in the middle, opening and closing.
        Add(Load("survival-kit", "chest"), "open-close", 0, _ => Matrix4x4.CreateScale(3.2f) * Matrix4x4.CreateRotationY(0.6f));

        // Four animals walk a ring; four more stand closer in and idle, dance or eat, facing the chest.
        for (int index = 0; index < Animals.Length; index++)
        {
            GltfModel animal = Load("cube-pets", Animals[index]);
            float baseAngle = index * MathF.Tau / Animals.Length;
            if (index % 2 == 0)
            {
                Add(animal, "walk", index * 0.13f, time =>
                {
                    float angle = baseAngle + (time * 0.28f);
                    Vector3 position = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * 5.4f;
                    return Matrix4x4.CreateScale(0.55f) * Matrix4x4.CreateRotationY(angle + (MathF.PI / 2)) * Matrix4x4.CreateTranslation(position);
                });
            }
            else
            {
                string track = StationaryTracks[(index / 2) % StationaryTracks.Length];
                Vector3 position = new Vector3(MathF.Sin(baseAngle), 0, MathF.Cos(baseAngle)) * 2.9f;
                Matrix4x4 placement = Matrix4x4.CreateScale(0.55f) * Matrix4x4.CreateRotationY(baseAngle + MathF.PI) * Matrix4x4.CreateTranslation(position);
                Add(animal, track, index * 0.21f, _ => placement);
            }
        }

        // The Avian circles overhead, the other way round.
        Add(Load("crystals-and-avian", "avian"), "fly", 0, time =>
        {
            float angle = -time * 0.45f;
            Vector3 position = new Vector3(MathF.Sin(angle) * 4.2f, 3.0f + (MathF.Sin(time * 1.7f) * 0.25f), MathF.Cos(angle) * 4.2f);
            return Matrix4x4.CreateScale(0.36f) * Matrix4x4.CreateRotationY(angle - (MathF.PI / 2)) * Matrix4x4.CreateTranslation(position);
        });

        // Static props around the edge.
        (string Name, float Angle, float Radius, float Scale)[] props =
        {
            ("tree", 0.3f, 10.5f, 2.6f), ("tree-tall", 0.9f, 11.5f, 2.6f), ("tree-autumn", 1.6f, 10f, 2.8f), ("tree", 2.3f, 11f, 2.4f),
            ("tree-autumn", 3.1f, 10.8f, 2.6f), ("tree-tall", 3.9f, 10.2f, 2.5f), ("tree", 4.6f, 11.4f, 2.7f), ("tree-autumn", 5.4f, 10.4f, 2.5f),
            ("rock-a", 0.6f, 7.8f, 2.2f), ("rock-b", 2.0f, 8.2f, 2.0f), ("rock-c", 3.5f, 7.6f, 2.4f), ("rock-a", 5.0f, 8.0f, 1.8f),
            ("barrel", 1.2f, 7.4f, 2.2f), ("barrel", 1.32f, 7.9f, 2.2f), ("campfire-pit", 4.25f, 7.6f, 2.4f),
            ("patch-grass-large", 2.7f, 6.8f, 2.4f), ("patch-grass-large", 5.8f, 6.6f, 2.4f), ("patch-grass-large", 0.0f, 7.0f, 2.4f),
            ("fence", 2.75f, 8.8f, 2.4f), ("fence", 2.95f, 8.8f, 2.4f), ("fence", 3.15f, 8.8f, 2.4f),
        };
        foreach ((string name, float angle, float radius, float scale) in props)
        {
            // Flat props (grass patches) sit a hair above the ground so the two surfaces never fight over depth.
            float lift = name.StartsWith("patch-", StringComparison.Ordinal) ? 0.03f : 0f;
            Matrix4x4 placement = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(angle + MathF.PI) * Matrix4x4.CreateTranslation(MathF.Sin(angle) * radius, lift, MathF.Cos(angle) * radius);
            Add(Load("survival-kit", name), null, 0, _ => placement);
        }
    }

    /// <summary>The shared textures and materials.</summary>
    public AssetCache Cache { get; }

    /// <summary>How many models are placed (actors and props, not counting the ground).</summary>
    public int ActorCount => actors.Count;

    /// <summary>How many distinct model files were loaded.</summary>
    public int ModelFileCount => models.Count;

    /// <summary>How many mesh draws the last <see cref="Draw" /> submitted.</summary>
    public int LastDrawableCount { get; private set; }

    /// <summary>How many triangles the last <see cref="Draw" /> submitted.</summary>
    public int LastTriangleCount { get; private set; }

    /// <summary>The world positions of the animals, for the HUD's mini map.</summary>
    public IEnumerable<Vector3> AnimalPositions
    {
        get
        {
            foreach (Actor actor in actors)
            {
                if (actor.Model.Name.StartsWith("animal-", StringComparison.Ordinal)) yield return actor.World.Translation;
            }
        }
    }

    /// <summary>The Avian's world position.</summary>
    public Vector3 BirdPosition { get; private set; }

    /// <summary>Advances every animation to <paramref name="time" /> seconds.</summary>
    /// <param name="time">The scene time.</param>
    public void Update(float time)
    {
        foreach (Actor actor in actors)
        {
            if (actor.Track >= 0) actor.Instance.Armature.SetAnimationFrame(actor.Track, time + actor.TimeOffset, true);
            actor.World = actor.Placement(time);
            if (actor.Model.Name == "avian") BirdPosition = actor.World.Translation;
        }
    }

    /// <summary>Draws the ground and every actor.</summary>
    /// <param name="renderer">A renderer between Begin and End.</param>
    public void Draw(MeshRenderer renderer)
    {
        int drawables = 1;
        int triangles = ground.IndexCount / 3;
        renderer.Draw(ground, groundMaterial, Matrix4x4.Identity);
        foreach (Actor actor in actors)
        {
            foreach (DrawableInstance drawable in actor.Instance)
            {
                IGeometryTransform transform = drawable.Transform;
                if (!transform.Visible) continue;
                IReadOnlyList<ModelPart> parts = actor.Model.PartsByMesh[drawable.Template.LogicalMeshIndex];
                switch (transform)
                {
                    case InstancingTransform instancing:
                        foreach (RigidTransform instance in instancing.WorldTransforms)
                        {
                            foreach (ModelPart part in parts) renderer.Draw(part.Mesh, part.Material, instance.WorldMatrix * actor.World);
                        }

                        break;
                    case RigidTransform rigid:
                        foreach (ModelPart part in parts)
                        {
                            renderer.Draw(part.Mesh, part.Material, rigid.WorldMatrix * actor.World);
                            drawables++;
                            triangles += part.Mesh.IndexCount / 3;
                        }

                        break;
                }
            }
        }

        LastDrawableCount = drawables;
        LastTriangleCount = triangles;
    }

    private void Add(GltfModel model, string track, float offset, Func<float, Matrix4x4> placement)
    {
        int trackIndex = model.TrackIndex(track);
        if (track != null && trackIndex < 0) throw new InvalidDataException(model.Name + " has no '" + track + "' animation (it has: " + string.Join(", ", model.TrackNames) + ").");
        actors.Add(new Actor(model, model.CreateInstance(), trackIndex, offset, placement));
    }

    private sealed class Actor
    {
        public Actor(GltfModel model, SceneInstance instance, int track, float offset, Func<float, Matrix4x4> placement)
        {
            Model = model;
            Instance = instance;
            Track = track;
            TimeOffset = offset;
            Placement = placement;
            World = placement(0);
        }

        public GltfModel Model { get; }

        public SceneInstance Instance { get; }

        public int Track { get; }

        public float TimeOffset { get; }

        public Func<float, Matrix4x4> Placement { get; }

        public Matrix4x4 World { get; set; }
    }
}
