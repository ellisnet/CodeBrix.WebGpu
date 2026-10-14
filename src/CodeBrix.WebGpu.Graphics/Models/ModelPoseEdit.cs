using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// An open pose edit of a <see cref="ModelInstance" /> (<see cref="ModelInstance.BeginPoseEdit" />): write local
/// transforms and morph weights in place, then dispose it (a <c>using</c> statement) - the pose counts as changed when
/// the edit ends. While it is open the instance refuses to compute node matrices or joint palettes, to be drawn or to
/// be animated, so no write can be missed.
/// </summary>
/// <example>
/// <code>
/// using (ModelPoseEdit pose = instance.BeginPoseEdit())
/// {
///     pose.LocalTransforms[arm].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
///     pose.GetMorphWeights(face)[0] = 1f;
/// }
/// </code>
/// </example>
public readonly ref struct ModelPoseEdit
{
    private readonly ModelInstance instance;
    private readonly int id;
    private readonly Span<NodeTransform> locals;

    internal ModelPoseEdit(ModelInstance instance, int id, Span<NodeTransform> locals)
    {
        this.instance = instance;
        this.id = id;
        this.locals = locals;
    }

    /// <summary>The instance being edited.</summary>
    public ModelInstance Instance => instance;

    /// <summary>Every node's local transform, writable in place (index = node index).</summary>
    public Span<NodeTransform> LocalTransforms => locals;

    /// <summary>A node's morph weights, writable in place (empty when the node's mesh has no morph targets).</summary>
    /// <param name="node">The node index.</param>
    /// <returns>The weights.</returns>
    public Span<float> GetMorphWeights(int node) => instance.MorphWeightSpan(node);

    /// <summary>Ends the edit (the pose counts as changed). Disposing a copy of an editor whose edit already ended does nothing.</summary>
    public void Dispose() => instance?.EndPoseEdit(id);
}
