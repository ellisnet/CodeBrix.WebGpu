using System;
using System.Numerics;
using CodeBrix.WebGpu.Graphics.Animation;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Plays a <see cref="Model" />'s animation clips on one <see cref="ModelInstance" /> (<see cref="ModelInstance.Animation" />).
/// The clips are shared, immutable model data; the player holds only this instance's playback state: up to
/// <see cref="MaxStates" /> clips at once, each with its own playhead, speed, loop mode, weight and layer. Every
/// <see cref="Update(float)" /> advances the playheads and writes the blended pose into the instance's local
/// transforms and morph weights; node matrices, joint palettes (skinning) and the renderer's morph weights follow
/// from there. Updating allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// Playing: <see cref="Play(int, AnimationLoopMode, float, float, int)" /> replaces what plays on a layer;
/// <see cref="CrossFade(int, float, AnimationLoopMode, float, float, int)" /> fades the layer's current clips out
/// while the new one fades in; <see cref="Blend(int, float, AnimationLoopMode, float, float, int)" /> adds a clip
/// with a weight beside the others. Clip names or indices (<see cref="Model.Animations" />) both work.
/// </para>
/// <para>
/// Blending: per animated property (a node's translation, rotation, scale or morph weights) the states of one layer
/// are averaged by weight (rotations: weighted quaternion sum with hemisphere correction, normalised; weights adding
/// up past 1 are normalised). A layer then replaces what lies below it (the rest pose, then lower layers) by its
/// summed weight up to 1, so a single clip fading in from weight 0 blends from the rest pose, and a higher layer
/// with weight 1 overrides only the properties its clips animate. Properties no playing clip animates keep whatever
/// they hold (set them with <see cref="ModelInstance.SetLocalTransform" />); when a clip stops, the properties it
/// animated return to the rest pose until another clip drives them.
/// </para>
/// <para>
/// Time: <see cref="Speed" /> scales every clip and every fade; each <see cref="AnimationState.Speed" /> scales its
/// own clip. <see cref="AnimationLoopMode.Once" /> holds the last frame and raises <see cref="ClipFinished" /> (and
/// <see cref="AnimationState.FinishedThisUpdate" />) in the update that reached the end.
/// </para>
/// <para>
/// Not thread-safe; different instances may be updated on different threads (they share only immutable model data).
/// </para>
/// </remarks>
public sealed class AnimationPlayer
{
    /// <summary>The most clips one player plays at once (cross-fades included).</summary>
    public const int MaxStates = 8;

    private const int PathCount = 4;

    private readonly ModelInstance instance;
    private readonly Model model;
    private readonly AnimationState[] pool = new AnimationState[MaxStates];
    private readonly AnimationState[] active = new AnimationState[MaxStates];
    private readonly AnimationState[] finished = new AnimationState[MaxStates];
    private readonly float[] sample;
    private int activeCount;

    private BlendScratch blend;

    internal AnimationPlayer(ModelInstance instance)
    {
        this.instance = instance;
        model = instance.Model;
        sample = new float[Math.Max(4, model.MaxAnimationComponents)];
    }

    /// <summary>The instance this player poses.</summary>
    public ModelInstance Instance => instance;

    /// <summary>The model whose clips play.</summary>
    public Model Model => model;

    /// <summary>Scales every clip's time and every fade (1 = normal; 0 freezes like <see cref="Paused" />).</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>True to freeze every playhead and fade (<see cref="Update(float)" /> still applies the current pose).</summary>
    public bool Paused { get; set; }

    /// <summary>True while at least one clip plays.</summary>
    public bool IsPlaying => activeCount > 0;

    /// <summary>The number of playing clips (states).</summary>
    public int StateCount => activeCount;

    /// <summary>
    /// Called during <see cref="Update(float)" /> for each once-clip that reached its end in that update (assign a
    /// cached delegate: invoking it allocates nothing). It may start other clips.
    /// </summary>
    public Action<AnimationState> ClipFinished { get; set; }

    /// <summary>A playing state.</summary>
    /// <param name="index">0..<see cref="StateCount" /> - 1 (start order).</param>
    /// <returns>The state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such state.</exception>
    public AnimationState GetState(int index)
    {
        if ((uint)index >= (uint)activeCount) throw new ArgumentOutOfRangeException(nameof(index), "The player has " + activeCount + " playing state(s).");
        return active[index];
    }

    /// <summary>The most recently started playing state of a clip, or null.</summary>
    /// <param name="clip">The clip index.</param>
    /// <returns>The state, or null.</returns>
    public AnimationState FindState(int clip)
    {
        for (int index = activeCount - 1; index >= 0; index--)
        {
            if (active[index].ClipIndex == clip) return active[index];
        }

        return null;
    }

    /// <summary>Plays a clip at weight 1, replacing every clip on its layer.</summary>
    /// <param name="clip">The clip index (<see cref="Model.Animations" />).</param>
    /// <param name="loopMode">Loop, ping-pong or once (hold the last frame).</param>
    /// <param name="speed">The clip's playback rate.</param>
    /// <param name="startTime">The starting playhead in clip seconds.</param>
    /// <param name="layer">The blend layer.</param>
    /// <returns>The clip's state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such clip.</exception>
    public AnimationState Play(int clip, AnimationLoopMode loopMode = AnimationLoopMode.Loop, float speed = 1f, float startTime = 0f, int layer = 0)
    {
        ModelAnimation animation = Clip(clip);
        Stop(layer);
        return Start(animation, loopMode, speed, startTime, 1f, layer);
    }

    /// <summary>Plays a clip by name (see <see cref="Play(int, AnimationLoopMode, float, float, int)" />).</summary>
    /// <param name="clip">The clip name.</param>
    /// <param name="loopMode">Loop, ping-pong or once (hold the last frame).</param>
    /// <param name="speed">The clip's playback rate.</param>
    /// <param name="startTime">The starting playhead in clip seconds.</param>
    /// <param name="layer">The blend layer.</param>
    /// <returns>The clip's state.</returns>
    /// <exception cref="ArgumentException">The model has no clip with that name.</exception>
    public AnimationState Play(string clip, AnimationLoopMode loopMode = AnimationLoopMode.Loop, float speed = 1f, float startTime = 0f, int layer = 0) =>
        Play(ClipIndex(clip), loopMode, speed, startTime, layer);

    /// <summary>
    /// Fades a clip in over <paramref name="duration" /> seconds while every other clip on its layer fades out (and
    /// stops at weight 0). A duration of 0 or less is <see cref="Play(int, AnimationLoopMode, float, float, int)" />.
    /// </summary>
    /// <param name="clip">The clip index.</param>
    /// <param name="duration">The fade length in seconds (player time).</param>
    /// <param name="loopMode">Loop, ping-pong or once.</param>
    /// <param name="speed">The clip's playback rate.</param>
    /// <param name="startTime">The starting playhead in clip seconds.</param>
    /// <param name="layer">The blend layer.</param>
    /// <returns>The new clip's state (weight 0, fading to 1).</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such clip.</exception>
    /// <exception cref="InvalidOperationException"><see cref="MaxStates" /> clips play and none of them is fading out.</exception>
    public AnimationState CrossFade(int clip, float duration, AnimationLoopMode loopMode = AnimationLoopMode.Loop, float speed = 1f, float startTime = 0f, int layer = 0)
    {
        ModelAnimation animation = Clip(clip);
        if (duration <= 0f) return Play(clip, loopMode, speed, startTime, layer);
        for (int index = 0; index < activeCount; index++)
        {
            AnimationState state = active[index];
            if (state.Layer == layer) state.FadeTo(0f, duration, true);
        }

        AnimationState started = Start(animation, loopMode, speed, startTime, 0f, layer);
        started.FadeTo(1f, duration);
        return started;
    }

    /// <summary>Cross-fades to a clip by name (see <see cref="CrossFade(int, float, AnimationLoopMode, float, float, int)" />).</summary>
    /// <param name="clip">The clip name.</param>
    /// <param name="duration">The fade length in seconds.</param>
    /// <param name="loopMode">Loop, ping-pong or once.</param>
    /// <param name="speed">The clip's playback rate.</param>
    /// <param name="startTime">The starting playhead in clip seconds.</param>
    /// <param name="layer">The blend layer.</param>
    /// <returns>The new clip's state.</returns>
    /// <exception cref="ArgumentException">The model has no clip with that name.</exception>
    public AnimationState CrossFade(string clip, float duration, AnimationLoopMode loopMode = AnimationLoopMode.Loop, float speed = 1f, float startTime = 0f, int layer = 0) =>
        CrossFade(ClipIndex(clip), duration, loopMode, speed, startTime, layer);

    /// <summary>Adds a clip with a weight beside the clips already playing (layered or weighted blending).</summary>
    /// <param name="clip">The clip index.</param>
    /// <param name="weight">The blend weight.</param>
    /// <param name="loopMode">Loop, ping-pong or once.</param>
    /// <param name="speed">The clip's playback rate.</param>
    /// <param name="startTime">The starting playhead in clip seconds.</param>
    /// <param name="layer">The blend layer.</param>
    /// <returns>The clip's state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">No such clip.</exception>
    /// <exception cref="InvalidOperationException"><see cref="MaxStates" /> clips play and none of them is fading out.</exception>
    public AnimationState Blend(int clip, float weight, AnimationLoopMode loopMode = AnimationLoopMode.Loop, float speed = 1f, float startTime = 0f, int layer = 0) =>
        Start(Clip(clip), loopMode, speed, startTime, weight, layer);

    /// <summary>Adds a clip by name with a weight (see <see cref="Blend(int, float, AnimationLoopMode, float, float, int)" />).</summary>
    /// <param name="clip">The clip name.</param>
    /// <param name="weight">The blend weight.</param>
    /// <param name="loopMode">Loop, ping-pong or once.</param>
    /// <param name="speed">The clip's playback rate.</param>
    /// <param name="startTime">The starting playhead in clip seconds.</param>
    /// <param name="layer">The blend layer.</param>
    /// <returns>The clip's state.</returns>
    /// <exception cref="ArgumentException">The model has no clip with that name.</exception>
    public AnimationState Blend(string clip, float weight, AnimationLoopMode loopMode = AnimationLoopMode.Loop, float speed = 1f, float startTime = 0f, int layer = 0) =>
        Blend(ClipIndex(clip), weight, loopMode, speed, startTime, layer);

    /// <summary>Stops every clip; the properties they animated return to the rest pose.</summary>
    public void Stop()
    {
        while (activeCount > 0) Remove(activeCount - 1);
    }

    /// <summary>Stops every clip on a layer.</summary>
    /// <param name="layer">The layer.</param>
    public void Stop(int layer)
    {
        for (int index = activeCount - 1; index >= 0; index--)
        {
            if (active[index].Layer == layer) Remove(index);
        }
    }

    /// <summary>Stops one clip (does nothing when the state is not playing on this player).</summary>
    /// <param name="state">The state.</param>
    public void Stop(AnimationState state)
    {
        for (int index = 0; index < activeCount; index++)
        {
            if (ReferenceEquals(active[index], state))
            {
                Remove(index);
                return;
            }
        }
    }

    /// <summary>
    /// Advances every playhead and fade by <paramref name="deltaSeconds" /> (times <see cref="Speed" />; nothing moves
    /// while <see cref="Paused" />), writes the blended pose into the instance and raises <see cref="ClipFinished" />.
    /// Allocates nothing.
    /// </summary>
    /// <param name="deltaSeconds">The frame time in seconds.</param>
    /// <exception cref="InvalidOperationException">The instance has an open pose edit.</exception>
    public void Update(float deltaSeconds)
    {
        instance.ThrowIfEditing();
        if (activeCount == 0) return;
        float seconds = Paused ? 0f : deltaSeconds * Speed;
        for (int index = 0; index < activeCount; index++)
        {
            AnimationState state = active[index];
            state.ClearFlags();
            state.Advance(seconds * state.Speed);
        }

        for (int index = activeCount - 1; index >= 0; index--)
        {
            if (active[index].AdvanceFade(seconds)) Remove(index);
        }

        Evaluate();
        if (ClipFinished == null) return;
        int count = 0;
        for (int index = 0; index < activeCount; index++)
        {
            if (active[index].FinishedThisUpdate) finished[count++] = active[index];
        }

        for (int index = 0; index < count; index++)
        {
            AnimationState state = finished[index];
            finished[index] = null;
            if (state.IsActive) ClipFinished(state);
        }
    }

    /// <summary>
    /// Writes the pose at the current playheads into the instance without advancing time (after seeking, for
    /// example). Allocates nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The instance has an open pose edit.</exception>
    public void Evaluate()
    {
        instance.ThrowIfEditing();
        if (activeCount == 0) return;
        if (activeCount == 1 && active[0].WeightValue >= 1f) ApplyDirect(active[0]);
        else ApplyBlended();
        instance.MarkPoseChanged();
    }

    /// <summary>
    /// Updates the players of many instances (instances whose <see cref="ModelInstance.Animation" /> was never used are
    /// skipped). For a <c>List&lt;ModelInstance&gt;</c> pass <c>CollectionsMarshal.AsSpan(list)</c>.
    /// </summary>
    /// <param name="instances">The instances.</param>
    /// <param name="deltaSeconds">The frame time in seconds.</param>
    public static void UpdateAll(ReadOnlySpan<ModelInstance> instances, float deltaSeconds)
    {
        for (int index = 0; index < instances.Length; index++)
        {
            ModelInstance item = instances[index];
            if (item != null && item.HasAnimationPlayer) item.Animation.Update(deltaSeconds);
        }
    }

    private ModelAnimation Clip(int clip)
    {
        if ((uint)clip >= (uint)model.Animations.Count) throw new ArgumentOutOfRangeException(nameof(clip), "The model has " + model.Animations.Count + " animation(s).");
        return model.Animations[clip];
    }

    private int ClipIndex(string clip)
    {
        int index = model.FindAnimation(clip);
        if (index < 0) throw new ArgumentException("The model has no animation named '" + clip + "'.", nameof(clip));
        return index;
    }

    private AnimationState Start(ModelAnimation clip, AnimationLoopMode loopMode, float speed, float startTime, float weight, int layer)
    {
        if (activeCount == MaxStates)
        {
            int victim = -1;
            for (int index = 0; index < activeCount; index++)
            {
                AnimationState state = active[index];
                if (state.StopsWhenFaded && (victim < 0 || state.WeightValue < active[victim].WeightValue)) victim = index;
            }

            if (victim < 0) throw new InvalidOperationException("The player already plays " + MaxStates + " clips; stop one first.");
            Remove(victim);
        }

        AnimationState free = null;
        for (int index = 0; index < pool.Length; index++)
        {
            if (pool[index] == null)
            {
                pool[index] = new AnimationState(this, model.MaxAnimationChannels);
                free = pool[index];
                break;
            }

            if (!pool[index].IsActive)
            {
                free = pool[index];
                break;
            }
        }

        free.Start(clip, loopMode, speed, startTime, weight, layer);
        active[activeCount++] = free;
        return free;
    }

    /// <summary>Removes a state and returns the properties its clip animated to the rest pose.</summary>
    private void Remove(int index)
    {
        AnimationState state = active[index];
        ModelAnimationChannel[] channels = state.Clip.ChannelArray;
        NodeTransform[] locals = instance.LocalArray;
        for (int channel = 0; channel < channels.Length; channel++)
        {
            ModelAnimationChannel item = channels[channel];
            ModelNode node = model.Nodes[item.Node];
            switch (item.Path)
            {
                case AnimationPath.Translation:
                    locals[item.Node].Translation = node.DefaultTransform.Translation;
                    break;
                case AnimationPath.Rotation:
                    locals[item.Node].Rotation = node.DefaultTransform.Rotation;
                    break;
                case AnimationPath.Scale:
                    locals[item.Node].Scale = node.DefaultTransform.Scale;
                    break;
                default:
                    int offset = model.MorphWeightOffsets[item.Node];
                    if (offset >= 0) node.DefaultMorphWeights.Span.CopyTo(instance.MorphWeightArray.AsSpan(offset));
                    break;
            }
        }

        state.Release();
        for (int next = index + 1; next < activeCount; next++) active[next - 1] = active[next];
        active[--activeCount] = null;
        instance.MarkPoseChanged();
    }

    /// <summary>One state at full weight: sample straight into the pose.</summary>
    private void ApplyDirect(AnimationState state)
    {
        ModelAnimationChannel[] channels = state.Clip.ChannelArray;
        int[] cursors = state.Cursors;
        NodeTransform[] locals = instance.LocalArray;
        Span<float> value = sample;
        float time = state.Time;
        for (int channel = 0; channel < channels.Length; channel++)
        {
            ModelAnimationChannel item = channels[channel];
            AnimationSampling.Sample(item, time, ref cursors[channel], value);
            switch (item.Path)
            {
                case AnimationPath.Translation:
                    locals[item.Node].Translation = new Vector3(value[0], value[1], value[2]);
                    break;
                case AnimationPath.Rotation:
                    locals[item.Node].Rotation = new Quaternion(value[0], value[1], value[2], value[3]);
                    break;
                case AnimationPath.Scale:
                    locals[item.Node].Scale = new Vector3(value[0], value[1], value[2]);
                    break;
                default:
                    Span<float> weights = instance.MorphWeightSpan(item.Node);
                    int count = Math.Min(weights.Length, item.ComponentCount);
                    value.Slice(0, count).CopyTo(weights);
                    break;
            }
        }
    }

    /// <summary>Several states or partial weights: blend per layer (see the class remarks).</summary>
    private void ApplyBlended()
    {
        blend ??= new BlendScratch(model);
        BlendScratch scratch = blend;
        scratch.NextEvaluation();
        int layer = int.MinValue;
        while (true)
        {
            int next = int.MaxValue;
            for (int index = 0; index < activeCount; index++)
            {
                int candidate = active[index].Layer;
                if (candidate > layer && candidate < next) next = candidate;
            }

            if (next == int.MaxValue) break;
            layer = next;
            scratch.NextLayer();
            for (int index = 0; index < activeCount; index++)
            {
                AnimationState state = active[index];
                if (state.Layer != layer || state.WeightValue <= 0f) continue;
                Accumulate(scratch, state);
            }

            ResolveLayer(scratch);
        }
    }

    private void Accumulate(BlendScratch scratch, AnimationState state)
    {
        ModelAnimationChannel[] channels = state.Clip.ChannelArray;
        int[] cursors = state.Cursors;
        Span<float> value = sample;
        float time = state.Time;
        float weight = state.WeightValue;
        for (int channel = 0; channel < channels.Length; channel++)
        {
            ModelAnimationChannel item = channels[channel];
            int node = item.Node;
            int path = (int)item.Path;
            int property = (node * PathCount) + path;
            int morphOffset = path == (int)AnimationPath.Weights ? model.MorphWeightOffsets[node] : 0;
            if (morphOffset < 0) continue;
            AnimationSampling.Sample(item, time, ref cursors[channel], value);
            bool first = scratch.Touch(property);
            switch (item.Path)
            {
                case AnimationPath.Translation:
                    scratch.Vectors[property] = (first ? Vector3.Zero : scratch.Vectors[property]) + (new Vector3(value[0], value[1], value[2]) * weight);
                    break;
                case AnimationPath.Scale:
                    scratch.Vectors[property] = (first ? Vector3.Zero : scratch.Vectors[property]) + (new Vector3(value[0], value[1], value[2]) * weight);
                    break;
                case AnimationPath.Rotation:
                {
                    Quaternion q = new Quaternion(value[0], value[1], value[2], value[3]);
                    if (first) scratch.Rotations[node] = q * weight;
                    else
                    {
                        Quaternion sum = scratch.Rotations[node];
                        if (Quaternion.Dot(sum, q) < 0f) q = -q;
                        scratch.Rotations[node] = sum + (q * weight);
                    }

                    break;
                }

                default:
                {
                    int count = Math.Min(model.Nodes[node].DefaultMorphWeights.Length, item.ComponentCount);
                    Span<float> sums = scratch.Morph.AsSpan(morphOffset, count);
                    if (first) sums.Clear();
                    for (int target = 0; target < count; target++) sums[target] += value[target] * weight;
                    break;
                }
            }

            scratch.Weights[property] = (first ? 0f : scratch.Weights[property]) + weight;
        }
    }

    private void ResolveLayer(BlendScratch scratch)
    {
        NodeTransform[] locals = instance.LocalArray;
        for (int index = 0; index < scratch.TouchedCount; index++)
        {
            int property = scratch.Touched[index];
            int node = property / PathCount;
            AnimationPath path = (AnimationPath)(property % PathCount);
            float total = scratch.Weights[property];
            if (total <= 0f) continue;
            float layerWeight = Math.Min(total, 1f);
            bool reset = scratch.FirstThisEvaluation(property);
            ModelNode data = model.Nodes[node];
            switch (path)
            {
                case AnimationPath.Translation:
                case AnimationPath.Scale:
                {
                    Vector3 value = scratch.Vectors[property] / total;
                    ref Vector3 target = ref (path == AnimationPath.Translation ? ref locals[node].Translation : ref locals[node].Scale);
                    if (reset) target = path == AnimationPath.Translation ? data.DefaultTransform.Translation : data.DefaultTransform.Scale;
                    target = layerWeight >= 1f ? value : Vector3.Lerp(target, value, layerWeight);
                    break;
                }

                case AnimationPath.Rotation:
                {
                    Quaternion sum = scratch.Rotations[node];
                    float length = sum.Length();
                    Quaternion value = length > 1e-12f ? sum * (1f / length) : Quaternion.Identity;
                    if (reset) locals[node].Rotation = data.DefaultTransform.Rotation;
                    locals[node].Rotation = layerWeight >= 1f ? value : AnimationSampling.Nlerp(locals[node].Rotation, value, layerWeight);
                    break;
                }

                default:
                {
                    int offset = model.MorphWeightOffsets[node];
                    ReadOnlySpan<float> defaults = data.DefaultMorphWeights.Span;
                    Span<float> weights = instance.MorphWeightArray.AsSpan(offset, defaults.Length);
                    if (reset) defaults.CopyTo(weights);
                    ReadOnlySpan<float> sums = scratch.Morph.AsSpan(offset, defaults.Length);
                    float scale = 1f / total;
                    for (int target = 0; target < weights.Length; target++)
                    {
                        float value = sums[target] * scale;
                        weights[target] = layerWeight >= 1f ? value : weights[target] + ((value - weights[target]) * layerWeight);
                    }

                    break;
                }
            }
        }
    }

    /// <summary>Per-player accumulators for blending (allocated on the first blended evaluation).</summary>
    private sealed class BlendScratch
    {
        private readonly int[] layerStamps;
        private readonly int[] evaluationStamps;
        private int layerPass;
        private int evaluationPass;

        public BlendScratch(Model model)
        {
            int properties = model.Nodes.Count * PathCount;
            layerStamps = new int[properties];
            evaluationStamps = new int[properties];
            Weights = new float[properties];
            Vectors = new Vector3[properties];
            Rotations = new Quaternion[model.Nodes.Count];
            Morph = new float[model.MorphWeightCount];
            Touched = new int[properties];
        }

        /// <summary>The summed weight per property.</summary>
        public float[] Weights { get; }

        /// <summary>The weighted translation or scale sums (indexed by property).</summary>
        public Vector3[] Vectors { get; }

        /// <summary>The weighted rotation sums (indexed by node).</summary>
        public Quaternion[] Rotations { get; }

        /// <summary>The weighted morph-weight sums (laid out like the instance's weights).</summary>
        public float[] Morph { get; }

        /// <summary>The properties touched in the current layer.</summary>
        public int[] Touched { get; }

        /// <summary>How many entries of <see cref="Touched" /> are valid.</summary>
        public int TouchedCount { get; private set; }

        public void NextEvaluation()
        {
            if (++evaluationPass == int.MaxValue)
            {
                Array.Clear(evaluationStamps);
                evaluationPass = 1;
            }
        }

        public void NextLayer()
        {
            TouchedCount = 0;
            if (++layerPass == int.MaxValue)
            {
                Array.Clear(layerStamps);
                layerPass = 1;
            }
        }

        /// <summary>Marks a property as touched in this layer.</summary>
        /// <returns>True the first time in this layer (its sums start from zero).</returns>
        public bool Touch(int property)
        {
            if (layerStamps[property] == layerPass) return false;
            layerStamps[property] = layerPass;
            Touched[TouchedCount++] = property;
            return true;
        }

        /// <summary>True the first time a property is resolved in this evaluation (it starts from the rest pose).</summary>
        public bool FirstThisEvaluation(int property)
        {
            if (evaluationStamps[property] == evaluationPass) return false;
            evaluationStamps[property] = evaluationPass;
            return true;
        }
    }
}
