// Listening folder for Marcel (TL decision W1644): every sound of the game as its own numbered WAV file
// plus LISTE.md in German (Nr | Datei | was es sein soll | Quelle/Lizenz | Bewertung).
// Usage: dotnet run --project tools/SoundExport -- <out folder> [extra MOSS wav files...]
// Library files are copied as they are; synth recipes, birdsong, music and murmur are rendered from the
// data exactly as the game renders them (same code, same seeds).
using System.Globalization;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("out folder missing");
var root = FindRoot();
var content = Path.Combine(root, "content");
var soundData = Path.Combine(content, "sound");
Directory.CreateDirectory(output);
// Only the tool's own files (NNN_name.wav and LISTE.md) are replaced; anything else in the folder stays (review #106).
foreach (var old in Directory.GetFiles(output, "*.wav").Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^\d{3}_.+\.wav$")))
{
    File.Delete(old);
}

var library = ContractSerializer.Deserialize<BaseSoundLibrary>(File.ReadAllText(Path.Combine(soundData, "library.json")));
var origins = library.Origins.ToDictionary(o => o.Id, o => $"{o.Name}, {o.License}", StringComparer.Ordinal);
// With the library's loudness target, exactly as the game renders them (RetroAudio): the first folder missed it.
var map = SoundLibrary.FromJson(File.ReadAllText(Path.Combine(soundData, "sound_map.json")), File.ReadAllText(Path.Combine(soundData, "synth_recipes.json")), loudness: library.Target);
var used = map.UsedSoundIds().ToHashSet(StringComparer.Ordinal);
var rows = new List<string>();
var number = 0;

string Next(string name) => $"{++number:000}_{name}.wav";

void Write(string file, short[] pcm, int rate) =>
    File.WriteAllBytes(Path.Combine(output, file), Pcm.WriteWav16(new MonoSound([.. pcm.Select(s => s / 32768f)], rate)));

void Row(string file, string what, string source) => rows.Add($"| {number} | {file} | {what} | {source} | |");

// 1. Base library (recorded or made sounds, as files in the game).
rows.Add("| | **Grundbibliothek (Dateien im Spiel)** | | | |");
foreach (var sound in library.Sounds.OrderBy(s => s.Id, StringComparer.Ordinal))
{
    for (var v = 0; v < sound.Files.Count; v++)
    {
        var src = Path.Combine(root, "game", "assets", "sound", "base", sound.Files.ElementAt(v).File);
        var file = Next($"{sound.Id}_v{v + 1}");
        File.Copy(src, Path.Combine(output, file), overwrite: true);
        var inGame = used.Contains(sound.Id) ? string.Empty : " (im Spiel derzeit nicht verwendet)";
        Row(file, German(sound.Id) + inGame, origins.GetValueOrDefault(sound.Origin, sound.Origin));
    }
}

// 2. Retro synthesis: every used sound without a library file (this is what the game plays for them).
rows.Add("| | **Retro-Synthese (im Spiel erzeugt, kein Aufnahme-Ton)** | | | |");
var libraryIds = library.Sounds.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
foreach (var id in used.Where(id => !libraryIds.Contains(id)).Order(StringComparer.Ordinal))
{
    var file = Next(id);
    Write(file, map.Render(id, 0, 1), map.SampleRate);
    Row(file, German(id), "prozedural aus content/sound/synth_recipes.json, eigene Arbeit");
}

// 3. Songbird melodies (each call its own seed).
rows.Add("| | **Vogelgesang (prozedural, jede Melodie anders)** | | | |");
var animals = ContractSerializer.Deserialize<AnimalSoundSettings>(File.ReadAllText(Path.Combine(soundData, "animals.json")));
foreach (var seed in new ulong[] { 1, 2, 3 })
{
    var file = Next($"vogelgesang_{seed}");
    Write(file, Birdsong.Render(animals.Birdsong, map.SampleRate, seed), map.SampleRate);
    Row(file, $"Singvogel, Melodie {seed}", "prozedural aus content/sound/animals.json, eigene Arbeit");
}

// 4. Music: one piece per mood (30 s).
rows.Add("| | **Musik (prozedural, 30 s je Stimmung)** | | | |");
var music = ContractSerializer.Deserialize<MusicSettings>(File.ReadAllText(Path.Combine(content, "music", "music.json")));
foreach (var mood in music.Moods)
{
    var file = Next($"musik_{mood.Id}");
    Write(file, MusicComposer.Render(music, mood, 1, 30), (int)music.SampleRate);
    Row(file, "Musik: " + mood.Id switch { "dawn" => "Morgendämmerung", "day" => "Tag", "dusk" => "Abenddämmerung", "night" => "Nacht", _ => mood.Id }, "prozedural aus content/music/music.json, eigene Arbeit");
}

// 5. Murmur (heard with the speech bubbles, A1342; not in the test build yet).
rows.Add("| | **Gemurmel (Stimme ohne Wörter; im Testbuild noch nicht zu hören)** | | | |");
var murmur = new Murmur(ContractSerializer.Deserialize<MurmurSettings>(File.ReadAllText(Path.Combine(content, "voice", "murmur.json"))));
foreach (var (who, sex, label) in new[] { ("inh-000001", "female", "Bewohnerin 1"), ("inh-000002", "male", "Bewohner 2") })
{
    foreach (var (text, kind) in new[] { ("Wo ist das Wasser?", "Frage"), ("Hier ist es ruhig und schön.", "ruhiger Satz") })
    {
        var file = Next($"gemurmel_{who}_{(kind == "Frage" ? "frage" : "ruhig")}");
        Write(file, murmur.Render(murmur.Plan(who, murmur.VoiceOf(sex, murmur.PlaceholderBody(who)), text)), murmur.SampleRate);
        Row(file, $"Gemurmel {label}, {kind} („{text}“)", "prozedural aus content/voice/murmur.json, eigene Arbeit");
    }
}

// 6. Extra MOSS samples (made live for inventions, D471 D7).
if (args.Length > 1)
{
    rows.Add("| | **MOSS-Proben (für Erfindungen erzeugt)** | | | |");
    foreach (var extra in args[1..])
    {
        var stem = Path.GetFileNameWithoutExtension(extra).Replace("snd-", string.Empty, StringComparison.Ordinal);
        var file = Next("moss_" + stem[..Math.Min(8, stem.Length)]);
        File.Copy(extra, Path.Combine(output, file), overwrite: true);
        Row(file, "Erfindung „Holztür knarrt“ (Live-Lauf D7, Beschreibung „wooden door creaks“)", origins.GetValueOrDefault("moss", "MOSS-SoundEffect, MIT"));
    }
}

var list = new StringBuilder();
list.AppendLine("# GlassWorld – Hörprobe (jedes Geräusch einzeln)");
list.AppendLine();
list.AppendLine(CultureInfo.InvariantCulture, $"Stand: {DateTime.Now:dd.MM.yyyy HH:mm}. {number} Dateien. Bitte jede Datei einzeln anhören und in der Spalte **Bewertung** eintragen:");
list.AppendLine("**1 = passt**, **2 = geht so**, **3 = weg damit**, gern mit einem Wort dazu (z. B. „zu laut“, „klingt nach Piepser“).");
list.AppendLine();
list.AppendLine("Lautstärke und Mischung im Spiel sind getrennt davon (seit #106 ruhig voreingestellt, Umgebung/Tiere zuschaltbar).");
list.AppendLine();
list.AppendLine("| Nr | Datei | was es sein soll | Quelle/Lizenz | Bewertung |");
list.AppendLine("|---|---|---|---|---|");
foreach (var row in rows)
{
    list.AppendLine(row);
}

File.WriteAllText(Path.Combine(output, "LISTE.md"), list.ToString(), new UTF8Encoding(false));
Console.WriteLine($"EXPORT_OK {number} files -> {output}");

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new DirectoryNotFoundException("GlassWorld.slnx not found");
}

static string German(string id) => id switch
{
    "action.rustle" => "Rascheln (Bewohner streift durchs Gras, beim Umherstreifen)",
    "action.combine" => "Kombinieren (zwei Dinge zusammenfügen)",
    "action.drink" => "Trinken",
    "action.drop" => "Etwas ablegen",
    "action.eat" => "Essen",
    "action.inspect" => "Etwas genau ansehen",
    "action.pick_up" => "Etwas aufheben",
    "action.talk_to" => "Jemanden ansprechen",
    "ambience.leaves" => "Umgebung: Blätterrauschen",
    "ambience.stream" => "Umgebung: Bach",
    "ambience.waves" => "Umgebung: Meereswellen",
    "ambience.wind" => "Umgebung: Wind",
    "ambience.forest" => "Umgebung: Wald",
    "ambience.meadow" => "Umgebung: Wiese",
    "ambience.rocks" => "Umgebung: Felsen",
    "ambience.swamp" => "Umgebung: Sumpf",
    "animal.gull.call" => "Möwe ruft",
    "animal.songbird.song" => "Singvogel (Aufnahme)",
    "animal.bee.buzz" => "Biene summt",
    "animal.butterfly.wings" => "Schmetterling (Flügelschlag)",
    "animal.crab.click" => "Krebs klickt",
    "animal.dragonfly.wings" => "Libelle (Flügelschwirren)",
    "animal.firefly.shimmer" => "Glühwürmchen (Schimmern)",
    "animal.fish.splash" => "Fisch platscht",
    "animal.frog.croak" => "Frosch quakt",
    "animal.goat.bleat" => "Ziege meckert",
    "animal.owl.hoot" => "Eule ruft",
    "animal.squirrel.chatter" => "Eichhörnchen keckert",
    "animal.wild_chicken.cluck" => "Wildhuhn gackert",
    "object.chop_wood" => "Holz hacken",
    "object.creak" => "Knarren",
    "object.fire" => "Feuer knistert",
    "object.knock_stone" => "Klopfen auf Stein",
    "object.knock_wood" => "Klopfen auf Holz",
    "object.strike_spark" => "Funken schlagen",
    "step.grass" => "Schritte auf Gras",
    "step.rock" => "Schritte auf Fels",
    "step.sand" => "Schritte auf Sand",
    "step.snow" => "Schritte im Schnee",
    "step.water" => "Schritte im Wasser",
    "step.wood" => "Schritte auf Holz",
    "weather.clear" => "Wetter: klar",
    "weather.cloudy" => "Wetter: bewölkt",
    "weather.fog" => "Wetter: Nebel",
    "weather.frost" => "Wetter: Frost",
    "weather.heat" => "Wetter: Hitze",
    "weather.rain" => "Wetter: Regen",
    "weather.snow" => "Wetter: Schnee",
    "weather.storm" => "Wetter: Sturm",
    _ => id,
};
