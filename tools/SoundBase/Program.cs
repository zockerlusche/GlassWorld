using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Presentation.Audio;

// Builds the base sound library (B8-4, B2530):
//   dotnet run --project tools/SoundBase -- <selection.json> <source-dir> <out-wav-dir> <library.json>
// <source-dir> holds the downloaded CC0 archives (paths in the selection) and the generated WAVs.
// Every file is mixed to mono, single sounds are trimmed to their audible part, the loudness is
// normalised to the target of its kind (capped by the peak limit) and written as 16-bit WAV.

if (args.Length != 4)
{
    Console.Error.WriteLine("usage: SoundBase <selection.json> <source-dir> <out-wav-dir> <library.json>");
    return 2;
}

var selection = JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
var sourceDir = args[1];
var outDir = args[2];
Directory.CreateDirectory(outDir);
var target = selection["target"]!;
var gate = target["gate_dbfs"]!.GetValue<double>();
var peakMax = target["peak_max_dbfs"]!.GetValue<double>();
var origins = selection["origins"]!.AsArray().ToDictionary(o => o!["id"]!.GetValue<string>(), o => o!);

var sounds = new JsonArray();
foreach (var entry in selection["sounds"]!.AsArray())
{
    var id = entry!["id"]!.GetValue<string>();
    var kind = entry["kind"]!.GetValue<string>();
    var origin = origins[entry["origin"]!.GetValue<string>()];
    var archive = origin["archive"]?.GetValue<string>();
    var wanted = target[kind == "loop" ? "loop_dbfs" : "one_shot_dbfs"]!.GetValue<double>();
    var files = new JsonArray();
    var inputs = entry["inputs"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
    for (var n = 0; n < inputs.Count; n++)
    {
        var sound = Load(sourceDir, archive, inputs[n]);
        if (kind == "one_shot")
        {
            sound = Trim(sound, gate);
        }

        var loudness = Pcm.LoudnessDbfs(sound, gate);
        var gain = Math.Min(wanted - loudness, peakMax - Pcm.PeakDbfs(sound));
        var factor = (float)Math.Pow(10, gain / 20);
        var normalised = new MonoSound([.. sound.Samples.Select(s => s * factor)], sound.SampleRate);
        var name = inputs.Count == 1 ? $"{id}.wav" : $"{id}_{n}.wav";
        var bytes = Pcm.WriteWav16(normalised);
        File.WriteAllBytes(Path.Combine(outDir, name), bytes);
        var back = Pcm.ReadWav(bytes);
        Console.WriteLine($"{name,-32} {back.Seconds,5:0.00} s  loud {Pcm.LoudnessDbfs(back, gate),6:0.0}  peak {Pcm.PeakDbfs(back),6:0.0}  gain {gain,6:0.0}");
        files.Add(new JsonObject
        {
            ["file"] = name,
            ["source"] = inputs[n],
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["prompt"] = entry["prompt"]?.DeepClone(),
            ["seed"] = entry["seed"]?.DeepClone(),
        });
    }

    sounds.Add(new JsonObject
    {
        ["id"] = id,
        ["kind"] = kind,
        ["origin"] = origin["id"]!.GetValue<string>(),
        ["sound_words"] = entry["sound_words"]!.DeepClone(),
        ["context_words"] = entry["context_words"]!.DeepClone(),
        ["files"] = files,
    });
}

var library = new JsonObject
{
    ["contract_version"] = "0.2",
    ["fallback_min_score"] = selection["fallback_min_score"]!.DeepClone(),
    ["target"] = target.DeepClone(),
    ["origins"] = new JsonArray([.. origins.Values.Select(o => (JsonNode)new JsonObject
    {
        ["id"] = o["id"]!.DeepClone(),
        ["name"] = o["name"]!.DeepClone(),
        ["url"] = o["url"]!.DeepClone(),
        ["license"] = o["license"]!.DeepClone(),
        ["archive_sha256"] = o["archive_sha256"]?.DeepClone(),
    })]),
    ["sounds"] = new JsonArray([.. sounds.OrderBy(s => s!["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(s => s!.DeepClone())]),
};
File.WriteAllText(args[3], library.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
Console.WriteLine($"LIBRARY_OK sounds={sounds.Count}");
return 0;

static MonoSound Load(string sourceDir, string? archive, string input)
{
    if (archive is null)
    {
        return Pcm.ReadWav(File.ReadAllBytes(Path.Combine(sourceDir, input)));
    }

    using var zip = ZipFile.OpenRead(Path.Combine(sourceDir, archive));
    var zipEntry = zip.GetEntry(input) ?? throw new FileNotFoundException($"'{input}' not in {archive}");
    using var stream = new MemoryStream();
    using (var source = zipEntry.Open())
    {
        source.CopyTo(stream);
    }

    stream.Position = 0;
    using var vorbis = new NVorbis.VorbisReader(stream, closeOnDispose: false);
    var interleaved = new List<float>();
    var buffer = new float[4096 * vorbis.Channels];
    int read;
    while ((read = vorbis.ReadSamples(buffer, 0, buffer.Length)) > 0)
    {
        interleaved.AddRange(buffer.AsSpan(0, read).ToArray());
    }

    var frames = interleaved.Count / vorbis.Channels;
    var mono = new float[frames];
    for (var f = 0; f < frames; f++)
    {
        float sum = 0;
        for (var c = 0; c < vorbis.Channels; c++)
        {
            sum += interleaved[(f * vorbis.Channels) + c];
        }

        mono[f] = sum / vorbis.Channels;
    }

    return new MonoSound(mono, vorbis.SampleRate);
}

static MonoSound Trim(MonoSound sound, double gateDbfs)
{
    // Keep the audible part (frames above the gate) with 10 ms before and a 20 ms fade after.
    var frame = sound.SampleRate / 100;
    var level = (float)Math.Pow(10, gateDbfs / 20);
    var first = Array.FindIndex(sound.Samples, s => Math.Abs(s) > level);
    var last = Array.FindLastIndex(sound.Samples, s => Math.Abs(s) > level);
    if (first < 0)
    {
        return sound;
    }

    var start = Math.Max(0, first - frame);
    var end = Math.Min(sound.Samples.Length, last + (2 * frame));
    var samples = sound.Samples[start..end];
    var fade = Math.Min(2 * frame, samples.Length);
    for (var i = 0; i < fade; i++)
    {
        samples[samples.Length - 1 - i] *= (float)i / fade;
    }

    return new MonoSound(samples, sound.SampleRate);
}
