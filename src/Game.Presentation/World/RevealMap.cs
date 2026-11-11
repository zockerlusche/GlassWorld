namespace GlassWorld.Presentation.World;

/// <summary>
/// The reveal map of the cloud cover (B2-6 R25): one value per cell of the island, 0 = under cloud,
/// <see cref="Clear"/> = seen. It follows the player's view of the core by its version
/// (<c>WorldSnapshot.ExploredVersion</c>, <c>Exploration.RevealedSince</c>) and takes only the cells that
/// came after. Newly seen cells clear slowly over real time (<c>exploration.reveal_real_seconds</c> of the
/// preset); at most <c>cells_per_frame</c> of them start per frame, the rest wait their turn. The first
/// view it takes (a new world, a loaded one) is clear at once: land the player knows is never shown
/// clearing again. A cell never goes back under cloud. Open sea further than a margin from any land is
/// open from the start (<see cref="OpenSea"/>). Presentation only, no Godot: the view copies the values
/// into a texture (<see cref="CopyTo"/>) whenever <see cref="Advance"/> says they changed.
/// </summary>
public sealed class RevealMap
{
    /// <summary>The value of a cell that is fully clear.</summary>
    public const byte Clear = byte.MaxValue;

    private readonly byte[] _texels;
    private readonly bool[] _known;
    private readonly bool[] _open;
    private readonly Queue<int> _waiting = new();
    private readonly List<(int Cell, double Seconds)> _clearing = [];
    private readonly double _clearSeconds;
    private readonly int _cellsPerFrame;
    private readonly int _coverable;
    private int _seen;
    private bool _started;
    private bool _changed = true;

    /// <summary>
    /// Creates the map of an island of <paramref name="width"/> × <paramref name="depth"/> cells, all under
    /// cloud except the <paramref name="open"/> cells (index <c>z × width + x</c>; null = none).
    /// </summary>
    public RevealMap(int width, int depth, double clearSeconds, int cellsPerFrame, IReadOnlyList<bool>? open = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentOutOfRangeException.ThrowIfNegative(clearSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellsPerFrame);
        if (open is not null && open.Count != width * depth)
        {
            throw new ArgumentException($"The open cells are {open.Count}, the map has {width * depth}.", nameof(open));
        }

        (Width, Depth, _clearSeconds, _cellsPerFrame) = (width, depth, clearSeconds, cellsPerFrame);
        _texels = new byte[width * depth];
        _known = new bool[width * depth];
        _open = open is null ? new bool[width * depth] : [.. open];
        _coverable = _open.Count(o => !o);
        Reset();
    }

    /// <summary>Cells across.</summary>
    public int Width { get; }

    /// <summary>Cells deep.</summary>
    public int Depth { get; }

    /// <summary>The version of the player's view this map has taken (the number of cells the core had revealed).</summary>
    public int Version { get; private set; }

    /// <summary>Seen cells that wait for their turn to start clearing (the per-frame budget).</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Cells that are clearing right now.</summary>
    public int Clearing => _clearing.Count;

    /// <summary>Share of the cells that can lie under cloud (all but the open sea) the player has seen, 0..1.</summary>
    public double SeenShare => _coverable == 0 ? 1 : _seen / (double)_coverable;

    /// <summary>The value of the cell (x, z): 0 under cloud … <see cref="Clear"/>.</summary>
    public byte At(int x, int z) => _texels[Index(x, z)];

    /// <summary>Copies the values row by row (index <c>z × Width + x</c>), one byte per cell.</summary>
    public void CopyTo(Span<byte> target) => _texels.CopyTo(target);

    /// <summary>
    /// Takes what the core revealed since the version this map has: <paramref name="revealedSince"/> is
    /// asked once with <see cref="Version"/> when <paramref name="coreVersion"/> is another one. The first
    /// cells it ever takes are clear at once, later ones wait and then clear slowly. A smaller version than
    /// the map's means the core holds another state of the world (loaded in place): the map starts over
    /// with that state's whole view, again at once.
    /// </summary>
    public void Follow(int coreVersion, Func<int, int[]> revealedSince)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(coreVersion);
        ArgumentNullException.ThrowIfNull(revealedSince);
        if (coreVersion < Version)
        {
            Reset();
        }

        if (coreVersion == Version)
        {
            return;
        }

        var atOnce = !_started || _clearSeconds <= 0;
        foreach (var cell in revealedSince(Version))
        {
            if (cell < 0 || cell >= _texels.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(revealedSince), cell, $"Revealed cell outside the map of {Width} x {Depth} cells.");
            }

            // Open sea is clear anyway; a cell the map knows is never taken twice.
            if (_known[cell] || _open[cell])
            {
                continue;
            }

            Know(cell);
            if (atOnce)
            {
                _texels[cell] = Clear;
                _changed = true;
            }
            else
            {
                _waiting.Enqueue(cell);
            }
        }

        Version = coreVersion;
        _started = true;
    }

    /// <summary>
    /// One frame: the clearing cells go on by <paramref name="realSeconds"/> (0 while the game is paused),
    /// then at most <c>cells_per_frame</c> waiting cells start. Returns whether any value changed since the
    /// last call (also through <see cref="Follow"/> or <see cref="ShowShare"/>).
    /// </summary>
    public bool Advance(double realSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(realSeconds);
        for (var i = _clearing.Count - 1; i >= 0; i--)
        {
            var (cell, seconds) = _clearing[i];
            seconds += realSeconds;
            var value = seconds >= _clearSeconds ? Clear : (byte)Math.Max(_texels[cell], (int)(Clear * seconds / _clearSeconds));
            _changed |= value != _texels[cell];
            _texels[cell] = value;
            if (value == Clear)
            {
                // Order does not matter: the last one takes the free place.
                _clearing[i] = _clearing[^1];
                _clearing.RemoveAt(_clearing.Count - 1);
            }
            else
            {
                _clearing[i] = (cell, seconds);
            }
        }

        for (var started = 0; started < _cellsPerFrame && _waiting.TryDequeue(out var cell); started++)
        {
            _clearing.Add((cell, 0));
        }

        var changed = _changed;
        _changed = false;
        return changed;
    }

    /// <summary>Everything the player has seen is clear at once (pictures show a settled cover).</summary>
    public void Settle()
    {
        foreach (var cell in _waiting.Concat(_clearing.Select(c => c.Cell)))
        {
            _texels[cell] = Clear;
            _changed = true;
        }

        _waiting.Clear();
        _clearing.Clear();
    }

    /// <summary>
    /// For pictures (the screenshot tool, <c>revealed_permille</c> of a shot point): shows the cover as if
    /// at least that share of the coverable cells were seen, settled. The cells nearest to the middle of
    /// what is seen clear first, so the open land grows as a round patch. It only ever clears: a share
    /// below the seen one changes nothing, and the core's view is not touched.
    /// </summary>
    public void ShowShare(int permille)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(permille);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permille, 1000);
        Settle();
        var wanted = (int)Math.Ceiling(_coverable * permille / 1000.0);
        if (wanted <= _seen)
        {
            return;
        }

        // The middle of the seen cells (the middle of the map while nothing is seen).
        double sumX = 0, sumZ = 0;
        var covered = new List<int>();
        for (var cell = 0; cell < _texels.Length; cell++)
        {
            if (_open[cell])
            {
                continue;
            }

            if (_known[cell])
            {
                sumX += cell % Width;
                sumZ += cell / Width;
            }
            else
            {
                covered.Add(cell);
            }
        }

        var (midX, midZ) = _seen == 0 ? ((Width - 1) / 2.0, (Depth - 1) / 2.0) : (sumX / _seen, sumZ / _seen);
        double Distance(int cell) => (((cell % Width) - midX) * ((cell % Width) - midX)) + (((cell / Width) - midZ) * ((cell / Width) - midZ));
        foreach (var cell in covered.OrderBy(Distance).ThenBy(c => c).Take(wanted - _seen))
        {
            Know(cell);
            _texels[cell] = Clear;
        }

        _changed = true;
    }

    /// <summary>
    /// The open sea: the cells further than <paramref name="marginCells"/> from any land. They never lie
    /// under cloud, so the cover is a bank over the island and its coast, not a sheet to the map edge.
    /// Distances are chamfer distances (3 straight, 4 diagonal), whole numbers.
    /// </summary>
    public static bool[] OpenSea(int width, int depth, Func<int, int, bool> isLand, int marginCells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentNullException.ThrowIfNull(isLand);
        ArgumentOutOfRangeException.ThrowIfNegative(marginCells);
        const int Far = int.MaxValue / 2;
        var distance = new int[width * depth];
        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < width; x++)
            {
                distance[(z * width) + x] = isLand(x, z) ? 0 : Far;
            }
        }

        int Near(int x, int z, int step) => x < 0 || z < 0 || x >= width || z >= depth ? Far : distance[(z * width) + x] + step;
        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (z * width) + x;
                distance[i] = Math.Min(Math.Min(distance[i], Near(x - 1, z, 3)), Math.Min(Near(x, z - 1, 3), Math.Min(Near(x - 1, z - 1, 4), Near(x + 1, z - 1, 4))));
            }
        }

        for (var z = depth - 1; z >= 0; z--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var i = (z * width) + x;
                distance[i] = Math.Min(Math.Min(distance[i], Near(x + 1, z, 3)), Math.Min(Near(x, z + 1, 3), Math.Min(Near(x + 1, z + 1, 4), Near(x - 1, z + 1, 4))));
            }
        }

        return [.. distance.Select(d => d > marginCells * 3)];
    }

    private int Index(int x, int z)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, Depth);
        return (z * Width) + x;
    }

    private void Know(int cell)
    {
        _known[cell] = true;
        _seen++;
    }

    private void Reset()
    {
        for (var cell = 0; cell < _texels.Length; cell++)
        {
            (_texels[cell], _known[cell]) = (_open[cell] ? Clear : (byte)0, false);
        }

        _waiting.Clear();
        _clearing.Clear();
        (Version, _seen, _started, _changed) = (0, 0, false, true);
    }
}
