using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using CodeBrix.WebGpu.Graphics;
using Gltf = CodeBrix.Graphics3D.Gltf2.Schema2;

namespace WebGpuModelViewer.Scene;

/// <summary>
/// The little scene: a grass plane with trees, rocks, barrels and a camp fire; Cube Pets animals walking a ring and
/// idling, eating and dancing around a chest that opens and closes; the Avian flying circles overhead. Every model is
/// loaded ONCE with <see cref="GraphicsContext.LoadModel(string, ModelLoadOptions)" /> (the kits' shared colour maps
/// through one <see cref="ModelTextureCache" />), and each actor is a <see cref="ModelInstance" /> of it, placed by
/// its world matrix and animated by its own <see cref="AnimationPlayer" />.
/// </summary>
internal sealed class ViewerScene : IDisposable
{
    /// <summary>How long a standing animal keeps one clip before it cross-fades to the next, in seconds.</summary>
    private const float RoutineSeconds = 3.5f;

    /// <summary>How long a cross-fade between two clips takes, in seconds.</summary>
    private const float CrossFadeSeconds = 0.4f;

    private static readonly string[] Animals = { "animal-cat", "animal-dog", "animal-fox", "animal-pig", "animal-cow", "animal-panda", "animal-penguin", "animal-bunny" };
    private static readonly string[] Routine = { "idle", "dance", "eat", "gesture-positive", "idle", "dance" };

    private readonly List<Actor> actors = new List<Actor>();
    private readonly Dictionary<string, Model> models = new Dictionary<string, Model>(StringComparer.Ordinal);
    private readonly ModelInstance[] instances;
    private readonly Vector3[] animalPositions;
    private readonly int birdActor = -1;
    private float lastTime;

    /// <summary>Loads every model and lays out the scene.</summary>
    /// <param name="context">The graphics context.</param>
    /// <param name="assets">The Assets folder.</param>
    public ViewerScene(GraphicsContext context, string assets)
    {
        TextureCache = new ModelTextureCache();
        ModelLoadOptions options = new ModelLoadOptions { TextureCache = TextureCache };

        Model Load(string pack, string name)
        {
            if (models.TryGetValue(name, out Model loaded)) return loaded;
            Model model = context.LoadModel(Path.Combine(assets, pack, name + ".glb"), options);
            models[name] = model;
            return model;
        }

        Ground = CreateGround(context);
        GroundInstance = Ground.CreateInstance();

        // The chest in the middle, opening and closing.
        Add(Load("survival-kit", "chest"), "open-close", null, 0, _ => Matrix4x4.CreateScale(3.2f) * Matrix4x4.CreateRotationY(0.6f));

        // Four animals walk a ring; four more stand closer in, facing the chest, and cross-fade through a routine of
        // idling, dancing, eating and cheering.
        int animalCount = 0;
        for (int index = 0; index < Animals.Length; index++)
        {
            Model animal = Load("cube-pets", Animals[index]);
            float baseAngle = index * MathF.Tau / Animals.Length;
            if (index % 2 == 0)
            {
                Add(animal, "walk", null, index * 0.13f, time =>
                {
                    float angle = baseAngle + (time * 0.28f);
                    Vector3 position = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * 5.4f;
                    return Matrix4x4.CreateScale(0.55f) * Matrix4x4.CreateRotationY(angle + (MathF.PI / 2)) * Matrix4x4.CreateTranslation(position);
                });
            }
            else
            {
                Vector3 position = new Vector3(MathF.Sin(baseAngle), 0, MathF.Cos(baseAngle)) * 2.9f;
                Matrix4x4 placement = Matrix4x4.CreateScale(0.55f) * Matrix4x4.CreateRotationY(baseAngle + MathF.PI) * Matrix4x4.CreateTranslation(position);
                Add(animal, null, Routine, index * 0.9f, _ => placement);
            }

            animalCount++;
        }

        // The Avian circles overhead, the other way round.
        birdActor = actors.Count;
        Add(Load("crystals-and-avian", "avian"), "fly", null, 0, time =>
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
            Add(Load("survival-kit", name), null, null, 0, _ => placement);
        }

        instances = new ModelInstance[actors.Count];
        int triangles = Ground.TriangleCount;
        for (int index = 0; index < actors.Count; index++)
        {
            instances[index] = actors[index].Instance;
            triangles += actors[index].Instance.Model.TriangleCount;
        }

        int materials = 0;
        foreach (Model model in models.Values) materials += model.Materials.Count;
        MaterialCount = materials;

        TriangleCount = triangles;
        animalPositions = new Vector3[animalCount];
        Update(0);
    }

    /// <summary>The colour maps shared by the kits' models (each pack's colormap.png is decoded and uploaded once).</summary>
    public ModelTextureCache TextureCache { get; }

    /// <summary>The ground plane (a glTF model built in memory).</summary>
    public Model Ground { get; }

    /// <summary>The ground plane's one instance.</summary>
    public ModelInstance GroundInstance { get; }

    /// <summary>How many models are placed (actors and props, not counting the ground).</summary>
    public int ActorCount => actors.Count;

    /// <summary>How many distinct model files were loaded.</summary>
    public int ModelFileCount => models.Count;

    /// <summary>How many glTF materials the loaded model files carry.</summary>
    public int MaterialCount { get; }

    /// <summary>Triangles in one frame of the scene (the ground and every placed model).</summary>
    public int TriangleCount { get; }

    /// <summary>The world positions of the animals, for the HUD's mini map.</summary>
    public ReadOnlySpan<Vector3> AnimalPositions => animalPositions;

    /// <summary>The Avian's world position.</summary>
    public Vector3 BirdPosition { get; private set; }

    /// <summary>
    /// Moves the scene to <paramref name="time" /> seconds: starts the routine clips due by then (cross-fading from
    /// the previous one), advances every animation player by the time since the last update and places the actors.
    /// </summary>
    /// <param name="time">The scene time. A time earlier than the last one restarts nothing; the players just hold.</param>
    public void Update(float time)
    {
        float delta = Math.Max(0f, time - lastTime);
        lastTime = time;
        int animal = 0;
        for (int index = 0; index < actors.Count; index++)
        {
            Actor actor = actors[index];
            if (actor.Routine != null)
            {
                int step = (int)MathF.Floor((time + actor.TimeOffset) / RoutineSeconds);
                if (step != actor.RoutineStep)
                {
                    string clip = actor.Routine[((step % actor.Routine.Length) + actor.Routine.Length) % actor.Routine.Length];
                    if (actor.RoutineStep == int.MinValue) actor.Instance.Animation.Play(clip);
                    else actor.Instance.Animation.CrossFade(clip, CrossFadeSeconds);
                    actor.RoutineStep = step;
                }
            }

            Matrix4x4 world = actor.Placement(time);
            actor.Instance.World = world;
            if (index == birdActor) BirdPosition = world.Translation;
            else if (actor.IsAnimal) animalPositions[animal++] = world.Translation;
        }

        AnimationPlayer.UpdateAll(instances, delta);
    }

    /// <summary>Draws the ground and every actor (one instanced draw per model primitive for all its instances).</summary>
    /// <param name="renderer">A model renderer between Begin and End.</param>
    public void Draw(ModelRenderer renderer)
    {
        renderer.Draw(GroundInstance);
        foreach (ModelInstance instance in instances) renderer.Draw(instance);
    }

    /// <summary>Releases the models, then the shared textures.</summary>
    public void Dispose()
    {
        foreach (Model model in models.Values) model.Dispose();
        Ground.Dispose();
        TextureCache.Dispose();
    }

    private void Add(Model model, string clip, string[] routine, float offset, Func<float, Matrix4x4> placement)
    {
        if (clip != null && model.FindAnimation(clip) < 0) throw new InvalidDataException(model.Name + " has no '" + clip + "' animation.");
        ModelInstance instance = model.CreateInstance(placement(0));

        // A clip that loops from its own start time, so the actors do not move in step.
        if (clip != null) instance.Animation.Play(clip, AnimationLoopMode.Loop, 1f, offset);
        actors.Add(new Actor(instance, routine, offset, placement, model.Name.StartsWith("animal-", StringComparison.Ordinal)));
    }

    /// <summary>A 180 x 180 grass-green plane (reaching nearly to the horizon) facing up: one quad with a rough, non-metallic material.</summary>
    private static Model CreateGround(GraphicsContext context)
    {
        const float Half = 90f;
        Gltf.ModelRoot root = Gltf.ModelRoot.CreateModel();
        Gltf.Material material = Gltf.Toolkit.WithPBRMetallicRoughness(root.CreateMaterial("grass"));

        // glTF colour factors are linear: sRGB (110, 168, 84) is the green the scene always had.
        Gltf.Toolkit.WithChannelColor(material, "BaseColor", new Vector4(Linear(110), Linear(168), Linear(84), 1f));
        Gltf.Toolkit.WithChannelFactor(material, "MetallicRoughness", "MetallicFactor", 0f);
        Gltf.Toolkit.WithChannelFactor(material, "MetallicRoughness", "RoughnessFactor", 0.95f);

        Gltf.MeshPrimitive primitive = root.CreateMesh("ground").CreatePrimitive();
        Gltf.Toolkit.WithVertexAccessor(primitive, "POSITION", new[] { new Vector3(-Half, 0, Half), new Vector3(Half, 0, Half), new Vector3(Half, 0, -Half), new Vector3(-Half, 0, -Half) });
        Gltf.Toolkit.WithVertexAccessor(primitive, "NORMAL", new[] { Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY });
        Gltf.Toolkit.WithIndicesAccessor(primitive, Gltf.PrimitiveType.TRIANGLES, new[] { 0, 1, 2, 0, 2, 3 });
        Gltf.Toolkit.WithMaterial(primitive, material);
        root.UseScene(0).CreateNode("ground").Mesh = root.LogicalMeshes[0];

        using MemoryStream glb = new MemoryStream(root.WriteGLB().ToArray());
        return context.LoadModel(glb, null, new ModelLoadOptions { Label = "ground" });
    }

    private static float Linear(byte srgb)
    {
        float c = srgb / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    private sealed class Actor
    {
        public Actor(ModelInstance instance, string[] routine, float offset, Func<float, Matrix4x4> placement, bool isAnimal)
        {
            Instance = instance;
            Routine = routine;
            TimeOffset = offset;
            Placement = placement;
            IsAnimal = isAnimal;
        }

        public ModelInstance Instance { get; }

        public string[] Routine { get; }

        public int RoutineStep { get; set; } = int.MinValue;

        public float TimeOffset { get; }

        public Func<float, Matrix4x4> Placement { get; }

        public bool IsAnimal { get; }
    }
}
