using System.Reflection;
using GlassWorld.Contracts;
using GlassWorld.Presentation.Audio;
using GlassWorld.Simulation;
using Godot;

namespace GlassWorld.Game.Audio;

/// <summary>
/// Background music (B8 R6): <see cref="MusicDirector"/> picks the mood from the world's time of day,
/// season and epoch; two players on the music bus cross-fade when the mood changes. Pieces are rendered
/// off the main thread (generative, from content/music/music.json). Real seconds drive it, so pausing
/// the world does not stop the music. Calm: no alarm, no time pressure (B2466).
/// </summary>
public partial class WorldMusic : Node
{
    /// <summary>Epoch until the core keeps one (B9/B10): the first epoch of the look data.</summary>
    public const string DefaultEpoch = "gathering";

    private readonly AudioStreamPlayer[] _players = new AudioStreamPlayer[2];
    private readonly (double From, double To, double Start, double Length)[] _fades = new (double, double, double, double)[2];
    private MusicSettings? _settings;
    private MusicDirector? _director;
    private SimulationHost? _host;
    private Task<(AudioStreamWav Stream, MusicCue Cue)>? _rendering;
    private int _active;
    private double _clock;

    /// <summary>Loads the data and creates the two players; false if the data is broken.</summary>
    public bool Setup(SimulationHost host, ulong worldSeed, string bus)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        try
        {
            _settings = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<MusicSettings>(Read("content.music.music.json"));
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushError($"music: {e.Message}");
            return false;
        }

        try
        {
            _director = new MusicDirector(_settings, worldSeed);
        }
        catch (InvalidDataException e)
        {
            GD.PushError($"music: {e.Message}");
            return false;
        }

        for (var i = 0; i < _players.Length; i++)
        {
            _players[i] = new AudioStreamPlayer { Bus = bus, VolumeDb = -80 };
            AddChild(_players[i]);
            _fades[i] = (0, 0, 0, 0);
        }

        return true;
    }

    /// <summary>The mood that should play now.</summary>
    public string? WantedMood => _host is null || _director is null ? null : _director.MoodAt(_host.World.Calendar.At(_host.World.Tick), DefaultEpoch);

    public override void _Process(double delta)
    {
        if (_director is null || _settings is null || WantedMood is not { } wanted)
        {
            return;
        }

        _clock += delta;
        for (var i = 0; i < _players.Length; i++)
        {
            var gain = Gain(i);
            _players[i].VolumeDb = gain <= 0.0001 ? -80 : (float)(20 * Math.Log10(gain));
        }

        if (_rendering is { IsCompleted: true } done)
        {
            _rendering = null;
            if (!done.IsCompletedSuccessfully)
            {
                GD.PushError($"music: a piece could not be rendered ({done.Exception?.GetBaseException().Message})");
            }
            else
            {
                var (stream, cue) = done.Result;
                _active = 1 - _active;
                var old = _players[_active].Stream;
                _players[_active].Stop();
                _players[_active].Stream = stream;
                old?.Dispose();
                _players[_active].Play();
                _fades[_active] = (0, 1, _clock, cue.FadeInS);
            }
        }

        if (_rendering is not null)
        {
            return;
        }

        var step = _director.Update(_clock, wanted);
        if (step.FadeOutS > 0)
        {
            _fades[_active] = (Gain(_active), 0, _clock, step.FadeOutS);
        }

        if (step.Start is { } next)
        {
            var settings = _settings;
            var mood = _director.Mood(next.Variant);
            _rendering = Task.Run(() => (Stream(MusicComposer.Render(settings, mood, next.Seed, next.Seconds), (int)settings.SampleRate), next));
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree() => Silence();

    /// <summary>
    /// Stops the players, frees their streams and drops a piece still rendering, so nothing is left at
    /// exit (review #92). Call it a frame before quitting: the audio server lets go of a stopped
    /// playback only in its next mix step.
    /// </summary>
    public void Silence()
    {
        if (_rendering is { } pending)
        {
            // A piece still rendering is dropped; its stream is freed as soon as it is done.
            _ = pending.ContinueWith(t => t.Result.Stream.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            _rendering = null;
        }

        foreach (var player in _players.Where(p => p is not null))
        {
            player.Stop();
            var stream = player.Stream;
            player.Stream = null;
            stream?.Dispose();
        }
    }

    /// <summary>
    /// Smoke run only: a piece of every mood renders to the planned length, and a change of the phase
    /// of the day changes the mood with a cross-fade.
    /// </summary>
    /// <returns>Empty if fine, otherwise the first problem.</returns>
    public string SelfCheck()
    {
        if (_settings is null || _director is null)
        {
            return "music not set up";
        }

        foreach (var mood in _settings.Moods)
        {
            using var stream = Stream(MusicComposer.Render(_settings, mood, 1, 4), (int)_settings.SampleRate);
            if (Math.Abs(stream.GetLength() - 4) > 0.01)
            {
                return $"piece of '{mood.Id}' is {stream.GetLength():0.00} s";
            }
        }

        var check = new MusicDirector(_settings, 1);
        var day = check.MoodFor("day", "spring", DefaultEpoch);
        var night = check.MoodFor("night", "spring", DefaultEpoch);
        check.Update(0, day);
        var held = check.Update(1, night);
        var change = check.Update(_settings.MinMoodS, night);
        return day != night && held.Start is null && change.Start?.Mood == night && change.FadeOutS == _settings.FadeS
            ? string.Empty
            : $"no cross-fade from '{day}' to '{night}'";
    }

    private static AudioStreamWav Stream(short[] pcm, int rate)
    {
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = bytes };
    }

    private static string Read(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    private double Gain(int i)
    {
        var (from, to, start, length) = _fades[i];
        return length <= 0 ? to : from + ((to - from) * Math.Clamp((_clock - start) / length, 0, 1));
    }
}
