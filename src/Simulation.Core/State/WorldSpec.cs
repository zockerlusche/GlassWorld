namespace GlassWorld.Simulation.State;

/// <summary>
/// A1768 (B2805): the world specification - every value the player set in the new-world form, kept with the world and
/// in its save game, so a world can be told apart and made again from it. What acts on the world is carried on its own
/// (the climate in <see cref="SimulationHostOptions.Climate"/>, the island in the save); this record is the form as it
/// was filled in. Further controls of B2805 (resources, terrain, start season ...) add their fields here. Not part of the
/// state hash. Null in save games from before.
/// </summary>
/// <param name="Size">Size option id (content/player/world_options.json); null = not chosen.</param>
/// <param name="GridCells">The island's grid of that size, cells per side; null = the preset's grid.</param>
/// <param name="Climate">Climate option id; null = the preset's climate.</param>
/// <param name="StartFavor">Start favour; null = the data's default.</param>
/// <param name="Resources">A1770: resources option id (poor/normal/rich); null = not chosen.</param>
/// <param name="ResourceFactor">A1770: that option's factor on spawn density and limits; null = 1.</param>
/// <param name="StartPairs">A1770: pairs of inhabitants the world started with; null = the preset's pairs.</param>
public sealed record WorldSpec(string? Size = null, int? GridCells = null, string? Climate = null, int? StartFavor = null, string? Resources = null, double? ResourceFactor = null, int? StartPairs = null);
