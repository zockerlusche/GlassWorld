using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>A dug cell of the map (save game): how deep its hole goes.</summary>
/// <param name="X">Cell x.</param>
/// <param name="Z">Cell z.</param>
/// <param name="DepthCm">Depth dug so far, centimetres.</param>
/// <param name="SinceTick">A1460: the last dig or overgrowing step there (it grows over from then); null in older saves = the load tick.</param>
public sealed record DugCell(int X, int Z, int DepthCm, long? SinceTick = null);

/// <summary>
/// Digging (A1578, B4b K5, R33, D711 section 6; design GW1 #27049): an inhabitant digs where it stands; each dig deepens the
/// hole of that map cell by the data's step and brings up the material of the layer at that depth as a thing - the top
/// layer is the cell's soil, deeper ones come from the data, ore only from a layer that allows it and by the cell's ore
/// share. The first dig makes the pit; at the groundwater it becomes a well and nothing goes deeper. Bare hands dig only
/// layers the data marks by_hand; a held thing with the tool property digs any (no_tool otherwise). The height map does
/// not change (that is A1460). The digs of a tick are applied after the inhabitants, in the same tick.
/// </summary>
public sealed class DiggingSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>Event: an inhabitant dug (target = the pit, detail = the material brought up, if any).</summary>
    public const string Dug = "dug";

    /// <summary>Event: digging gave nothing (detail = why).</summary>
    public const string DigFailed = "dig_failed";

    /// <summary>Event: the hole reached the groundwater (target = the well).</summary>
    public const string WellDug = "well_dug";

    /// <summary>Detail of <see cref="DigFailed"/>: the ground here is too hard for bare hands.</summary>
    public const string NoTool = "no_tool";

    /// <summary>Detail of <see cref="DigFailed"/>: the hole is at the groundwater - no deeper.</summary>
    public const string WaterReached = "water_reached";

    /// <summary>Detail of <see cref="DigFailed"/>: nothing to dig here (off the map, no underground, in the water).</summary>
    public const string NoGround = "no_ground";

    private readonly DiggingSettings _data;
    private readonly WorldObjects _objects;
    private readonly WorldMapSystem _map;
    private readonly int _heightUnitMm;
    private readonly ulong _seed;
    private readonly Func<string, (Position At, string? HeldType)?> _digger;
    private readonly Action<WorldEvent> _report;
    private readonly SortedDictionary<(int X, int Z), int> _depth = [];
    private readonly SortedDictionary<(int X, int Z), long> _since = [];
    private readonly List<string> _pending = [];
    private readonly List<WorldObject> _near = [];

    /// <summary>Creates the system.</summary>
    /// <param name="data">Digging data (content/world/digging.json).</param>
    /// <param name="objects">The world's objects (pits, wells, materials).</param>
    /// <param name="map">The island with its underground.</param>
    /// <param name="heightUnitMm">Millimetres of one height unit (groundwater depth is given in them).</param>
    /// <param name="seed">World seed (ore rolls).</param>
    /// <param name="digger">Where an inhabitant stands and the type it holds (null when unknown).</param>
    /// <param name="report">Where events go (the inhabitants' report).</param>
    public DiggingSystem(DiggingSettings data, WorldObjects objects, WorldMapSystem map, int heightUnitMm, ulong seed, Func<string, (Position At, string? HeldType)?> digger, Action<WorldEvent> report)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _heightUnitMm = heightUnitMm;
        _seed = seed;
        _digger = digger ?? throw new ArgumentNullException(nameof(digger));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <inheritdoc/>
    public string Name => "digging";

    /// <inheritdoc/>
    public bool HasState => _depth.Count > 0;

    /// <summary>A1460: game ticks per hour (the overgrowing is looked at on the full hour; set by the host).</summary>
    public int TicksPerHour { get; set; } = 60;

    /// <summary>How deep the hole of a cell goes, centimetres (0 = untouched).</summary>
    public int DepthAt(int x, int z) => _depth.GetValueOrDefault((x, z));

    /// <summary>An inhabitant event: the end of the dig action is applied in this tick's step of the system.</summary>
    public void Observe(WorldEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind == InhabitantsSystem.ActionFinished && string.Equals(e.Action, _data.Action, StringComparison.Ordinal))
        {
            _pending.Add(e.InhabitantId);
        }
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var who in _pending)
        {
            Dig(world, who);
        }

        _pending.Clear();
        Overgrow(world);
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_depth.Count);
        foreach (var ((x, z), depth) in _depth)
        {
            hasher.Write(x).Write(z).Write(depth).Write(_since.GetValueOrDefault((x, z), -1));
        }
    }

    /// <summary>The dug cells, for a save game.</summary>
    public IReadOnlyList<DugCell> Capture() => [.. _depth.Select(d => new DugCell(d.Key.X, d.Key.Z, d.Value, _since.TryGetValue(d.Key, out var since) ? since : null))];

    /// <summary>Restores the dug cells (pits and wells are objects and come back with them).</summary>
    public void Restore(IReadOnlyList<DugCell>? cells)
    {
        _depth.Clear();
        _since.Clear();
        foreach (var cell in cells ?? [])
        {
            _depth[(cell.X, cell.Z)] = cell.DepthCm;
            if (cell.SinceTick is { } since)
            {
                _since[(cell.X, cell.Z)] = since;
            }
        }
    }

    private void Dig(World world, string who)
    {
        if (_digger(who) is not { } digger || _map.CellOf(digger.At) is not { } cell || _map.Map.Underground is not { } underground
            || _map.Map.Cell(cell.X, cell.Z).Water != WaterKind.None)
        {
            _report(new WorldEvent(DigFailed, world.Tick, who, _data.Action, null) { Detail = NoGround });
            return;
        }

        var ground = underground.Cell(cell.X, cell.Z);
        var water = ground.GroundwaterDepth * _heightUnitMm / 10;
        var depth = _depth.GetValueOrDefault(cell);
        var pit = PitAt(cell);
        var at = _map.CentreOf(cell.X, cell.Z);
        if (depth >= water)
        {
            if (pit is null)
            {
                // Wet ground: the groundwater lies at the surface - the first dig is a waterhole at once (review GW4 #506).
                _depth[cell] = depth;
                _since[cell] = world.Tick;
                var wet = _objects.Spawn(_data.WellType, at);
                _report(new WorldEvent(WellDug, world.Tick, who, _data.Action, wet.Id) { TargetType = wet.TypeId });
                return;
            }

            _report(new WorldEvent(DigFailed, world.Tick, who, _data.Action, pit.Id) { Detail = WaterReached });
            return;
        }

        var layer = _data.Layers.FirstOrDefault(l => depth < l.UntilCm) ?? _data.Layers.Last();
        var tool = digger.HeldType is { } held && _objects.Catalog.Contains(held) && _objects.Catalog.HasProperty(held, _data.ToolProperty);
        var soil = underground.SoilAt(cell.X, cell.Z);
        if (!tool && (layer.ByHand != true || layer.NotByHandOn?.Contains(soil, StringComparer.Ordinal) == true))
        {
            _report(new WorldEvent(DigFailed, world.Tick, who, _data.Action, pit?.Id) { Detail = NoTool });
            return;
        }

        var deeper = Math.Min(depth + _data.StepCm, water);
        _depth[cell] = deeper;
        _since[cell] = world.Tick;
        pit ??= _objects.Spawn(_data.PitType, at);

        // What comes up lies beside the hole.
        var material = Material(layer, soil, ground.OrePermille, cell, depth);
        if (material is not null)
        {
            _objects.Spawn(material, new Position(at.X + 0.4, at.Z));
        }

        _report(new WorldEvent(Dug, world.Tick, who, _data.Action, pit.Id) { TargetType = pit.TypeId, Detail = material });
        if (deeper >= water)
        {
            // Water stands in it: a well, and no deeper.
            _objects.Remove(pit.Id);
            var well = _objects.Spawn(_data.WellType, at);
            _report(new WorldEvent(WellDug, world.Tick, who, _data.Action, well.Id) { TargetType = well.TypeId });
        }
    }

    /// <summary>
    /// A1460 Z1 (R49, D1248): on the full hour every hole grows over a little - once its biome's hours of growth have passed
    /// since the last dig or step (growing like the data's type: its group's seasons, else by the clock), it gets one
    /// step shallower; at depth 0 the pit is gone and the cell untouched. Holes of the data's kept types (a well) stay; a
    /// biome with 0 hours never grows over. A cell without a time yet (an older save) starts at this hour.
    /// </summary>
    private void Overgrow(World world)
    {
        if (_data.Overgrow is not { } overgrow || _depth.Count == 0 || world.Tick % Math.Max(1, TicksPerHour) != 0)
        {
            return;
        }

        foreach (var cell in _depth.Keys.ToList())
        {
            if (!_since.TryGetValue(cell, out var since))
            {
                _since[cell] = world.Tick;
                continue;
            }

            var pit = PitAt(cell);
            if (pit is not null && overgrow.Keeps?.Contains(pit.TypeId) == true)
            {
                continue;
            }

            var biome = _map.Map.BiomeIds[_map.Map.Cell(cell.X, cell.Z).Biome];
            var hours = overgrow.HoursPerStep is { } byBiome && byBiome.TryGetValue(biome, out var h) ? h : overgrow.DefaultHoursPerStep;
            if (hours <= 0)
            {
                continue;
            }

            var ticks = (long)hours * TicksPerHour;
            var grown = overgrow.GrowsLike is { } like && _map.Grown is { } growth ? growth(like, since, world.Tick, ticks) : world.Tick - since >= ticks;
            if (!grown)
            {
                continue;
            }

            var shallower = _depth[cell] - _data.StepCm;
            if (shallower > 0)
            {
                _depth[cell] = shallower;
                _since[cell] = world.Tick;
                continue;
            }

            _depth.Remove(cell);
            _since.Remove(cell);
            if (pit is not null)
            {
                _objects.Remove(pit.Id);
            }
        }
    }

    /// <summary>The material of a layer at this cell: its soil where the data names one, ore by the cell's share, else the layer's.</summary>
    private string? Material(DigLayer layer, string soil, int orePermille, (int X, int Z) cell, int depth)
    {
        if (layer.OrePossible == true && _data.Ore is { } ore && orePermille > 0
            && (int)(Roll(cell, depth) % 1000) < orePermille)
        {
            return ore.Type;
        }

        return layer.BySoil is { } bySoil && bySoil.TryGetValue(soil, out var fromSoil) ? fromSoil : layer.Material;
    }

    /// <summary>The pit (or well) of a cell: the thing of that type at the cell's centre, or null.</summary>
    private WorldObject? PitAt((int X, int Z) cell)
    {
        // Only the things at the cell's centre, not every thing of the world (review GW1 #506).
        var at = _map.CentreOf(cell.X, cell.Z);
        _near.Clear();
        _objects.Within(at, 0.05, _near);
        return _near.FirstOrDefault(o => o.TypeId == _data.PitType || o.TypeId == _data.WellType);
    }

    private ulong Roll((int X, int Z) cell, int depth) =>
        BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_seed}/digging/{cell.X}/{cell.Z}/{depth}"))), 0);
}
