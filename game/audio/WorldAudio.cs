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
    private InventionSounds? _inventionSounds;
    private string? _useAction;
    private SoundMixer? _mixer;
    private SoundDirector? _director;
    private WorldFeed? _feed;
    private ulong _worldSeed;
    private WorldMusic? _music;
    private SimulationHost? _host;
    private AnimalCalls? _animals;
    private BirdsongSettings? _birdsong;
    private long _animalTick = -1;
    private CallBudget? _callBudget;
    private double _clock;
    private AmbienceDirector? _ambience;
    private readonly AudioStreamPlayer[] _ambiencePlayers = new AudioStreamPlayer[2];
    private readonly (double From, double To, double Start, double Length)[] _ambienceFades = new (double, double, double, double)[2];
    private int _ambienceActive;
    private readonly Dictionary<string, AudioStreamWav> _loopStreams = new(StringComparer.Ordinal);

    /// <summary>
    /// Ground height in metres at (x, z) for the sound positions (island mode: the drawn ground, so a sound
    /// comes from the figure's feet, not from below a hill); null = flat at 0 (placeholder).
    /// </summary>
    public Func<double, double, float>? GroundHeight { get; set; }

    /// <summary>
    /// Island mode: where the mix listens and the zoom (0..1), read every frame (the camera rig's target on
    /// the ground and its distance in the zoom range); null = the camera listens and Zoom stays as set.
    /// </summary>
    public Func<(Position Listener, double Zoom)?>? Hearing { get; set; }

    /// <summary>Zoom 0 (fully in) .. 1 (fully out); set by the camera control.</summary>
    public double Zoom { get; set; } = 0.5;

    /// <summary>The mixer in use (null until <see cref="Setup"/>).</summary>
    public SoundMixer? Mixer => _mixer;

    /// <summary>Loads the mix, creates buses and player pools and binds to a running world.</summary>
    /// <returns>False if the mix or sound data is invalid (logged).</returns>
    public bool Setup(SimulationHost host, ulong worldSeed, string? madeSounds = null)
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

        // A limiter on the master bus (B8 R7): many sounds at once never clip (Marcel W1643: "grauenhaft").
        var master = AudioServer.GetBusIndex(BusName(MixBusId.Master));
        if (master >= 0 && !Enumerable.Range(0, AudioServer.GetBusEffectCount(master)).Any(i => AudioServer.GetBusEffect(master, i) is AudioEffectHardLimiter))
        {
            AudioServer.AddBusEffect(master, new AudioEffectHardLimiter { CeilingDb = -1f });
        }

        _baseSounds = LoadBaseSounds();
        MurmurAudio.Samples = (soundId, variant) => _baseSounds?.File(soundId, variant) is null ? null : Stream(soundId, variant);
        _inventionSounds = new InventionSounds(host.Inventions, host.Objects, _baseSounds, madeSounds);
        _useAction = host.Content.Inventions.Build.Settings.UseAction;
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

        _host = host;
        try
        {
            var animals = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<AnimalSoundSettings>(Read("content.sound.animals.json"));
            _animals = new AnimalCalls(RetroAudio.Library!, animals);
            _callBudget = new CallBudget(animals.MaxCallsPerSecond);
            _birdsong = animals.Birdsong;
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            // Without the data the world stays quiet of animals; everything else still sounds.
            GD.PushError($"audio: {e.Message}");
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
            AudioServer.SetBusMute(index, bus.VolumeDb <= _mixer.Mix.PlayerVolumeDb.Min);
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

        _ambience = new AmbienceDirector(RetroAudio.Library!, _mixer.Mix.Ambience);
        for (var i = 0; i < 2; i++)
        {
            _ambiencePlayers[i] = new AudioStreamPlayer { Bus = BusName(MixBusId.Ambience), VolumeDb = -80 };
            AddChild(_ambiencePlayers[i]);
        }

        return true;
    }

    public override void _Process(double delta)
    {
        if (_feed is null || _director is null || _mixer is null)
        {
            return;
        }

        _clock += delta;
        var listener = Listener();
        foreach (var (bus, pool) in _pools)
        {
            foreach (var player in pool.Where(p => p.Playing))
            {
                ApplyVolume(player, bus, listener);
            }
        }

        PlayAnimals(listener);
        PlayAmbience(listener);
        var events = _feed.NewEvents();
        if (events.Count == 0)
        {
            return;
        }

        Speak(events, listener);

        var positions = _feed.Snapshot.Inhabitants.ToDictionary(i => i.Id, i => i.Position, StringComparer.Ordinal);
        var playing = _pools.ToDictionary(p => p.Key, p => p.Value.Count(x => x.Playing));
        var inventions = _inventionSounds;
        foreach (var request in _director.Plan(events, positions, listener, Zoom, playing, e => e.Action == _useAction ? inventions?.For(e.TargetId) : null))
        {
            var player = _pools[request.Bus].FirstOrDefault(p => !p.Playing);
            if (player is null)
            {
                continue;
            }

            player.Stream = request.File is { } made ? MadeStream(made) ?? Stream(request.SoundId, request.Variant) : Stream(request.SoundId, request.Variant);
            player.Position = At(request.Position);
            ApplyVolume(player, request.Bus, listener);
            player.Play();
        }
    }

    /// <summary>Stops the music before the game quits (see <see cref="WorldMusic.Silence"/>).</summary>
    public void Silence() => _music?.Silence();

    /// <summary>Smoke run only: the master bus has its limiter (B8 R7).</summary>
    public static string LimiterSelfCheck()
    {
        var master = AudioServer.GetBusIndex(BusName(MixBusId.Master));
        return master >= 0 && Enumerable.Range(0, AudioServer.GetBusEffectCount(master)).Any(i => AudioServer.GetBusEffect(master, i) is AudioEffectHardLimiter)
            ? string.Empty
            : "no limiter on the master bus";
    }

    /// <summary>Smoke run only: the background music (B8 R6).</summary>
    public string MusicSelfCheck() => _music?.SelfCheck() ?? "music not set up";

    /// <summary>
    /// Biome ambience (B8 R7): the loop of the biome under the listener on the ambience bus; a change of
    /// biome cross-fades over the mix data's fade time (two players, like the music).
    /// </summary>
    private void PlayAmbience(Position listener)
    {
        if (_ambience is null || _ambiencePlayers[0] is null)
        {
            return;
        }

        for (var i = 0; i < 2; i++)
        {
            var gain = CurrentGain(i);
            var to = _ambienceFades[i].To;
            _ambiencePlayers[i].VolumeDb = gain <= 0.0001 ? -80 : (float)(20 * Math.Log10(gain));
            if (gain <= 0.0001 && _ambiencePlayers[i].Playing && to <= 0)
            {
                _ambiencePlayers[i].Stop();
            }
        }

        var step = _ambience.Update(_clock, BiomeAt(listener));
        if (step.FadeS <= 0)
        {
            return;
        }

        var old = _ambienceActive;
        _ambienceFades[old] = (CurrentGain(old), 0, _clock, step.FadeS);
        if (step.Start is { } sound)
        {
            _ambienceActive = 1 - old;
            var player = _ambiencePlayers[_ambienceActive];
            player.Stream = LoopStream(sound);
            player.Play();
            _ambienceFades[_ambienceActive] = (0, 1, _clock, step.FadeS);
        }
    }

    private double CurrentGain(int i)
    {
        var (from, to, start, length) = _ambienceFades[i];
        return length <= 0 ? to : from + ((to - from) * Math.Clamp((_clock - start) / length, 0, 1));
    }

    /// <summary>
    /// A sound as an endless loop (library file or retro recipe). The cached one-shot stream is never
    /// changed: a loop gets its own copy, so the same sound played as an event stays a one-shot.
    /// </summary>
    private AudioStreamWav LoopStream(string soundId)
    {
        if (_loopStreams.TryGetValue(soundId, out var cached))
        {
            return cached;
        }

        var stream = Stream(soundId, 0);
        if (stream.LoopMode == AudioStreamWav.LoopModeEnum.Disabled)
        {
            stream = (AudioStreamWav)stream.Duplicate();
            stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            stream.LoopBegin = 0;
            stream.LoopEnd = (int)Math.Round(stream.GetLength() * stream.MixRate);
        }

        _loopStreams[soundId] = stream;
        return stream;
    }

    /// <summary>Smoke run only: beach and forest get their loops, and they loop.</summary>
    public string AmbienceSelfCheck()
    {
        if (_ambience is null)
        {
            return "ambience not set up";
        }

        foreach (var biome in new[] { "beach_sea", "forest" })
        {
            if (_ambience.SoundFor(biome) is not { } sound)
            {
                return $"no ambience for {biome}";
            }

            var oneShot = Stream(sound, 0);
            var wasLooping = oneShot.LoopMode != AudioStreamWav.LoopModeEnum.Disabled;
            if (LoopStream(sound).LoopMode == AudioStreamWav.LoopModeEnum.Disabled)
            {
                return $"ambience {sound} does not loop";
            }

            if (!wasLooping && Stream(sound, 0).LoopMode != AudioStreamWav.LoopModeEnum.Disabled)
            {
                return $"ambience {sound} changed the cached one-shot stream";
            }
        }

        return string.Empty;
    }

    /// <summary>The biome under a ground position, or null without a map or outside it.</summary>
    public string? BiomeAt(Position position)
    {
        if (_host?.Map is not { } map)
        {
            return null;
        }

        var x = (int)Math.Floor(position.X * 100 / map.CellSizeCm);
        var z = (int)Math.Floor(position.Z * 100 / map.CellSizeCm);
        return map.Map.Contains(x, z) ? map.Map.BiomeAt(x, z) : null;
    }

    /// <summary>
    /// Animal calls since the last frame near the listener (B8 R4). After a jump (fast forward, loading)
    /// only the last game hour sounds; a full animal bus skips calls (voice limit, B8 R7).
    /// </summary>
    private void PlayAnimals(Position listener)
    {
        if (_host is null || _animals is null || !_pools.TryGetValue(MixBusId.Animals, out var pool))
        {
            return;
        }

        var now = _host.World.Tick;
        var perHour = _host.World.Calendar.Settings.TicksPerHour;
        var from = _animalTick < 0 ? now : Math.Max(_animalTick, now - perHour);
        _animalTick = now;
        if (now <= from)
        {
            return;
        }

        var calendar = _host.World.Calendar;
        foreach (var call in _animals.Between(from, now, perHour, t => HourTime.Of(calendar.At(t)), BiomeAt(listener), listener, _worldSeed))
        {
            var player = pool.FirstOrDefault(p => !p.Playing);
            if (player is null)
            {
                return;
            }

            if (_callBudget is not null && !_callBudget.TryTake(_clock))
            {
                continue;
            }

            // A sung call is a synthesised melody or, by the data's share, a recorded song (both variants, W1651).
            player.Stream = call.Sung && _birdsong is not null && !Birdsong.Recorded(_birdsong, call.Seed) ? Song(call.Seed)
                : Stream(call.SoundId, call.Sung ? (int)(call.Seed % 1024) : call.Variant);
            player.Position = At(call.Position);
            ApplyVolume(player, MixBusId.Animals, listener);
            player.Play();
        }
    }

    /// <summary>A songbird melody for one call (each call its own seed, B8 R4).</summary>
    private AudioStreamWav Song(ulong seed)
    {
        var rate = RetroAudio.Library!.SampleRate;
        var pcm = Birdsong.Render(_birdsong!, rate, seed);
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = bytes };
    }

    /// <summary>
    /// Smoke run only: over one game day in the forest and on the meadow, animals call near the listener
    /// and the songbird sings a melody of plausible length.
    /// </summary>
    /// <returns>Empty if fine, otherwise the first problem.</returns>
    public string AnimalSelfCheck()
    {
        if (_host is null || _animals is null || _birdsong is null)
        {
            return "animal sounds not set up";
        }

        var calendar = _host.World.Calendar;
        var heard = new HashSet<string>(StringComparer.Ordinal);
        foreach (var biome in new[] { "forest", "meadow" })
        {
            foreach (var call in _animals.Between(0, calendar.TicksPerDay, calendar.Settings.TicksPerHour, t => HourTime.Of(calendar.At(t)), biome, new Position(0, 0), _worldSeed))
            {
                heard.Add(call.Animal);
            }
        }

        if (!heard.Contains("songbird") || heard.Count < 3)
        {
            return "too few animals over a day: " + string.Join(", ", heard.Order(StringComparer.Ordinal));
        }

        var song = Song(1);
        return song.GetLength() is > 0.1 and < 5 ? string.Empty : $"songbird melody is {song.GetLength():0.00} s";
    }

    /// <summary>A sound position on the ground (the island's height in island mode).</summary>
    private Vector3 At(Position position) =>
        new((float)position.X, GroundHeight?.Invoke(position.X, position.Z) ?? 0, (float)position.Z);

    /// <summary>
    /// The murmur of what an inhabitant says to the player (B6 R13, B8 R5): words are only in the speech
    /// bubble, the ear hears a murmur without words in the inhabitant's voice.
    /// </summary>
    private void Speak(IReadOnlyList<Simulation.Inhabitants.WorldEvent> events, Position listener)
    {
        if (MurmurAudio.Voice is null || !_pools.TryGetValue(MixBusId.Voices, out var pool))
        {
            return;
        }

        foreach (var e in events)
        {
            if (e.Kind is not ("said" or "declined") || e.Text is not { Length: > 0 } text
                || _feed!.Snapshot.Inhabitants.FirstOrDefault(i => i.Id == e.InhabitantId) is not { } speaker
                || pool.FirstOrDefault(p => !p.Playing) is not { } player)
            {
                continue;
            }

            player.Stream = MurmurAudio.Stream(speaker.Id, speaker.Sex, text);
            player.Position = At(speaker.Position);
            ApplyVolume(player, MixBusId.Voices, listener);
            player.Play();
        }
    }

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
        if (Hearing?.Invoke() is { } hearing)
        {
            Zoom = hearing.Zoom;
            return hearing.Listener;
        }

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

                // The slider at its minimum means off (test feedback F5286: music off by default).
                AudioServer.SetBusMute(index, db <= mix.PlayerVolumeDb.Min);
            }
        }
    }

    /// <summary>A made invention sound from disk (D471 D7), cached; null (and a warning) if it cannot be read.</summary>
    private AudioStreamWav? MadeStream(string path)
    {
        if (_fileStreams.TryGetValue(path, out var cached))
        {
            return cached;
        }

        AudioStreamWav? stream = null;
        try
        {
            var sound = Pcm.ReadWav(System.IO.File.ReadAllBytes(path));
            var bytes = new byte[sound.Samples.Length * 2];
            for (var i = 0; i < sound.Samples.Length; i++)
            {
                var value = (short)Math.Round(Math.Clamp(sound.Samples[i], -1f, 1f) * short.MaxValue);
                bytes[i * 2] = (byte)(value & 0xff);
                bytes[(i * 2) + 1] = (byte)((value >> 8) & 0xff);
            }

            stream = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = sound.SampleRate, Stereo = false, Data = bytes };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PushWarning($"audio: invention sound {System.IO.Path.GetFileName(path)} not readable ({e.Message}); the action sound plays");
        }

        _fileStreams[path] = stream;
        return stream;
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
