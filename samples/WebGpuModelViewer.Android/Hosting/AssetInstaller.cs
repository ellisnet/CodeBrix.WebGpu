using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Content.Res;

namespace WebGpuModelViewer.AndroidHosting;

/// <summary>
/// Copies the models, textures and licence files from the APK's assets into the app's files folder, where the model
/// loader can read them as ordinary files. A file is copied only when it is missing or its size differs.
/// </summary>
internal static class AssetInstaller
{
    /// <summary>The asset folders the viewer uses.</summary>
    private static readonly string[] Packs = ["crystals-and-avian", "cube-pets", "survival-kit"];

    /// <summary>Copies the assets.</summary>
    /// <param name="context">The activity.</param>
    /// <param name="copied">How many files were copied this time.</param>
    /// <param name="total">How many asset files there are.</param>
    /// <returns>The folder holding the copies (the viewer's Assets folder).</returns>
    public static string Install(Context context, out int copied, out int total)
    {
        string root = Path.Combine(context.FilesDir.AbsolutePath, "Assets");
        AssetManager assets = context.Assets;
        List<string> files = new List<string>();
        foreach (string pack in Packs) Collect(assets, pack, files);

        copied = 0;
        total = files.Count;
        foreach (string file in files)
        {
            string destination = Path.Combine(root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            using MemoryStream buffer = new MemoryStream();
            using (Stream source = assets.Open(file)) source.CopyTo(buffer);
            FileInfo existing = new FileInfo(destination);
            if (existing.Exists && existing.Length == buffer.Length) continue;
            File.WriteAllBytes(destination, buffer.ToArray());
            copied++;
        }

        return root;
    }

    private static void Collect(AssetManager assets, string path, List<string> files)
    {
        string[] children = assets.List(path);
        if (children == null || children.Length == 0)
        {
            files.Add(path);
            return;
        }

        foreach (string child in children) Collect(assets, path + "/" + child, files);
    }
}
