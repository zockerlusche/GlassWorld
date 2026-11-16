namespace GlassWorld.Presentation.World;

/// <summary>
/// A1802 (Marcel W2245): the face a figure rests in by its mood - the mood furthest above its threshold (shows_from) shows its mouth
/// shape at the data's weight (a hint, not a grimace). Presentation only: the core's moods are read, never written.
/// </summary>
public static class MoodFace
{
    /// <summary>
    /// The mouth shape and weight for these moods, or null (neutral): the entry whose mood lies furthest above its
    /// 'from' wins; ties go to the data's order.
    /// </summary>
    public static (string Face, double Weight)? For(IEnumerable<GlassWorld.Contracts.FigureMoodFace>? faces, IReadOnlyDictionary<string, double> moods)
    {
        ArgumentNullException.ThrowIfNull(moods);
        (string Face, double Weight)? best = null;
        var lead = 0.0;
        foreach (var face in faces ?? [])
        {
            if (moods.TryGetValue(face.Mood, out var value) && value >= face.ShowsFrom && (best is null || value - face.ShowsFrom > lead))
            {
                best = (face.Face.ToString().ToLowerInvariant(), face.Weight);
                lead = value - face.ShowsFrom;
            }
        }

        return best;
    }
}
