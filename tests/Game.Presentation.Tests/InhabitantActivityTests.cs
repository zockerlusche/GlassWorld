using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.View;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Marcel W1687, W1691: the inhabitant list says in words what an inhabitant does (never a bare "?"), a show
/// towards another inhabitant does not bend down, and two inhabitants of one action turn to each other.
/// </summary>
public sealed class InhabitantActivityTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Catalogs = new(() =>
        new[] { "de", "en" }.ToDictionary(l => l, l => (IReadOnlyDictionary<string, string>)JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", l + ".json")))
            .RootElement.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("text").GetString()!, StringComparer.Ordinal)));

    private static readonly GaitSettings Gaits =
        ContractSerializer.Deserialize<GaitSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "behaviour", "gaits.json")));

    [Theory]
    [InlineData("de", "zeigt Bewohner 2: Quellwasser")]
    [InlineData("en", "is showing Inhabitant 2: Spring water")]
    public void ShowingAnotherInhabitantNamesWhomAndWhat(string locale, string expected)
    {
        var snapshot = Snapshot(Inhabitant("inh-000001", "show", "inh-000002") with { Topic = "spring_water" }, Inhabitant("inh-000002", null, null));
        Assert.Equal(expected, Describe(snapshot.Inhabitants[0], snapshot, locale), ignoreCase: true);
    }

    [Theory]
    [InlineData("de", "spricht mit Bewohner 2", "steht da", "denkt nach")]
    [InlineData("en", "is talking with Inhabitant 2", "is standing", "is thinking")]
    public void EveryStateHasReadableWords(string locale, string talking, string standing, string thinking)
    {
        var talker = Inhabitant("inh-000001", "talk_to", "inh-000002");
        var idle = Inhabitant("inh-000002", null, null);
        var waiting = Inhabitant("inh-000003", null, null) with { OpenQuestionIds = ["q-1"] };
        var snapshot = Snapshot(talker, idle, waiting);
        Assert.Equal(talking, Describe(talker, snapshot, locale));
        Assert.Equal(standing, Describe(idle, snapshot, locale));
        Assert.Equal(thinking, Describe(waiting, snapshot, locale));
    }

    [Fact]
    public void EveryActionOfTheVocabularyGivesWordsNotAKeyOrASymbol()
    {
        var actions = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "vocabulary", "actions.json")))
            .RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToList();
        var water = new WorldObject("obj-000001", "spring_water", new Position(1, 0));
        foreach (var locale in new[] { "de", "en" })
        {
            foreach (var action in actions)
            {
                foreach (var target in new string?[] { null, "inh-000002", water.Id, "gone" })
                {
                    var snapshot = Snapshot([Inhabitant("inh-000001", action, target), Inhabitant("inh-000002", null, null)], [water]);
                    var text = Describe(snapshot.Inhabitants[0], snapshot, locale);
                    Assert.False(Ui.TextKeys.LooksLikeKey(text), $"{locale} {action} {target}: {text}");
                    Assert.DoesNotContain("{", text, StringComparison.Ordinal);
                    Assert.True(text.Count(char.IsLetter) >= 3, $"{locale} {action} {target}: '{text}'");
                }
            }
        }
    }

    [Fact]
    public void ShowingToAnInhabitantTalksAndOnlyAThingOnTheGroundBendsDown()
    {
        var library = ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "animation", "library.json")));
        var chooser = new AnimationChooser(library);
        var toInhabitant = chooser.ForInhabitant("show", false, "inh-000001", 0, new Dictionary<string, double>(), AnimationMappingTarget.Inhabitant);
        var toThing = chooser.ForInhabitant("show", false, "inh-000001", 0, new Dictionary<string, double>(), AnimationMappingTarget.Object);
        Assert.Equal("talk", toInhabitant.Clip.ClipId);
        Assert.Equal(chooser.ForAction("show").ClipId, toThing.Clip.ClipId);
        Assert.NotEqual(toInhabitant.Clip.Asset, toThing.Clip.Asset);
        foreach (var action in new[] { "show", "warn", "teach", "talk_to" })
        {
            Assert.Equal("Idle_Talking", chooser.ForAction(action, AnimationMappingTarget.Inhabitant).Asset);
        }
    }

    [Fact]
    public void TargetKindTellsInhabitantsFromThings()
    {
        var all = new[] { Inhabitant("inh-000001", "show", "inh-000002"), Inhabitant("inh-000002", "eat", "obj-000001"), Inhabitant("inh-000003", null, null) };
        Assert.Equal(AnimationMappingTarget.Inhabitant, InhabitantActivity.TargetKind(all[0], all));
        Assert.Equal(AnimationMappingTarget.Object, InhabitantActivity.TargetKind(all[1], all));
        Assert.Null(InhabitantActivity.TargetKind(all[2], all));
    }

    [Fact]
    public void BothTurnTheirWholeBodyToEachOtherWithinTheTolerance()
    {
        // The core says who faces whom (InhabitantsSystem.FacingOf, tested in the core); the figures turn.
        var actor = Inhabitant("inh-000001", "show", "inh-000002", new Position(0, 0)) with { Facing = "inh-000002" };
        var listener = Inhabitant("inh-000002", null, null, new Position(1.2, 0.4)) with { Facing = "inh-000001" };
        var all = new[] { actor, listener };
        foreach (var (me, other) in new[] { (actor, listener), (listener, actor) })
        {
            var motion = new FigureMotion(new Point3(me.Position.X, 0, me.Position.Z), 180);
            motion.Retarget(new Point3(me.Position.X, 0, me.Position.Z), 1, 60, 0.8, 0.05);
            var face = InhabitantActivity.FacePoint(me, all);
            Assert.Equal(other.Position, face);
            motion.Face(face!.Value.X, face.Value.Z, 0.05);
            motion.Advance(2);
            var wanted = Math.Atan2(other.Position.X - me.Position.X, other.Position.Z - me.Position.Z);
            var off = Math.Abs(Math.IEEERemainder(motion.Yaw - wanted, 2 * Math.PI)) * 180 / Math.PI;
            Assert.True(off <= Gaits.Spacing!.FacingToleranceDeg, $"{me.Id} looks {off:0.0} degrees away");
        }
    }

    [Fact]
    public void AWalkingFigureDoesNotTurnAway()
    {
        var motion = new FigureMotion(new Point3(0, 0, 0), 180);
        motion.Retarget(new Point3(0, 0, 1), 1, 60, 0.8, 0.05);
        motion.Face(0, -5, 0.05);
        motion.Advance(2);
        Assert.InRange(motion.Yaw, -1e-9, 1e-9);
    }

    [Fact]
    public void TheNameComesFromTheScreen()
    {
        var snapshot = Snapshot(Inhabitant("inh-000001", "talk_to", "inh-000002"), Inhabitant("inh-000002", null, null));
        var text = InhabitantActivity.Describe(snapshot.Inhabitants[0], new SnapshotIndex(snapshot), Tr("en"), id => id == "inh-000002" ? "Toma" : "nobody");
        Assert.Equal("is talking with Toma", text);
    }

    /// <summary>Describes with the unnamed-inhabitant name the screen shows until names exist (B9).</summary>
    private static string Describe(InhabitantSnapshot inhabitant, WorldSnapshot snapshot, string locale) =>
        InhabitantActivity.Describe(inhabitant, new SnapshotIndex(snapshot), Tr(locale), id =>
            Ui.TextFill.Fill(Tr(locale)("ui.inhabitant.unnamed"), new Dictionary<string, string> { ["number"] = WorldFeed.NumberOf(id).ToString(CultureInfo.InvariantCulture) }));

    private static Func<string, string> Tr(string locale) => key => Catalogs.Value[locale].GetValueOrDefault(key, key);

    private static InhabitantSnapshot Inhabitant(string id, string? action, string? target, Position? at = null) =>
        new(id, "female", at ?? new Position(0, 0), FrozenDictionary<string, double>.Empty, FrozenDictionary<string, double>.Empty, FrozenDictionary<string, double>.Empty, 1, false, action, target, []);

    private static WorldSnapshot Snapshot(params InhabitantSnapshot[] inhabitants) => Snapshot(inhabitants, []);

    private static WorldSnapshot Snapshot(InhabitantSnapshot[] inhabitants, WorldObject[] objects) =>
        new(default, inhabitants, objects, Array.Empty<WorldEvent>());
}
