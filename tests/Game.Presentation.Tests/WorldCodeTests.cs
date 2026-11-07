using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Presentation.Tests;

/// <summary>Shareable world code (B2534): round trip, forgiving input, typos, same code = same start world.</summary>
public sealed class WorldCodeTests
{
    private static readonly Contracts.WorldOptions Options =
        ContractSerializer.Deserialize<Contracts.WorldOptions>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "player", "world_options.json")));

    private static readonly Contracts.FavorSettings Favor =
        ContractSerializer.Deserialize<Contracts.FavorSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "player", "favor.json")));

    public static TheoryData<ulong> Seeds => [0UL, 1UL, 127UL, 128UL, 42UL, 999_999_999UL, ulong.MaxValue];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EveryStartRoundTrips(ulong seed)
    {
        foreach (var size in Options.Sizes)
        {
            foreach (var climate in Options.Climates)
            {
                foreach (var favor in Favor.StartOptions)
                {
                    var start = new WorldStart(seed, size.Id, climate.Id, favor);
                    var code = WorldCode.Encode(start, Options, Favor);
                    Assert.Matches("^GW(-[0-9A-HJKMNP-TV-Z]{1,4})+$", code);
                    Assert.True(WorldCode.TryDecode(code, Options, Favor, out var back));
                    Assert.Equal(start, back);
                }
            }
        }
    }

    [Fact]
    public void SmallSeedsGiveShortCodes()
        => Assert.True(WorldCode.Encode(new WorldStart(42, Options.DefaultSize, Options.DefaultClimate, Favor.StartOptions.First()), Options, Favor).Length <= 16);

    [Fact]
    public void ReadingIsForgiving()
    {
        var start = new WorldStart(123_456_789, Options.DefaultSize, Options.DefaultClimate, Favor.StartOptions.Last());
        var code = WorldCode.Encode(start, Options, Favor);
        var sloppy = "  " + code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal).Replace('1', 'l').Replace('0', 'o') + " ";
        Assert.True(WorldCode.TryDecode(sloppy, Options, Favor, out var back));
        Assert.Equal(start, back);
    }

    [Fact]
    public void TyposAndForeignTextAreRefused()
    {
        var code = WorldCode.Encode(new WorldStart(123_456_789, Options.DefaultSize, Options.DefaultClimate, Favor.StartOptions.First()), Options, Favor);
        var last = code[^1];
        var typo = code[..^1] + (last == 'A' ? 'B' : 'A');
        Assert.False(WorldCode.TryDecode(typo, Options, Favor, out _));
        Assert.False(WorldCode.TryDecode("hello world", Options, Favor, out _));
        Assert.False(WorldCode.TryDecode("", Options, Favor, out _));
        Assert.False(WorldCode.TryDecode(null, Options, Favor, out _));
        Assert.False(WorldCode.TryDecode("GW-!!", Options, Favor, out _));
    }

    [Fact]
    public void UnknownOptionCannotBeEncoded()
        => Assert.Throws<ArgumentException>(() => WorldCode.Encode(new WorldStart(1, "huge", Options.DefaultClimate, Favor.StartOptions.First()), Options, Favor));

    [Fact]
    public void SameCodeGivesTheSameStartWorld()
    {
        var code = WorldCode.Encode(new WorldStart(4242, Options.DefaultSize, Options.DefaultClimate, Favor.StartOptions.First()), Options, Favor);
        Assert.True(WorldCode.TryDecode(code, Options, Favor, out var a));
        Assert.True(WorldCode.TryDecode(code, Options, Favor, out var b));
        var content = Path.Combine(TestPaths.Root, "content");
        var first = SimulationHost.CreatePlaceholderWorld(content, a!.Seed, new MockDecisionPort(), "de");
        var second = SimulationHost.CreatePlaceholderWorld(content, b!.Seed, new MockDecisionPort(), "de");
        Assert.Equal(first.World.StateHash(), second.World.StateHash());
        var other = SimulationHost.CreatePlaceholderWorld(content, a.Seed + 1, new MockDecisionPort(), "de");
        Assert.NotEqual(first.World.StateHash(), other.World.StateHash());
    }
}
