using System;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One placement of a <see cref="Model" /> with its own state: a world transform, every node's local transform
/// (translation, rotation, scale - what animation playback drives), the node matrices computed from them, and per-node
/// morph weights. Create any number per model (<see cref="Model.CreateInstance()" />); an instance holds no GPU objects
/// and is drawn by a <see cref="ModelRenderer" />. Pose changes go through <see cref="SetLocalTransform" />,
/// <see cref="SetMorphWeight" />, <see cref="BeginPoseEdit" /> or the instance's <see cref="Animation" /> player, so
/// derived data (node matrices, joint palettes) is always recomputed after a change. Not thread-safe (different
/// instances may be updated on different threads).
/// </summary>
public sealed class ModelInstance
{
    private readonly NodeTransform[] locals;
    private readonly Matrix4x4[] nodeMatrices;
    private readonly float[] morphWeights;
    private readonly Matrix4x4[] palette;
    private bool posed;
    private int paletteVersion = -1;
    private AnimationPlayer player;
    private int editId;
    private int lastEditId;

    internal ModelInstance(Model model)
    {
        Model = model;
        locals = new NodeTransform[model.Nodes.Count];
        nodeMatrices = new Matrix4x4[model.Nodes.Count];
        morphWeights = new float[model.MorphWeightCount];
        palette = new Matrix4x4[model.PaletteSize];
        ResetPose();
    }

    /// <summary>The model.</summary>
    public Model Model { get; }

    /// <summary>The model-to-world matrix. Default identity.</summary>
    public Matrix4x4 World { get; set; } = Matrix4x4.Identity;

    /// <summary>False to skip the instance when drawing. Default true.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>
    /// Every node's local transform (index = node index), read-only. Change it with
    /// <see cref="SetLocalTransform(int, in NodeTransform)" />, an <see cref="AnimationPlayer" /> or, for many nodes at
    /// once, inside <see cref="BeginPoseEdit" />. Nodes with a fixed matrix (<see cref="ModelNode.HasFixedMatrix" />)
    /// ignore their entry.
    /// </summary>
    public ReadOnlySpan<NodeTransform> LocalTransforms => locals;

    /// <summary>
    /// This instance's animation player (created on first use; instances that never animate pay nothing). Call
    /// <see cref="AnimationPlayer.Update(float)" /> once per frame, or <see cref="AnimationPlayer.UpdateAll" /> for many
    /// instances.
    /// </summary>
    public AnimationPlayer Animation => player ??= new AnimationPlayer(this);

    /// <summary>True once <see cref="Animation" /> has been used.</summary>
    public bool HasAnimationPlayer => player != null;

    /// <summary>True between <see cref="BeginPoseEdit" /> and the end of that edit.</summary>
    public bool IsEditingPose => editId != 0;

    /// <summary>
    /// Every node's matrix in model space (node-to-model; multiply by <see cref="World" /> for world space), recomputed
    /// first when the pose changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">A pose edit is open (<see cref="BeginPoseEdit" />).</exception>
    public ReadOnlySpan<Matrix4x4> NodeMatrices
    {
        get
        {
            if (!posed)
            {
                ThrowIfEditing();
                UpdateNodeMatrices();
            }

            return nodeMatrices;
        }
    }

    /// <summary>Increments whenever the pose may have changed (renderers use it to cache derived data).</summary>
    public int PoseVersion { get; private set; }

    /// <summary>A node's local transform.</summary>
    /// <param name="node">The node index.</param>
    /// <returns>The transform.</returns>
    public NodeTransform GetLocalTransform(int node) => locals[node];

    /// <summary>Sets a node's local transform.</summary>
    /// <param name="node">The node index.</param>
    /// <param name="transform">The transform.</param>
    public void SetLocalTransform(int node, in NodeTransform transform)
    {
        locals[node] = transform;
        MarkPoseChanged();
    }

    /// <summary>
    /// Opens a pose edit: the returned editor exposes every local transform and morph weight for writing in place.
    /// The pose counts as changed when the edit ends (<see cref="ModelPoseEdit.Dispose" />, so <c>using</c> it is
    /// the normal form); until then reading <see cref="NodeMatrices" />, <see cref="GetJointPalette" /> or
    /// <see cref="GetNodeWorldMatrix" />, drawing the instance or updating its animation throws, so a write can never
    /// be missed by a stale cache.
    /// </summary>
    /// <returns>The editor (a ref struct; it cannot outlive the calling method).</returns>
    /// <exception cref="InvalidOperationException">An edit is already open.</exception>
    public ModelPoseEdit BeginPoseEdit()
    {
        ThrowIfEditing();
        lastEditId = lastEditId == int.MaxValue ? 1 : lastEditId + 1;
        editId = lastEditId;
        MarkPoseChanged();
        return new ModelPoseEdit(this, editId, locals);
    }

    /// <summary>Ends the open pose edit (what <see cref="ModelPoseEdit.Dispose" /> calls); does nothing when none is open.</summary>
    public void EndPoseEdit()
    {
        if (editId == 0) return;
        editId = 0;
        MarkPoseChanged();
    }

    /// <summary>Ends an edit only when it is still the open one (a copied editor disposed twice is harmless).</summary>
    /// <param name="id">The edit id.</param>
    internal void EndPoseEdit(int id)
    {
        if (id == editId) EndPoseEdit();
    }

    /// <summary>A node's matrix in world space (<see cref="NodeMatrices" /> times <see cref="World" />).</summary>
    /// <param name="node">The node index.</param>
    /// <returns>The matrix.</returns>
    /// <exception cref="InvalidOperationException">A pose edit is open (<see cref="BeginPoseEdit" />).</exception>
    public Matrix4x4 GetNodeWorldMatrix(int node) => NodeMatrices[node] * World;

    /// <summary>
    /// A node's current morph weights (empty when the node's mesh has no morph targets), read-only. They start at the
    /// node's <see cref="ModelNode.DefaultMorphWeights" />; change them with <see cref="SetMorphWeight" />,
    /// <see cref="SetMorphWeights" />, an <see cref="AnimationPlayer" /> (weights channels) or inside
    /// <see cref="BeginPoseEdit" />.
    /// </summary>
    /// <param name="node">The node index.</param>
    /// <returns>The weights.</returns>
    public ReadOnlySpan<float> GetMorphWeights(int node) => MorphWeightSpan(node);

    /// <summary>Sets one morph weight of a node.</summary>
    /// <param name="node">The node index.</param>
    /// <param name="target">The morph target index.</param>
    /// <param name="weight">The weight.</param>
    /// <exception cref="ArgumentOutOfRangeException">The node has no such morph target.</exception>
    public void SetMorphWeight(int node, int target, float weight)
    {
        Span<float> weights = MorphWeightSpan(node);
        if ((uint)target >= (uint)weights.Length) throw new ArgumentOutOfRangeException(nameof(target), "Node " + node + " has " + weights.Length + " morph weight(s).");
        weights[target] = weight;
        MarkPoseChanged();
    }

    /// <summary>Sets a node's morph weights (the first <c>min(weights.Length, count)</c> of them).</summary>
    /// <param name="node">The node index.</param>
    /// <param name="weights">The weights.</param>
    public void SetMorphWeights(int node, ReadOnlySpan<float> weights)
    {
        Span<float> destination = MorphWeightSpan(node);
        int count = Math.Min(destination.Length, weights.Length);
        weights.Slice(0, count).CopyTo(destination);
        MarkPoseChanged();
    }

    /// <summary>
    /// A skin's joint palette in model space: for joint <c>j</c>, <c>InverseBindMatrices[j] * NodeMatrices[Joints[j]]</c>
    /// (System.Numerics row-vector order; multiply by <see cref="World" /> for world space). A skinned vertex is
    /// <c>sum(weight_i * Vector3.Transform(position, palette[joint_i]))</c>. Recomputed from the current pose when it
    /// changed (no allocation); in the rest pose of a typical export every matrix is the identity.
    /// </summary>
    /// <param name="skin">The skin index (<see cref="Model.Skins" />).</param>
    /// <returns>One matrix per joint of the skin.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The skin does not exist.</exception>
    /// <exception cref="InvalidOperationException">A pose edit is open (<see cref="BeginPoseEdit" />).</exception>
    public ReadOnlySpan<Matrix4x4> GetJointPalette(int skin)
    {
        if ((uint)skin >= (uint)Model.Skins.Count) throw new ArgumentOutOfRangeException(nameof(skin), "The model has " + Model.Skins.Count + " skin(s).");
        return JointPalettes.Slice(Model.SkinPaletteOffsets[skin], Model.Skins[skin].JointCount);
    }

    /// <summary>Every skin's palette, skin after skin (<see cref="Model.SkinPaletteOffsets" />), recomputed when the pose changed.</summary>
    internal ReadOnlySpan<Matrix4x4> JointPalettes
    {
        get
        {
            if (paletteVersion != PoseVersion || !posed) UpdateJointPalettes();
            return palette;
        }
    }

    /// <summary>Every morph weight (laid out by <see cref="Model.MorphWeightOffsets" />), read without marking the pose changed.</summary>
    internal ReadOnlySpan<float> MorphWeights => morphWeights;

    /// <summary>The writable local transforms (animation playback writes them, then calls <see cref="MarkPoseChanged" />).</summary>
    internal NodeTransform[] LocalArray => locals;

    /// <summary>The writable morph weights (laid out by <see cref="Model.MorphWeightOffsets" />).</summary>
    internal float[] MorphWeightArray => morphWeights;

    /// <summary>Records that the pose changed: node matrices and palettes are recomputed on their next read.</summary>
    internal void MarkPoseChanged()
    {
        posed = false;
        PoseVersion++;
    }

    /// <summary>Throws when a pose edit is open.</summary>
    /// <exception cref="InvalidOperationException">A pose edit is open.</exception>
    internal void ThrowIfEditing()
    {
        if (editId != 0) throw new InvalidOperationException("The instance has an open pose edit (BeginPoseEdit); dispose the editor (or call EndPoseEdit) first.");
    }

    /// <summary>A node's writable morph weights.</summary>
    /// <param name="node">The node index.</param>
    /// <returns>The weights (empty without morph targets).</returns>
    internal Span<float> MorphWeightSpan(int node)
    {
        int offset = Model.MorphWeightOffsets[node];
        if (offset < 0) return Span<float>.Empty;
        return morphWeights.AsSpan(offset, Model.Nodes[node].DefaultMorphWeights.Length);
    }

    /// <summary>Returns every node to its rest pose and every morph weight to its default.</summary>
    public void ResetPose()
    {
        for (int node = 0; node < locals.Length; node++)
        {
            ModelNode data = Model.Nodes[node];
            locals[node] = data.DefaultTransform;
            int offset = Model.MorphWeightOffsets[node];
            if (offset >= 0) data.DefaultMorphWeights.Span.CopyTo(morphWeights.AsSpan(offset));
        }

        MarkPoseChanged();
    }

    /// <summary>Recomputes <see cref="NodeMatrices" /> from the local transforms (parents first). No allocation.</summary>
    /// <exception cref="InvalidOperationException">A pose edit is open (<see cref="BeginPoseEdit" />).</exception>
    public void UpdateNodeMatrices()
    {
        ThrowIfEditing();
        int[] order = Model.NodeOrder;
        for (int index = 0; index < order.Length; index++)
        {
            int node = order[index];
            ModelNode data = Model.Nodes[node];
            Matrix4x4 local = data.HasFixedMatrix ? data.LocalMatrix : locals[node].ToMatrix();
            nodeMatrices[node] = data.Parent >= 0 ? local * nodeMatrices[data.Parent] : local;
        }

        posed = true;
    }

    private void UpdateJointPalettes()
    {
        ReadOnlySpan<Matrix4x4> nodes = NodeMatrices;
        for (int skin = 0; skin < Model.Skins.Count; skin++)
        {
            ModelSkin data = Model.Skins[skin];
            ReadOnlySpan<int> joints = data.Joints.Span;
            ReadOnlySpan<Matrix4x4> inverseBind = data.InverseBindMatrices.Span;
            Span<Matrix4x4> destination = palette.AsSpan(Model.SkinPaletteOffsets[skin], joints.Length);
            for (int joint = 0; joint < joints.Length; joint++) destination[joint] = inverseBind[joint] * nodes[joints[joint]];
        }

        paletteVersion = PoseVersion;
    }
}
