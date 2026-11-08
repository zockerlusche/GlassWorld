using System.Security.Cryptography;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>
/// Checks the base sound library (B8-4): every origin carries an approved licence, every file exists
/// with the recorded SHA-256, has the loudness of its kind (files capped by the peak limit may be
/// quieter) and stays within the length limit. Shared by the unit tests and the game smoke run (which
/// has the LFS files in CI).
/// </summary>
public static class BaseSoundCheck
{
    /// <summary>Licences that need no question (B2482); the schema allows only these.</summary>
    public static readonly IReadOnlySet<string> ApprovedLicences = new HashSet<string>(["CC0-1.0", "MIT", "Apache-2.0"], StringComparer.Ordinal);

    /// <summary>Problems of the library data alone (no files needed).</summary>
    public static IReadOnlyList<string> DataProblems(BaseSoundLibrary library, IEnumerable<string> mappedSoundIds)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(mappedSoundIds);
        var problems = new List<string>();
        var origins = library.Origins.ToDictionary(o => o.Id, StringComparer.Ordinal);
        foreach (var origin in library.Origins.Where(o => !ApprovedLicences.Contains(LicenceId(o.License))))
        {
            problems.Add($"origin {origin.Id}: licence {LicenceId(origin.License)} not approved");
        }

        var mapped = mappedSoundIds.ToHashSet(StringComparer.Ordinal);
        foreach (var sound in library.Sounds)
        {
            if (!origins.ContainsKey(sound.Origin))
            {
                problems.Add($"{sound.Id}: unknown origin {sound.Origin}");
            }

            foreach (var file in sound.Files.Where(f => f.Origin is not null && !origins.ContainsKey(f.Origin)))
            {
                problems.Add($"{file.File}: unknown origin {file.Origin}");
            }

            if (!mapped.Contains(sound.Id) && sound.SoundWords.Count == 0)
            {
                problems.Add($"{sound.Id}: neither used by the sound map nor reachable by keywords");
            }
        }

        foreach (var id in library.Sounds.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            problems.Add($"{id}: listed twice");
        }

        return problems;
    }

    /// <summary>Problems of the files; <paramref name="read"/> returns the bytes of a library file name.</summary>
    public static IReadOnlyList<string> FileProblems(BaseSoundLibrary library, Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(read);
        var problems = new List<string>();
        var target = library.Target;
        foreach (var sound in library.Sounds)
        {
            var loop = sound.Kind == SoundKind.Loop;
            var wanted = loop ? target.LoopDbfs : target.OneShotDbfs;
            var maxSeconds = loop ? target.LoopMaxS : target.OneShotMaxS;
            foreach (var file in sound.Files)
            {
                var bytes = read(file.File);
                if (bytes is null)
                {
                    problems.Add($"{file.File}: missing");
                    continue;
                }

                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                {
                    problems.Add($"{file.File}: SHA-256 differs from library.json");
                }

                MonoSound wav;
                try
                {
                    wav = Pcm.ReadWav(bytes);
                }
                catch (InvalidDataException e)
                {
                    problems.Add($"{file.File}: {e.Message}");
                    continue;
                }

                var loudness = Pcm.LoudnessDbfs(wav, target.GateDbfs);
                var peak = Pcm.PeakDbfs(wav);
                var peakLimited = Math.Abs(peak - target.PeakMaxDbfs) <= 0.1;
                if (loudness > wanted + target.ToleranceDb || (loudness < wanted - target.ToleranceDb && !peakLimited))
                {
                    problems.Add($"{file.File}: loudness {loudness:0.0} dBFS, target {wanted:0.0} ± {target.ToleranceDb:0.0}");
                }

                if (peak > target.PeakMaxDbfs + 0.1)
                {
                    problems.Add($"{file.File}: peak {peak:0.0} dBFS above {target.PeakMaxDbfs:0.0}");
                }

                if (wav.Seconds > maxSeconds + 0.01)
                {
                    problems.Add($"{file.File}: {wav.Seconds:0.00} s longer than {maxSeconds:0.##} s");
                }
            }
        }

        return problems;
    }

    /// <summary>True if the bytes are a Git LFS pointer instead of the real file (CI jobs without LFS).</summary>
    public static bool IsLfsPointer(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length < 200 && System.Text.Encoding.ASCII.GetString(bytes).StartsWith("version https://git-lfs", StringComparison.Ordinal);
    }

    private static string LicenceId(LibraryOriginLicense licence) => licence switch
    {
        LibraryOriginLicense.CC01_0 => "CC0-1.0",
        LibraryOriginLicense.MIT => "MIT",
        LibraryOriginLicense.Apache2_0 => "Apache-2.0",
        _ => licence.ToString(),
    };
}
