using System.Security.Cryptography;
using System.Text;

namespace GlassWorld.Simulation.Data;

/// <summary>
/// Fingerprint of a content folder, stored with save games (<see cref="SaveGameStore.ContentFingerprintKey"/>)
/// so a replay can tell changed data from lost determinism: SHA-256 over every file, in ordinal order of
/// the relative path (with <c>/</c>), each as path, NUL, length, bytes. Raw bytes: a checkout with other
/// line endings gives another fingerprint. Written as <c>sha256:</c> plus the first 16 hex digits.
/// </summary>
public static class ContentFingerprint
{
    /// <summary>Computes the fingerprint of <paramref name="contentDirectory"/>.</summary>
    public static string Compute(string contentDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentDirectory);
        var root = Path.GetFullPath(contentDirectory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (Relative: Path.GetRelativePath(root, f).Replace('\\', '/'), Full: f))
            .OrderBy(f => f.Relative, StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (relative, full) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            var bytes = File.ReadAllBytes(full);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, bytes.LongLength);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>The fingerprint, or null if the folder cannot be read (the save then simply has none).</summary>
    public static string? TryCompute(string contentDirectory)
    {
        try
        {
            return Compute(contentDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
