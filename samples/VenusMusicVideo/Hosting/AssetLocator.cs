using System;
using System.IO;

namespace VenusMusicVideo.Hosting;

/// <summary>
/// Finds the sample's files. Its own assets (the song and <c>flight.json</c>) are read from the SOURCE folder
/// <c>samples/VenusMusicVideo/assets</c>, not a build copy, so editing the flight reloads it live. The planet data
/// lives in <c>samples/VenusMusicVideo/downloaded</c> (created when missing, git-ignored): the app downloads and
/// builds it there on the first run (see <see cref="Maps.MapPreparation" />).
/// </summary>
internal sealed class AssetLocator
{
    private AssetLocator(string sampleFolder)
    {
        SampleFolder = sampleFolder;
        AssetFolder = Path.Combine(sampleFolder, "assets");
        DownloadFolder = Path.Combine(sampleFolder, "downloaded");
    }

    /// <summary>samples/VenusMusicVideo.</summary>
    public string SampleFolder { get; }

    /// <summary>samples/VenusMusicVideo/assets.</summary>
    public string AssetFolder { get; }

    /// <summary>samples/VenusMusicVideo/downloaded (git-ignored).</summary>
    public string DownloadFolder { get; }

    /// <summary>The song.</summary>
    public string SongPath => Path.Combine(AssetFolder, "music.mp3");

    /// <summary>The flight choreography.</summary>
    public string FlightPath => Path.Combine(AssetFolder, "flight.json");

    /// <summary>Where videos and stills are written (git-ignored).</summary>
    public string OutputFolder => Path.Combine(SampleFolder, "Output");

    /// <summary>Finds the sample folder (the one holding VenusMusicVideo.csproj) from the working folder or the program's folder.</summary>
    /// <returns>The locator.</returns>
    public static AssetLocator Find()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo folder = new DirectoryInfo(start);
            while (folder != null)
            {
                if (File.Exists(Path.Combine(folder.FullName, "VenusMusicVideo.csproj"))) return new AssetLocator(folder.FullName);
                string nested = Path.Combine(folder.FullName, "samples", "VenusMusicVideo");
                if (File.Exists(Path.Combine(nested, "VenusMusicVideo.csproj"))) return new AssetLocator(nested);
                folder = folder.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find samples/VenusMusicVideo (VenusMusicVideo.csproj) above " + Environment.CurrentDirectory + " or " + AppContext.BaseDirectory + ".");
    }
}
