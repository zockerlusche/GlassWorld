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
    /// <param name="faces">The data's mood faces.</param>
    /// <param name="moods">The inhabitant's moods now.</param>
    /// <param name="shown">The face shown until now, or null: its entry holds down to its shows_from less <paramref name="hold"/>.</param>
    /// <param name="hold">The margin a shown face keeps (review GW5 #547: no flicker at the threshold).</param>
    public static (string Face, double Weight)? For(IEnumerable<GlassWorld.Contracts.FigureMoodFace>? faces, IReadOnlyDictionary<string, double> moods, (string Face, double Weight)? shown = null, double hold = 0)
    {
        ArgumentNullException.ThrowIfNull(moods);
        (string Face, double Weight)? best = null;
        var lead = 0.0;
        foreach (var face in faces ?? [])
        {
            var name = face.Face.ToString().ToLowerInvariant();
            var from = shown is { } was && was.Face == name ? face.ShowsFrom - Math.Max(0, hold) : face.ShowsFrom;
            if (moods.TryGetValue(face.Mood, out var value) && value >= from && (best is null || value - face.ShowsFrom > lead))
            {
                best = (name, face.Weight);
                lead = value - face.ShowsFrom;
            }
        }

        return best;
    }
}
