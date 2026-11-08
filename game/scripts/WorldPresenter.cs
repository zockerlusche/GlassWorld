using GlassWorld.AiClient;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Shows the core world (R15): builds it from the content folder, advances it with an accumulator at the
/// speed from content/world/time.json (normal: one game day in the configured real seconds, at most
/// max_ticks_per_frame ticks per frame) and draws placeholder figures and objects. No game logic here;
/// the island, look and animation come with B2.
/// The decider is the mock by default (deterministic smoke and CI). With the environment variable
/// <c>GLASSWORLD_AI=live</c> the game starts the local AI service itself (ADR-0006, B3 S7) and stops it
/// when the scene ends. Thoughts from answers appear as a short bubble above the figure.
/// </summary>
public partial class WorldPresenter : Node3D
{
    /// <summary>How long a thought bubble stays visible (presentation only, real seconds).</summary>
    private const double ThoughtSeconds = 6.0;

    private readonly Dictionary<string, MeshInstance3D> _figures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Label3D Label, double Until)> _thoughts = new(StringComparer.Ordinal);
    private AiServiceProcess? _service;
    private HttpDecisionPort? _livePort;
    private long _seenEvents;
    private double _clock;
    private SimulationHost? _host;
    private MultiMeshInstance3D? _objects;
    private int _objectCount = -1;
    private double _accumulator;

    /// <summary>The running simulation (null until <see cref="Start"/>).</summary>
    public SimulationHost? Host => _host;

    /// <summary>Speed factor: 0 = pause, 1 = normal, 4/16 = fast forward (B6 adds the controls).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Save game of the running world (null = not saved, e.g. smoke runs).</summary>
    public string? SavePath { get; private set; }

    /// <summary>Raised after every save (autosave at the day boundary or explicit), e.g. to update meta data.</summary>
    public event Action<string>? Saved;

    /// <summary>
    /// Raised when an autosave fails (disk full, file locked by a virus scanner or cloud sync). The world
    /// runs on; the next game day tries again. The screen shows a calm hint (B6 R3).
    /// </summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>State hash right after restoring, before open questions are sent again (save/load check).</summary>
    public string? HashAtLoad { get; private set; }

    /// <summary>Builds the placeholder start world for a seed and creates the visuals (no save game).</summary>
    public void Start(string contentDirectory, ulong seed) => CreateWorld(contentDirectory, seed, null, null);

    /// <summary>
    /// Starts a new world. Until B2-2 generates the island, size and climate are only stored by the
    /// caller; the world is the placeholder start for the seed. With a save path the world is saved at
    /// once and then after every game day (B2487, Leitbild: at most one game day lost).
    /// </summary>
    /// <param name="contentDirectory">Content folder.</param>
    /// <param name="seed">World seed.</param>
    /// <param name="startFavor">Start favour chosen in the new-world form (null = default of the data).</param>
    /// <param name="savePath">Save game, or null for an unsaved world.</param>
    public void CreateWorld(string contentDirectory, ulong seed, int? startFavor, string? savePath)
    {
        var host = SimulationHost.CreatePlaceholderWorld(contentDirectory, seed, CreatePort(contentDirectory), "de");
        if (startFavor is { } favor)
        {
            // Part of the start (GW1 review #67 S1/N1): as a command in the log, applied at the first
            // tick, so a replay from seed and log starts with the same favour.
            // Applied and logged at once: the world is saved right after creation, before the first tick
            // (GW1 #73 F1); a replay applies the logged command again at the first tick.
            host.Decisions.ApplySetup(host.World, new Simulation.Decisions.PlayerCommand(Simulation.Player.PlayerSystem.StartFavorCommand, null) { Amount = favor });
        }

        Begin(host, savePath);
        if (savePath is not null)
        {
            Save();
        }
    }

    /// <summary>
    /// Loads a world from its save game (ADR-0003: same state hash as when saved). Questions that were
    /// open at saving go out again with the current port (mock or live), because the old answers are lost.
    /// </summary>
    /// <param name="contentDirectory">Content folder.</param>
    /// <param name="savePath">Save game to load.</param>
    /// <param name="port">Decider port; null = the usual one (mock, or the live service with GLASSWORLD_AI=live).</param>
    public void Load(string contentDirectory, string savePath, IDecisionPort? port = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(savePath);
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
            System.IO.File.ReadAllText(System.IO.Path.Combine(contentDirectory, "decider", "settings.json")));
        var factory = new SimulationHostFactory(ContentSet.Load(contentDirectory), settings, port ?? CreatePort(contentDirectory), "de");
        var host = StateCodec.Restore(factory, SaveGameStore.Load(savePath));
        HashAtLoad = host.World.StateHash();
        host.Decisions.ResendPendingAfterLoad(host.World);
        Begin(host, savePath);
    }

    /// <summary>Saves the running world into its save game (crash-safe: copy, then swap).</summary>
    public void Save()
    {
        if (_host is null || SavePath is null)
        {
            return;
        }

        SaveGameStore.Save(SavePath, StateCodec.Capture(_host));
        Saved?.Invoke(SavePath);
    }

    private void Begin(SimulationHost host, string? savePath)
    {
        if (_host is not null)
        {
            throw new InvalidOperationException("The presenter already shows a world; create a new presenter for another world.");
        }

        _host = host;
        SavePath = savePath;
        if (savePath is not null)
        {
            // Autosave every game day (B2487). Like AutoSaver, but a failure must not escape into
            // World.Step: it would cost the remaining ticks of the frame and the player would not know.
            host.World.BoundaryReached += (_, boundary) =>
            {
                if (boundary != TimeBoundary.Day)
                {
                    return;
                }

                try
                {
                    Save();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    GD.PushWarning($"SAVE_FAILED autosave {e.GetType().Name}: {e.Message}");
                    SaveFailed?.Invoke(e);
                }
            };
        }

        foreach (var inhabitant in _host.Inhabitants.All)
        {
            var figure = new MeshInstance3D
            {
                Name = inhabitant.Id,
                Mesh = new CapsuleMesh { Radius = 0.6f, Height = 1.8f },
            };
            AddChild(figure);
            _figures[inhabitant.Id] = figure;
        }

        foreach (var (id, figure) in _figures)
        {
            var label = new Label3D { Name = "Thought", Position = new Vector3(0, 1.6f, 0), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Visible = false };
            figure.AddChild(label);
            _thoughts[id] = (label, 0);
        }

        _objects = new MultiMeshInstance3D { Name = "Objects" };
        AddChild(_objects);
        UpdateVisuals();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_host is null)
        {
            return;
        }

        var calendar = _host.World.Calendar;
        _accumulator += delta * Speed / calendar.RealSecondsPerTickAtNormalSpeed;
        var ticks = Math.Min((long)_accumulator, calendar.Settings.MaxTicksPerFrame);
        _accumulator -= ticks;
        _accumulator = Math.Min(_accumulator, calendar.Settings.MaxTicksPerFrame);
        if (ticks > 0)
        {
            _host.World.Run(ticks);
            UpdateVisuals();
        }

        _clock += delta;
        ShowThoughts();
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        _livePort?.Dispose();
        if (_service is not null)
        {
            // ADR-0006: shutdown endpoint unloads the model (frees VRAM), then the process is killed.
            // At least 20 s: the service unloads the model first (GW2 #30).
            _service.StopAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            _service.Dispose();
            GD.Print("AI_SERVICE stopped");
            _service = null;
        }
    }

    private IDecisionPort CreatePort(string contentDirectory)
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("GLASSWORLD_AI"), "live", StringComparison.Ordinal))
        {
            return new MockDecisionPort();
        }

        var repository = System.IO.Path.GetFullPath(System.IO.Path.Combine(contentDirectory, ".."));
        try
        {
            var userData = System.IO.Path.Combine(OS.GetUserDataDir(), "ai-service");
            _service = AiServiceProcess.Start(
                "uv",
                ["run", "python", "-m", "glassworld_ai"],
                repository,
                TimeSpan.FromSeconds(60),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GW_AI_LOCK_DIR"] = System.IO.Path.Combine(userData, "lock"),
                    ["GW_AI_LOG_DIR"] = System.IO.Path.Combine(userData, "logs"),
                });
            if (_service.StaleKilled is { } stale)
            {
                GD.Print($"AI_SERVICE ended leftover service from an earlier session (pid {stale})");
            }

            var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
                System.IO.File.ReadAllText(System.IO.Path.Combine(contentDirectory, "decider", "settings.json")));
            _livePort = HttpDecisionPort.ForService(_service, settings.DeadlineMs);
            GD.Print($"AI_SERVICE live on port {_service.Port}");
            return _livePort;
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // B3 A3: without the service the world runs on in routine mode; questions simply stay open.
            GD.PushWarning($"AI service not started ({ex.Message}); inhabitants live in routine mode.");
            return new SilentPort();
        }
    }

    private void ShowThoughts()
    {
        if (_host is null)
        {
            return;
        }

        foreach (var sequenced in _host.Inhabitants.EventsSince(_seenEvents))
        {
            _seenEvents = sequenced.Seq;
            var e = sequenced.Event;
            if (e.Kind == "thought" && e.Text is { Length: > 0 } text && _thoughts.TryGetValue(e.InhabitantId, out var bubble))
            {
                bubble.Label.Text = text;
                bubble.Label.Visible = true;
                _thoughts[e.InhabitantId] = (bubble.Label, _clock + ThoughtSeconds);
            }
        }

        foreach (var (id, bubble) in _thoughts.ToList())
        {
            if (bubble.Label.Visible && _clock > bubble.Until)
            {
                bubble.Label.Visible = false;
            }
        }
    }

    /// <summary>Decider that never answers (service could not be started): questions stay open (B2487).</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    private void UpdateVisuals()
    {
        if (_host is null || _objects is null)
        {
            return;
        }

        foreach (var inhabitant in _host.Inhabitants.All)
        {
            _figures[inhabitant.Id].Position = new Vector3((float)inhabitant.Position.X, 0.9f, (float)inhabitant.Position.Z);
        }

        var objects = _host.Objects.All.ToList();
        if (objects.Count != _objectCount)
        {
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new SphereMesh { Radius = 0.25f, Height = 0.5f },
                InstanceCount = objects.Count,
            };
            for (var i = 0; i < objects.Count; i++)
            {
                multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3((float)objects[i].Position.X, 0.25f, (float)objects[i].Position.Z)));
            }

            _objects.Multimesh = multimesh;
            _objectCount = objects.Count;
        }
    }
}
