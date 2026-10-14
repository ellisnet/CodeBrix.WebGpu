using System;
using System.IO;
using CodeBrix.WebGpu.Graphics.Models;
using Schema = CodeBrix.Graphics3D.Gltf2.Schema2;
using ValidationMode = CodeBrix.Graphics3D.Gltf2.Validation.ValidationMode;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>glTF 2.0 model loading (read by CodeBrix.Graphics3D.Gltf2) and the model renderer.</summary>
public sealed partial class GraphicsContext
{
    /// <summary>
    /// Loads a glTF 2.0 model (.glb, or .gltf with its external .bin and image files) onto the GPU: geometry into one
    /// shared vertex and index buffer, materials, textures (decoded by CodeBrix.Imaging, mipmapped on the GPU),
    /// samplers, node hierarchy, skins, morph targets and animation clips. See <see cref="Model" />.
    /// </summary>
    /// <param name="path">The .glb or .gltf file.</param>
    /// <param name="options">The options (null for the defaults).</param>
    /// <returns>The model.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is not a glTF 2.0 model the reader accepts (the message says why: a parse or validation error, a missing .bin, a required extension that is not supported ...).</exception>
    /// <exception cref="GraphicsException">WebGPU rejected a buffer or texture.</exception>
    public Model LoadModel(string path, ModelLoadOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        if (!File.Exists(path)) throw new FileNotFoundException("The model file does not exist.", path);
        options ??= new ModelLoadOptions();
        string fullPath = Path.GetFullPath(path);
        Schema.ModelRoot root = Read(validation => Schema.ModelRoot.Load(fullPath, new Schema.ReadSettings { Validation = validation }), options.Validation, "'" + path + "'", out string validationWarning);
        return GltfModelLoader.Load(this, root, Path.GetFileNameWithoutExtension(fullPath), fullPath, Path.GetDirectoryName(fullPath), options, validationWarning);
    }

    /// <summary>
    /// Loads a glTF 2.0 model from a stream (a .glb, or a .gltf whose external files resolve against
    /// <paramref name="baseDirectory" />). See <see cref="LoadModel(string, ModelLoadOptions)" />.
    /// </summary>
    /// <param name="stream">The stream, positioned at the model.</param>
    /// <param name="baseDirectory">The folder external .bin and image files are read from, or null when the model has none.</param>
    /// <param name="options">The options (null for the defaults).</param>
    /// <returns>The model.</returns>
    /// <exception cref="InvalidDataException">The stream is not a glTF 2.0 model the reader accepts.</exception>
    public Model LoadModel(Stream stream, string baseDirectory = null, ModelLoadOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        options ??= new ModelLoadOptions();
        MemoryStream copy = new MemoryStream();
        stream.CopyTo(copy);
        Schema.ModelRoot root = Read(
            validation =>
            {
                copy.Position = 0;
                Schema.ReadContext context = baseDirectory != null
                    ? Schema.ReadContext.CreateFromDirectory(new DirectoryInfo(baseDirectory))
                    : Schema.ReadContext.Create(asset => throw new FileNotFoundException("The model refers to the external file '" + asset + "', but no base directory was given."));
                return context.WithSettingsFrom(new Schema.ReadSettings { Validation = validation }).ReadSchema2(copy);
            },
            options.Validation,
            "stream",
            out string validationWarning);

        string name = options.Label ?? "model";
        return GltfModelLoader.Load(this, root, name, null, baseDirectory == null ? null : Path.GetFullPath(baseDirectory), options, validationWarning);
    }

    /// <summary>Creates a renderer that draws <see cref="ModelInstance" />s.</summary>
    /// <returns>The renderer.</returns>
    public ModelRenderer CreateModelRenderer() => new ModelRenderer(this);

    /// <summary>
    /// Reads a document with the requested validation. Under <see cref="ModelValidation.TryFix" /> a document that
    /// fails validation is read once more without validation (many exporters write small spec violations, such as a
    /// scene root that is another node's child); the validation message becomes a model warning.
    /// </summary>
    private static Schema.ModelRoot Read(Func<ValidationMode, Schema.ModelRoot> read, ModelValidation validation, string what, out string validationWarning)
    {
        validationWarning = null;
        try
        {
            return read(Map(validation));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (validation == ModelValidation.TryFix && exception is CodeBrix.Graphics3D.Gltf2.Validation.ModelException)
            {
                try
                {
                    Schema.ModelRoot root = read(ValidationMode.Skip);
                    validationWarning = "The file failed glTF validation and was read without it: " + exception.Message;
                    return root;
                }
                catch (Exception retry) when (retry is not OutOfMemoryException)
                {
                    throw new InvalidDataException("The glTF model " + what + " could not be read: " + exception.Message + " (reading it without validation failed too: " + retry.Message + ")", exception);
                }
            }

            throw new InvalidDataException("The glTF model " + what + " could not be read: " + exception.Message, exception);
        }
    }

    private static ValidationMode Map(ModelValidation validation) => validation switch
    {
        ModelValidation.Strict => ValidationMode.Strict,
        ModelValidation.Skip => ValidationMode.Skip,
        _ => ValidationMode.TryFix,
    };
}
