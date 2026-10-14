using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One clip playing on an <see cref="AnimationPlayer" />: its playhead, speed, loop mode, blend weight (with an
/// optional fade) and layer, plus "what happened in the last update" flags. Returned by
/// <see cref="AnimationPlayer.Play(int, AnimationLoopMode, float, float, int)" />,
/// <see cref="AnimationPlayer.CrossFade(int, float, AnimationLoopMode, float, float, int)" /> and
/// <see cref="AnimationPlayer.Blend(int, float, AnimationLoopMode, float, float, int)" />.
/// </summary>
/// <remarks>
/// The player owns a small pool of states and reuses one after it stops (<see cref="IsActive" /> false), so keep a
/// reference only while the clip plays. Changing properties allocates nothing.
/// </remarks>
public sealed class AnimationState
{
    private float phase;
    private float playhead;
    private float fadeRate;
    private float fadeTarget;
    private bool stopWhenFaded;

    internal AnimationState(AnimationPlayer player, int channelCapacity)
    {
        Player = player;
        Cursors = new int[Math.Max(1, channelCapacity)];
    }

    /// <summary>The player.</summary>
    public AnimationPlayer Player { get; }

    /// <summary>The clip (null once the state is no longer active).</summary>
    public ModelAnimation Clip { get; private set; }

    /// <summary>The clip's index in <see cref="Model.Animations" />, or -1 when inactive.</summary>
    public int ClipIndex => Clip?.Index ?? -1;

    /// <summary>The clip's name, or null.</summary>
    public string Name => Clip?.Name;

    /// <summary>The clip's length in seconds.</summary>
    public float Duration => Clip?.Duration ?? 0f;

    /// <summary>True while the state belongs to its player (false after it stopped or faded out).</summary>
    public bool IsActive => Clip != null;

    /// <summary>How playback continues at the clip's end (<see cref="AnimationLoopMode.Once" /> holds the last frame).</summary>
    public AnimationLoopMode LoopMode { get; set; }

    /// <summary>The playback rate (1 = normal, 0.5 = half speed, negative = backwards). Multiplied by <see cref="AnimationPlayer.Speed" />.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>True to hold the playhead (the clip still contributes to the pose).</summary>
    public bool Paused { get; set; }

    /// <summary>The blend layer: higher layers override lower ones (by their weight) on the properties they animate.</summary>
    public int Layer { get; private set; }

    /// <summary>
    /// The blend weight (0..1 contributes that share; states of one layer whose weights add up past 1 are normalised).
    /// Setting it cancels a running fade.
    /// </summary>
    public float Weight
    {
        get => WeightValue;
        set
        {
            WeightValue = Math.Max(0f, value);
            fadeRate = 0f;
            stopWhenFaded = false;
        }
    }

    /// <summary>True while a fade (<see cref="FadeTo" />, a cross-fade) changes <see cref="Weight" />.</summary>
    public bool IsFading => fadeRate != 0f;

    /// <summary>
    /// The playhead in clip seconds, 0..<see cref="Duration" /> (ping-pong reports the folded position). Setting it
    /// seeks: values outside the clip wrap (loop, ping-pong) or clamp (once); a once-clip seeked before its end plays
    /// again.
    /// </summary>
    public float Time
    {
        get => playhead;
        set => Seek(value);
    }

    /// <summary>The playhead as a fraction of <see cref="Duration" /> (0..1; 0 for an empty clip). Setting it seeks.</summary>
    public float NormalizedTime
    {
        get => Duration > 0f ? playhead / Duration : 0f;
        set => Seek(value * Duration);
    }

    /// <summary>Completed cycles (loop: wraps; ping-pong: full forward-and-back trips).</summary>
    public int LoopCount { get; private set; }

    /// <summary>True when a <see cref="AnimationLoopMode.Once" /> clip has reached its end (it holds the last frame).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>True when the last <see cref="AnimationPlayer.Update" /> finished the clip (once mode).</summary>
    public bool FinishedThisUpdate { get; private set; }

    /// <summary>True when the last <see cref="AnimationPlayer.Update" /> wrapped the clip (loop and ping-pong modes).</summary>
    public bool LoopedThisUpdate { get; private set; }

    /// <summary>The weight without the fade bookkeeping.</summary>
    internal float WeightValue { get; private set; }

    /// <summary>The key cursor of each channel of the clip.</summary>
    internal int[] Cursors { get; }

    /// <summary>True when the state should be removed once its fade reaches zero.</summary>
    internal bool StopsWhenFaded => stopWhenFaded;

    /// <summary>Fades <see cref="Weight" /> linearly to a target.</summary>
    /// <param name="weight">The target weight.</param>
    /// <param name="duration">Seconds (player time); 0 or less sets the weight at once.</param>
    /// <param name="stopWhenZero">True to stop the state when it reaches weight 0.</param>
    public void FadeTo(float weight, float duration, bool stopWhenZero = false)
    {
        weight = Math.Max(0f, weight);
        if (duration <= 0f || weight == WeightValue)
        {
            WeightValue = weight;
            fadeRate = 0f;
            stopWhenFaded = stopWhenZero && weight == 0f;
            if (stopWhenFaded && Clip != null) Player.Stop(this);
            return;
        }

        fadeTarget = weight;
        fadeRate = (weight - WeightValue) / duration;
        stopWhenFaded = stopWhenZero;
    }

    /// <summary>Stops the clip now (its animated nodes return to the rest pose unless other states animate them).</summary>
    public void Stop()
    {
        if (Clip != null) Player.Stop(this);
    }

    /// <summary>The clip, name and playhead.</summary>
    /// <returns>For example <c>AnimationState 'walk' 0.25/0.667 s weight 1</c>.</returns>
    public override string ToString() => Clip == null
        ? "AnimationState (inactive)"
        : "AnimationState '" + (Clip.Name ?? Clip.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "' "
          + playhead.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "/" + Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
          + " s weight " + WeightValue.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Starts playing a clip.</summary>
    internal void Start(ModelAnimation clip, AnimationLoopMode loopMode, float speed, float startTime, float weight, int layer)
    {
        Clip = clip;
        LoopMode = loopMode;
        Speed = speed;
        Paused = false;
        Layer = layer;
        WeightValue = Math.Max(0f, weight);
        fadeRate = 0f;
        stopWhenFaded = false;
        LoopCount = 0;
        FinishedThisUpdate = false;
        LoopedThisUpdate = false;
        Array.Clear(Cursors);
        Seek(startTime);
    }

    /// <summary>Marks the state inactive (back in the pool).</summary>
    internal void Release()
    {
        Clip = null;
        fadeRate = 0f;
        stopWhenFaded = false;
    }

    /// <summary>Clears the per-update flags.</summary>
    internal void ClearFlags()
    {
        FinishedThisUpdate = false;
        LoopedThisUpdate = false;
    }

    /// <summary>Moves the playhead by clip seconds (already scaled by both speeds).</summary>
    internal void Advance(float seconds)
    {
        if (Paused || seconds == 0f || float.IsNaN(seconds)) return;
        float duration = Duration;
        if (duration <= 0f)
        {
            phase = 0f;
            playhead = 0f;
            if (LoopMode == AnimationLoopMode.Once && !IsFinished)
            {
                IsFinished = true;
                FinishedThisUpdate = true;
            }

            return;
        }

        switch (LoopMode)
        {
            case AnimationLoopMode.Once:
                if (IsFinished) return;
                phase += seconds;
                if (phase >= duration || (seconds < 0f && phase <= 0f))
                {
                    phase = phase >= duration ? duration : 0f;
                    IsFinished = true;
                    FinishedThisUpdate = true;
                }

                playhead = phase;
                return;

            case AnimationLoopMode.PingPong:
            {
                float period = 2f * duration;
                phase += seconds;
                if (phase >= period || phase < 0f)
                {
                    LoopCount += Wrap(ref phase, period);
                    LoopedThisUpdate = true;
                }

                playhead = phase > duration ? period - phase : phase;
                return;
            }

            default:
                phase += seconds;
                if (phase >= duration || phase < 0f)
                {
                    LoopCount += Wrap(ref phase, duration);
                    LoopedThisUpdate = true;
                }

                playhead = phase;
                return;
        }
    }

    /// <summary>Moves the weight along its fade.</summary>
    /// <param name="seconds">Player seconds.</param>
    /// <returns>True when the state faded out and should stop.</returns>
    internal bool AdvanceFade(float seconds)
    {
        if (fadeRate == 0f || seconds <= 0f) return false;
        float next = WeightValue + (fadeRate * seconds);
        if ((fadeRate > 0f && next >= fadeTarget) || (fadeRate < 0f && next <= fadeTarget))
        {
            WeightValue = fadeTarget;
            fadeRate = 0f;
            return stopWhenFaded && fadeTarget == 0f;
        }

        WeightValue = next;
        return false;
    }

    private void Seek(float time)
    {
        float duration = Duration;
        IsFinished = false;
        if (duration <= 0f || float.IsNaN(time))
        {
            phase = 0f;
            playhead = 0f;
            return;
        }

        switch (LoopMode)
        {
            case AnimationLoopMode.Once:
                phase = Math.Clamp(time, 0f, duration);
                IsFinished = time >= duration;
                playhead = phase;
                return;
            case AnimationLoopMode.PingPong:
            {
                float period = 2f * duration;
                phase = time;
                if (phase >= period || phase < 0f) Wrap(ref phase, period);
                playhead = phase > duration ? period - phase : phase;
                return;
            }

            default:
                phase = time;
                if (phase >= duration || phase < 0f) Wrap(ref phase, duration);
                playhead = phase;
                return;
        }
    }

    /// <summary>Wraps a value into [0, period) and returns how many periods were crossed.</summary>
    private static int Wrap(ref float value, float period)
    {
        float cycles = MathF.Floor(value / period);
        value -= cycles * period;
        if (value >= period || value < 0f) value = 0f;
        return (int)Math.Min(Math.Abs(cycles), int.MaxValue);
    }
}
