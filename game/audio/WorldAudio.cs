using System.Reflection;
using GlassWorld.Contracts;
using GlassWorld.Presentation.Audio;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Objects;
using Godot;

namespace GlassWorld.Game.Audio;

/// <summary>
/// Plays the world's sounds in space (B8 R2/R7, B2512): buses from content/sound/mix.json, a pool of
/// <see cref="AudioStreamPlayer3D"/> per bus (its voice budget), Godot's own distance attenuation
/// switched off so the volume comes from <see cref="SoundMixer"/> (same rule as the tests); Godot pans
/// by direction. Volumes of playing sounds follow the camera every frame.
/// </summary>
public partial class WorldAudio : Node3D
{
    private const string BaseFolder = "res://assets/sound/base/";
    private readonly Dictionary<MixBusId, List<AudioStreamPlayer3D>> _pools = [];
    private readonly Dictionary<string, AudioStreamWav?> _fileStreams = new(StringComparer.Ordinal);
    private BaseSounds? _baseSounds;
    private SoundMixer? _mixer;
    private SoundDirector? _director;
    private WorldFeed? _feed;
    private ulong _worldSeed;
    private WorldMusic? _music;

    /// <summary>Zoom 0 (fully in) .. 1 (fully out); set by the camera control.</summary>
    public double Zoom { get; set; } = 0.5;

    /// <summary>The mixer in use (null until <see cref="Setup"/>).</summary>
    public SoundMixer? Mixer => _mixer;

    /// <summary>Loads the mix, creates buses and player pools and binds to a running world.</summary>
    /// <returns>False if the mix or sound data is invalid (logged).</returns>
    public bool Setup(SimulationHost host, ulong worldSeed)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (RetroAudio.Library is null && !RetroAudio.Load())
        {
            return false;
        }

        try
        {
            _mixer = SoundMixer.FromJson(Read("content.sound.mix.json"));
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushError($"audio: {e.Message}");
            return false;
        }

        _baseSounds = LoadBaseSounds();
        _director = new SoundDirector(RetroAudio.Library!, _mixer, _baseSounds?.Variants);
        _feed = new WorldFeed(host);
        _worldSeed = worldSeed;
        _music = new WorldMusic { Name = "WorldMusic" };
        AddChild(_music);
        if (!_music.Setup(host, worldSeed, BusName(MixBusId.Music)))
        {
            _music.QueueFree();
            _music = null;
        }
        foreach (var bus in _mixer.Mix.Buses)
        {
            var name = BusName(bus.Id);
            var index = AudioServer.GetBusIndex(name);
            if (index < 0)
            {
                AudioServer.AddBus();
                index = AudioServer.BusCount - 1;
                AudioServer.SetBusName(index, name);
                AudioServer.SetBusSend(index, BusName(MixBusId.Master));
            }

            AudioServer.SetBusVolumeDb(index, (float)bus.VolumeDb);
            if (bus.Id is MixBusId.Master or MixBusId.Music)
            {
                continue;
            }

            var pool = new List<AudioStreamPlayer3D>();
            for (var i = 0; i < bus.MaxVoices; i++)
            {
                var player = new AudioStreamPlayer3D
                {
                    Bus = name,
                    AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                    MaxDistance = (float)_mixer.SilentDistance(bus.Id, 1),
                };
                AddChild(player);
                pool.Add(player);
            }

            _pools[bus.Id] = pool;
        }

        return true;
    }

    public override void _Process(double delta)
    {
        if (_feed is null || _director is null || _mixer is null)
        {
            return;
        }

        var listener = Listener();
        foreach (var (bus, pool) in _pools)
        {
            foreach (var player in pool.Where(p => p.Playing))
            {
                ApplyVolume(player, bus, listener);
            }
        }

        var events = _feed.NewEvents();
        if (events.Count == 0)
        {
            return;
        }

        var positions = _feed.Snapshot.Inhabitants.ToDictionary(i => i.Id, i => i.Position, StringComparer.Ordinal);
        var playing = _pools.ToDictionary(p => p.Key, p => p.Value.Count(x => x.Playing));
        foreach (var request in _director.Plan(events, positions, listener, Zoom, playing))
        {
            var player = _pools[request.Bus].FirstOrDefault(p => !p.Playing);
            if (player is null)
            {
                continue;
            }

            player.Stream = Stream(request.SoundId, request.Variant);
            player.Position = new Vector3((float)request.Position.X, 0, (float)request.Position.Z);
            ApplyVolume(player, request.Bus, listener);
            player.Play();
        }
    }

    /// <summary>Smoke run only: the background music (B8 R6).</summary>
    public string MusicSelfCheck() => _music?.SelfCheck() ?? "music not set up";

    /// <summary>Sets a player's volume from its ground distance to the listener.</summary>
    public void ApplyVolume(AudioStreamPlayer3D player, MixBusId bus, Position listener)
    {
        ArgumentNullException.ThrowIfNull(player);
        var distance = SoundDirector.Distance(new Position(player.Position.X, player.Position.Z), listener);
        player.VolumeDb = (float)SoundMixer.ToDb(_mixer!.Gain(bus, distance, Zoom));
    }

    /// <summary>
    /// Smoke run only: one player moved away from the listener gets quieter and is muted beyond the
    /// range; zooming out makes it audible again (B2512).
    /// </summary>
    /// <returns>Empty if fine, otherwise the problem.</returns>
    public string SpatialSelfCheck()
    {
        if (_mixer is null)
        {
            return "mixer not set up";
        }

        var probe = new AudioStreamPlayer3D { AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled };
        AddChild(probe);
        try
        {
            var range = _mixer.SilentDistance(MixBusId.Effects, Zoom);
            var volumes = new List<float>();
            foreach (var fraction in new[] { 0.0, 0.25, 0.5, 0.75, 1.0, 1.5 })
            {
                probe.Position = new Vector3((float)(range * fraction), 0, 0);
                ApplyVolume(probe, MixBusId.Effects, new Position(0, 0));
                volumes.Add(probe.VolumeDb);
            }

            if (volumes.Zip(volumes.Skip(1)).Any(p => p.Second > p.First))
            {
                return "volume rises with distance: " + string.Join(", ", volumes);
            }

            if (volumes[^1] > SoundMixer.SilentDb + 0.01 || volumes[^2] > SoundMixer.SilentDb + 0.01)
            {
                return "not silent beyond the range";
            }

            var zoom = Zoom;
            Zoom = 1;
            probe.Position = new Vector3((float)range * 1.1f, 0, 0);
            ApplyVolume(probe, MixBusId.Effects, new Position(0, 0));
            Zoom = zoom;
            return probe.VolumeDb > SoundMixer.SilentDb ? string.Empty : "zooming out does not widen the hearing range";
        }
        finally
        {
            probe.QueueFree();
        }
    }

    private Position Listener()
    {
        var camera = GetViewport()?.GetCamera3D();
        return camera is null ? new Position(0, 0) : new Position(camera.GlobalPosition.X, camera.GlobalPosition.Z);
    }

    /// <summary>
    /// Sets the bus volumes: the player's choice from the settings (B6 R4), otherwise the default of the
    /// mix data. Buses that do not exist yet (no world audio) are skipped.
    /// </summary>
    public static void ApplyVolumes(SoundMix mix, IReadOnlyDictionary<string, double> playerDb)
    {
        ArgumentNullException.ThrowIfNull(mix);
        ArgumentNullException.ThrowIfNull(playerDb);
        foreach (var bus in mix.Buses)
        {
            var index = AudioServer.GetBusIndex(BusName(bus.Id));
            if (index >= 0)
            {
                var db = playerDb.TryGetValue(Ui.Screens.SettingsWindow.BusKey(bus.Id), out var chosen) ? chosen : bus.VolumeDb;
                AudioServer.SetBusVolumeDb(index, (float)db);
            }
        }
    }

    /// <summary>
    /// The sound to play: a file of the base library if the id has one (recorded or generated, B8-4),
    /// otherwise the retro synth. Event sounds play once; loops of the library repeat only where an
    /// ambience player asks for it (A1350).
    /// </summary>
    private AudioStreamWav Stream(string soundId, int variant)
    {
        var file = _baseSounds?.File(soundId, variant);
        if (file is not null)
        {
            if (!_fileStreams.TryGetValue(file, out var stream))
            {
                stream = GD.Load<AudioStreamWav>(BaseFolder + file);
                _fileStreams[file] = stream;
            }

            if (stream is not null)
            {
                return stream;
            }
        }

        return RetroAudio.Stream(soundId, variant, _worldSeed);
    }

    /// <summary>Smoke run only: every sound of the base library plays from its file, not from the synth.</summary>
    /// <returns>Empty if fine, otherwise the problem.</returns>
    public string BaseSelfCheck()
    {
        if (_baseSounds is null)
        {
            return "base library not loaded";
        }

        foreach (var sound in _baseSounds.Library.Sounds)
        {
            for (var variant = 0; variant < sound.Files.Count; variant++)
            {
                var stream = Stream(sound.Id, variant);
                if (stream.ResourcePath != BaseFolder + _baseSounds.File(sound.Id, variant) || stream.GetLength() <= 0)
                {
                    return $"{sound.Id} variant {variant} does not play its file";
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Reads the base library data; without it every sound comes from the retro synth.</summary>
    private static BaseSounds? LoadBaseSounds()
    {
        try
        {
            return new BaseSounds(GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<BaseSoundLibrary>(Read("content.sound.library.json")));
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushWarning($"audio: base library not loaded ({e.Message}); retro sounds only");
            return null;
        }
    }

    private static string BusName(MixBusId id) => id == MixBusId.Master ? "Master" : id.ToString();

    private static string Read(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
