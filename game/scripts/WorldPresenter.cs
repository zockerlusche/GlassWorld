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
    private readonly Dictionary<string, (GlassWorld.Game.World.ScreenBubble Label, double Until)> _thoughts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _speechUntil = new(StringComparer.Ordinal);
    private AiServiceProcess? _service;
    private HttpDecisionPort? _livePort;
    private HttpConversationPort? _conversationPort;
    private HttpJobPort? _jobPort;
    private HttpHelpPort? _helpPort;
    private HttpSetupPort? _setupPort;
    private string? _contentFingerprint;
    private long _seenEvents;
    private double _clock;
    private SimulationHost? _host;
    private MultiMeshInstance3D? _objects;
    private int _objectCount = -1;
    private double _accumulator;
    private string? _askedLocale;

    /// <summary>Help suggestions from the AI (A1341); null without the live service (the window uses the data).</summary>
    public HttpHelpPort? HelpPort => _helpPort;

    /// <summary>Self-check and diagnostics of the AI service (W1703); null without the live service.</summary>
    public HttpSetupPort? SetupPort => _setupPort;

    /// <summary>The running simulation (null until <see cref="Start"/>).</summary>
    public SimulationHost? Host => _host;

    /// <summary>Whether the core accepts debug commands (debug jumps, B2-6 R31); set before the world starts, off by default.</summary>
    public bool DebugCommands { get; init; }

    /// <summary>Speed factor: 0 = pause, 1 = normal, 4/16 = fast forward (B6 adds the controls).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Save game of the running world (null = not saved, e.g. smoke runs).</summary>
    public string? SavePath { get; private set; }

    /// <summary>Raised after a successful autosave at the day boundary (a calm notice, test feedback F5287).</summary>
    public event Action? AutoSaved;

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
                ? SimulationHost.CreateGeneratedWorld(contentDirectory, seed, port, UiLocale(), look.IslandPreset, _jobPort, new SimulationHostOptions(DebugCommands))
                : SimulationHost.CreatePlaceholderWorld(contentDirectory, seed, port, UiLocale(), _jobPort, new SimulationHostOptions(DebugCommands));
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
        var factory = new SimulationHostFactory(ContentSet.Load(contentDirectory), settings, decisions, UiLocale(), _jobPort, new SimulationHostOptions(DebugCommands));
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

        // The content fingerprint lets a replay tell changed data from lost determinism (once per world, ~1.5 MB).
        _contentFingerprint ??= ContentFingerprint.TryCompute(_contentDirectory);
        SaveGameStore.Save(SavePath, StateCodec.Capture(_host) with { ContentFingerprint = _contentFingerprint });
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
        _askedLocale = host.DisplayLocale;

        // Talking (B6 R13): only the live service answers. Without it (routine mode, or the service
        // failed to start) nothing answers and the core gives the calm "no answer" with the favour back
        // (R16, review GW1 #131) - never made-up mock words in front of the player.
        host.Conversation.Port = _conversationPort;
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
                    AutoSaved?.Invoke();
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
            var label = new GlassWorld.Game.World.ScreenBubble { Name = "Thought", Position = new Vector3(0, 1.6f, 0), Visible = false };
            figure.AddChild(label);
            _thoughts[id] = (label, 0);
        }

        _objects = new MultiMeshInstance3D { Name = "Objects" };
        AddChild(_objects);
        UpdateVisuals();
        BeginPlaceholderCamera();
        LightTheScene(_contentDirectory, ReadLook(_contentDirectory));
    }

    /// <summary>
    /// The language the UI shows (the TranslationServer locale the settings set), as the core knows it;
    /// another one falls back to the UI's fallback language (#20043: no fixed "de" any more).
    /// </summary>
    private static string UiLocale()
    {
        var shown = TranslationServer.GetLocale();
        return Localization.TextCatalogs.SupportedLocales.FirstOrDefault(l => shown.StartsWith(l, StringComparison.Ordinal))
            ?? Localization.TextCatalogs.SupportedLocales[0];
    }

    /// <summary>
    /// If the player switched the language, new requests use it from the next tick (a logged command, so
    /// replays stay identical); questions already asked are not asked again.
    /// </summary>
    private void FollowUiLanguage()
    {
        var locale = UiLocale();
        if (_host is null || locale == _askedLocale)
        {
            return;
        }

        _askedLocale = locale;
        _host.Decisions.Submit(new PlayerCommand(DecisionsSystem.SetDisplayLocale, null) { Text = locale });
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_host is null)
        {
            return;
        }

        FollowUiLanguage();
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
            FollowNature(delta);
            FollowTheClock();
            _clock += delta;
            ShowThoughts();
            ObserverCamera();
            KeepFiguresVisible();
            return;
        }

        if (ticks > 0)
        {
            UpdateVisuals();
        }

        FollowTheClock();
        _clock += delta;
        ShowThoughts();
        LayPlaceholderGround();
        ObserverCamera();
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        RestoreScene();
        _livePort?.Dispose();
        _conversationPort?.Dispose();
        _jobPort?.Dispose();
        _helpPort?.Dispose();
        _setupPort?.Dispose();
        _setupPort = null;
        if (_service is not null)
        {
            // ADR-0006: shutdown endpoint unloads the model (frees VRAM), then the process is killed.
            // At least 20 s: the service unloads the model first (GW2 #30).
            _service.StopAsync(LocalAiService.StopTimeout).GetAwaiter().GetResult();
            _service.Dispose();
            GD.Print("AI_SERVICE stopped");
            _service = null;
        }
    }

    private IDecisionPort CreatePort(string contentDirectory)
    {
        if (!string.Equals(System.Environment.GetEnvironmentVariable("GLASSWORLD_AI"), "live", StringComparison.Ordinal))
        {
            return MockDecisionPort.FromContent(contentDirectory);
        }

        // The AI service runs from the repository (uv project). An exported build sits elsewhere, so
        // GLASSWORLD_AI_ROOT may name the folder that holds it (test package, until B10 packages the service).
        var repository = LocalAiService.RepositoryRoot(contentDirectory, System.Environment.GetEnvironmentVariable(LocalAiService.RootVariable));
        try
        {
            _service = LocalAiService.Start(repository, System.IO.Path.Combine(OS.GetUserDataDir(), "ai-service"));
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
            _setupPort = HttpSetupPort.ForService(_service);
            _conversationPort = HttpConversationPort.ForService(_service, settings.DeadlineMs);
            GD.Print($"AI_SERVICE live on port {_service.Port}");
            return _livePort;
        }
        catch (Exception ex) when (LocalAiService.IsStartFailure(ex))
        {
            // B3 A3: without the service the world runs on in routine mode; questions simply stay open.
            GD.PushWarning($"AI service not started ({ex.Message}); inhabitants live in routine mode.");
            return new SilentPort();
        }
    }

    private bool _groundReady;
    private bool _cameraHeld;

    /// <summary>Picture runs only: the camera stays where the run puts it (the rig neither moves it nor reads input).</summary>
    public bool CameraHeld
    {
        get => _cameraHeld;
        set
        {
            _cameraHeld = value;
            if (_rig is not null)
            {
                _rig.Held = value;
            }
        }
    }

    /// <summary>On first use the B0 island placeholder is hidden and a plain ground is laid under the placeholder world.</summary>
    private void LayPlaceholderGround()
    {
        if (_groundReady || _host is null)
        {
            return;
        }

        _groundReady = true;
        if (GetParent()?.GetNodeOrNull<Node3D>("IslandPlaceholder") is { } island)
        {
            island.Visible = false;
        }

        AddChild(new MeshInstance3D
        {
            Name = "PlaceholderGround",
            Mesh = new PlaneMesh { Size = new Vector2(PlaceholderGroundM.Width, PlaceholderGroundM.Depth) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.78f, 0.55f) },
        });
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
            if (TalkBubble.For(e.Kind, e.Text, _look?.Labels.SpeechS ?? (ThoughtSeconds + 2)) is { } talk)
            {
                // Talking (B6 R13): "..." while it thinks, then the answer or a question mark; the figures
                // show that they heard and the camera follows the one spoken to (W1688).
                if (_thoughts.TryGetValue(e.InhabitantId, out var said))
                {
                    said.Label.Text = talk.Text;
                    said.Label.Visible = talk.Seconds > 0;
                    _thoughts[e.InhabitantId] = (said.Label, _clock + Shorter(talk.Seconds));
                }

                _speechUntil[e.InhabitantId] = TalkBubble.IsSpeech(e.Kind, e.Text) ? _clock + Shorter(talk.Seconds) : 0;
                React(e);
                continue;
            }

            if (e.Kind == "thought" && e.Text is { Length: > 0 } text && _thoughts.TryGetValue(e.InhabitantId, out var bubble)
                && _clock >= _speechUntil.GetValueOrDefault(e.InhabitantId))
            {
                // A thought never covers a spoken answer still showing (W1688).
                bubble.Label.Text = text;
                bubble.Label.Visible = true;
                _thoughts[e.InhabitantId] = (bubble.Label, _clock + Shorter(_look?.Labels.ThoughtSeconds ?? ThoughtSeconds));
                ObserveThought(e.InhabitantId);
            }
        }

        foreach (var bubble in _thoughts.Values)
        {
            if (bubble.Label.Visible && _clock > bubble.Until)
            {
                bubble.Label.Visible = false;
            }
        }
    }

    /// <summary>
    /// How long a line stays: at high speed the data's shorter time, so lines do not pile up (W1687). A waiting
    /// "…" (much longer than any line) is kept.
    /// </summary>
    private double Shorter(double seconds)
    {
        if (_look?.Labels is not { } labels || seconds > 60)
        {
            return seconds;
        }

        return Speed >= labels.FastFromSpeed ? Math.Min(seconds, labels.ThoughtSecondsFast) : seconds;
    }

    /// <summary>
    /// The screen layer for thoughts and said sentences (W1687): one line per inhabitant, never on top of
    /// another, inside <paramref name="area"/> (below the top bar, beside the side panel).
    /// </summary>
    public void ShowBubbles(Func<Rect2?> area, Func<float> uiScale)
    {
        if (GetNodeOrNull<GlassWorld.Game.World.BubbleOverlay>("Bubbles") is { } old)
        {
            old.QueueFree();
        }

        var overlay = new GlassWorld.Game.World.BubbleOverlay
        {
            Name = "Bubbles",
            Bubbles = () => _thoughts.Select(t => (t.Key, t.Value.Label)),
            Area = area,
            WidthPx = () => (_look?.Labels.BubbleWidthPx ?? 440) * uiScale(),
            OrderHysteresisPx = () => (float)(_look?.Labels.BubbleOrderHysteresisPx ?? 0) * uiScale(),
        };
        AddChild(overlay);
    }

    /// <summary>Smoke only: the bubbles on screen now.</summary>
    public IReadOnlyList<(string Id, Rect2 Box)> BubblesShown() =>
        GetNodeOrNull<GlassWorld.Game.World.BubbleOverlay>("Bubbles")?.Shown() ?? [];

    /// <summary>Smoke only: makes an inhabitant say <paramref name="text"/> now.</summary>
    public void SayForCheck(string inhabitantId, string text)
    {
        if (_thoughts.TryGetValue(inhabitantId, out var bubble))
        {
            bubble.Label.Text = text;
            bubble.Label.Visible = true;
            _thoughts[inhabitantId] = (bubble.Label, _clock + 60);
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
