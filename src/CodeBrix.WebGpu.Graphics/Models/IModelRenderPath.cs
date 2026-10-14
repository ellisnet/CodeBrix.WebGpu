using System;
using System.Collections.Generic;

namespace CodeBrix.WebGpu.Graphics.Models;

/// <summary>
/// How a <see cref="ModelRenderer" /> turns batched instances into GPU work. The renderer groups instances by model;
/// a path owns the shading (materials, pipelines, per-instance data such as joint palettes or morph weights).
/// </summary>
internal interface IModelRenderPath : IDisposable
{
    /// <summary>The draw calls of the last <see cref="End" />.</summary>
    int LastDrawCallCount { get; }

    /// <summary>The primitive instances of the last <see cref="End" />.</summary>
    int LastInstanceCount { get; }

    /// <summary>Starts a batch in a pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="lighting">The lighting, or null.</param>
    void Begin(RenderPass pass, Camera camera, SceneLighting lighting);

    /// <summary>Draws every instance of one model.</summary>
    /// <param name="model">The model.</param>
    /// <param name="instances">Its instances (at least one).</param>
    void DrawModel(Model model, List<ModelInstance> instances);

    /// <summary>Records the batch.</summary>
    void End();

    /// <summary>Drops cached state for a model.</summary>
    /// <param name="model">The model.</param>
    void Forget(Model model);
}
