using GlassWorld.AiClient;
using GlassWorld.Presentation.World;
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
/// max_ticks_per_frame ticks per frame) and draws it: as the placeholder (capsules and spheres on a flat
/// ground) or, in island mode (content/look/world.json, GLASSWORLD_WORLD), as the generated island with the
/// real figures and objects (WorldPresenter.Island.cs). No game logic here.
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
    private HttpConversationPort? _conversationPort;
    private HttpJobPort? _jobPort;
    private HttpHelpPort? _helpPort;
    private long _seenEvents;
    private double _clock;
    private SimulationHost? _host;
    private MultiMeshInstance3D? _objects;
    private int _objectCount = -1;
    private double _accumulator;

    /// <summary>Help suggestions from the AI (A1341); null without the live service (the window uses the data).</summary>
    public HttpHelpPort? HelpPort => _helpPort;

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
    /// Starts a new world: the placeholder start for the seed, or in island mode (content/look/world.json,
    /// overridden by GLASSWORLD_WORLD) the island generated from the seed and the data's preset. Size and
    /// climate of the new-world form are only stored by the caller. With a save path the world is saved at
    /// once and then after every game day (B2487, Leitbild: at most one game day lost).
    /// </summary>
    /// <param name="contentDirectory">Content folder.</param>
    /// <param name="seed">World seed.</param>
    /// <param name="startFavor">Start favour chosen in the new-world form (null = default of the data).</param>
    /// <param name="savePath">Save game, or null for an unsaved world.</param>
    public void CreateWorld(string contentDirectory, ulong seed, int? startFavor, string? savePath)
    {
        _contentDirectory = contentDirectory;
        var look = ReadLook(contentDirectory);
        var mode = WorldLookChoice.ForNewWorld(look, System.Environment.GetEnvironmentVariable(WorldLookChoice.EnvironmentVariable), out var problem);
        if (problem is not null)
        {
            GD.PushWarning($"WORLD_LOOK {problem}");
        }

        var port = CreatePort(contentDirectory);
        SimulationHost Make(WorldLookMode m)
        {
            var made = m == WorldLookMode.Island
                ? SimulationHost.CreateGeneratedWorld(contentDirectory, seed, port, "de", look.IslandPreset, _jobPort)
                : SimulationHost.CreatePlaceholderWorld(contentDirectory, seed, port, "de", _jobPort);
            if (startFavor is { } favor)
            {
                // Part of the start (GW1 review #67 S1/N1): as a command in the log, applied at the first
                // tick, so a replay from seed and log starts with the same favour.
                // Applied and logged at once: the world is saved right after creation, before the first tick
                // (GW1 #73 F1); a replay applies the logged command again at the first tick.
                made.Decisions.ApplySetup(made.World, new Simulation.Decisions.PlayerCommand(Simulation.Player.PlayerSystem.StartFavorCommand, null) { Amount = favor });
            }

            return made;
        }

        if (mode == WorldLookMode.Island)
        {
            try
            {
                Begin(Make(WorldLookMode.Island), savePath);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A new world must always start (GW3 #113, finding 3): if the island cannot be generated or
                // drawn, the placeholder world takes its place. A loaded island save still shows its error.
                GD.PushWarning($"WORLD_LOOK_FALLBACK island failed ({e.GetType().Name}: {e.Message}); building the placeholder world");
                UndoIsland();
                Begin(Make(WorldLookMode.Placeholder), savePath);
            }
        }
        else
        {
            Begin(Make(WorldLookMode.Placeholder), savePath);
        }

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
        _contentDirectory = contentDirectory;
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
            System.IO.File.ReadAllText(System.IO.Path.Combine(contentDirectory, "decider", "settings.json")));
        var decisions = port ?? CreatePort(contentDirectory);
        var factory = new SimulationHostFactory(ContentSet.Load(contentDirectory), settings, decisions, "de", _jobPort);
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

        // Talking (B6 R13): the live service when it runs, otherwise the deterministic mock; with a
        // service that failed to start nothing answers and the core gives a calm "no answer" (R16).
        host.Conversation.Port = _conversationPort is not null ? _conversationPort : _service is null && !_aiFailed ? new Simulation.Player.MockConversationPort() : null;
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

        if (WorldLookChoice.ForWorld(host.Map is not null) == WorldLookMode.Island)
        {
            BeginIsland();
            return;
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
        }

        if (Mode == WorldLookMode.Island)
        {
            ProcessIsland(ticks);
            _clock += delta;
            ShowThoughts();
            IslandCamera();
            return;
        }

        if (ticks > 0)
        {
            UpdateVisuals();
        }

        _clock += delta;
        ShowThoughts();
        FollowWithCamera(delta);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        RestoreScene();
        _livePort?.Dispose();
        _conversationPort?.Dispose();
        _jobPort?.Dispose();
        _helpPort?.Dispose();
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

        // The AI service runs from the repository (uv project). An exported build sits elsewhere, so
        // GLASSWORLD_AI_ROOT may name the folder that holds it (test package, until B10 packages the service).
        var root = System.Environment.GetEnvironmentVariable("GLASSWORLD_AI_ROOT");
        var repository = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? System.IO.Path.Combine(contentDirectory, "..") : root);
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

            // Generation jobs (inventions, D471): only with the live service; with the mock the system is dormant.
            _jobPort = HttpJobPort.ForService(_service);
            _helpPort = HttpHelpPort.ForService(_service, settings.DeadlineMs);
            _conversationPort = HttpConversationPort.ForService(_service, settings.DeadlineMs);
            GD.Print($"AI_SERVICE live on port {_service.Port}");
            return _livePort;
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // B3 A3: without the service the world runs on in routine mode; questions simply stay open.
            GD.PushWarning($"AI service not started ({ex.Message}); inhabitants live in routine mode.");
            _aiFailed = true;
            return new SilentPort();
        }
    }

    private bool _aiFailed;
    private int _focusGroup = 1;
    private double _focusUntil;
    private bool _groundReady;

    /// <summary>
    /// Observer camera until B2-3 brings the real camera: follows the group where the last thought
    /// appeared, otherwise switches groups every 30 s; smooth, presentation only. On first use the B0
    /// island placeholder is hidden and a plain ground is laid under the placeholder world.
    /// </summary>
    /// <summary>Picture runs only: the placeholder camera stays where it was put and does not follow the groups.</summary>
    public bool CameraHeld { get; set; }

    private void FollowWithCamera(double delta)
    {
        if (_host is null || GetViewport().GetCamera3D() is not { } camera)
        {
            return;
        }

        if (!_groundReady)
        {
            _groundReady = true;
            if (GetParent()?.GetNodeOrNull<Node3D>("IslandPlaceholder") is { } island)
            {
                island.Visible = false;
            }

            AddChild(new MeshInstance3D
            {
                Name = "PlaceholderGround",
                Mesh = new PlaneMesh { Size = new Vector2(400, 120) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.78f, 0.55f) },
            });
        }

        if (CameraHeld)
        {
            return;
        }

        var groups = _host.Inhabitants.All.Select(i => i.Group).Distinct().Order().ToList();
        if (groups.Count == 0)
        {
            return;
        }

        if (_clock > _focusUntil)
        {
            var next = groups.IndexOf(_focusGroup) + 1;
            _focusGroup = groups[next % groups.Count];
            _focusUntil = _clock + 30.0;
        }

        var members = _host.Inhabitants.All.Where(i => i.Group == _focusGroup).ToList();
        if (members.Count == 0)
        {
            return;
        }

        var centre = new Vector3((float)members.Average(i => i.Position.X), 0.9f, (float)members.Average(i => i.Position.Z));
        var wanted = centre + new Vector3(0, 9f, 13f);
        var blend = 1 - Mathf.Exp(-2.5f * (float)delta);
        camera.GlobalPosition = camera.GlobalPosition.Lerp(wanted, blend);
        camera.LookAt(centre, Vector3.Up);
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
            if (Talk(e) is { } talk && _thoughts.TryGetValue(e.InhabitantId, out var said))
            {
                // Talking (B6 R13): "..." while it thinks, then the answer or a question mark.
                said.Label.Text = talk.Text;
                said.Label.Visible = true;
                _thoughts[e.InhabitantId] = (said.Label, _clock + talk.Seconds);
                continue;
            }

            if (e.Kind == "thought" && e.Text is { Length: > 0 } text && _thoughts.TryGetValue(e.InhabitantId, out var bubble))
            {
                bubble.Label.Text = text;
                bubble.Label.Visible = true;
                _thoughts[e.InhabitantId] = (bubble.Label, _clock + ThoughtSeconds);
                _focusGroup = _host.Inhabitants.Get(e.InhabitantId).Group;
                _focusUntil = _clock + 12.0;
                ObserveThought(e.InhabitantId);
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

    /// <summary>The bubble of a conversation event: text and how long it stays (null for other events).</summary>
    private static (string Text, double Seconds)? Talk(Simulation.Inhabitants.WorldEvent e) => e.Kind switch
    {
        "conversation_started" => ("…", 120.0),
        "said" or "declined" when e.Text is { Length: > 0 } text => (text, ThoughtSeconds + 2),
        "gestured" or "declined" => ("…", 2.0),
        "not_understood" => ("?", ThoughtSeconds),
        "no_answer" => (string.Empty, 0.0),
        _ => null,
    };

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
