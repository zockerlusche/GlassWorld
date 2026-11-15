using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>B2749 (TL #25515): the inhabitant corner with many inhabitants, and search and filter of the inhabitants window.</summary>
public sealed class InhabitantCornerTests
{
    private static readonly UiTokens Tokens = ContractSerializer.Deserialize<UiTokens>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")));
    private static readonly HashSet<string> NoKnock = new(StringComparer.Ordinal);

    private static List<InhabitantRow> Rows(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new InhabitantRow($"inh-{i:000000}", i, 80, false, null, 0, Name: $"Name{i}"))];

    [Fact]
    public void TheThresholdsAreTheDecidedOnes()
    {
        // B2749: up to 4 full rows, 5 to 8 portraits only, from 9 only the important ones, at most 6 (as data).
        Assert.Equal(5, Tokens.Zones.PortraitsCompactFrom);
        Assert.Equal(9, Tokens.Zones.PortraitsImportantFrom);
        Assert.Equal(6, Tokens.Zones.PortraitsImportantMax);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void BelowTheImportantThresholdEveryoneIsShown(int count)
    {
        var rows = Rows(count);
        var (shown, more) = CornerModel.Shown(rows, Tokens.Zones, null, NoKnock);
        Assert.Equal(rows, shown);
        Assert.Equal(0, more);
        Assert.Equal(count >= 5, CornerModel.Compact(count, Tokens.Zones));
    }

    [Fact]
    public void FromNineOnlyTheImportantOnesAndHowManyMore()
    {
        var rows = Rows(50);
        rows[40] = rows[40] with { Favourite = true };
        rows[3] = rows[3] with { PuzzlingQuestions = 1 };
        var knocking = new HashSet<string>(StringComparer.Ordinal) { rows[20].Id };
        var (shown, more) = CornerModel.Shown(rows, Tokens.Zones, rows[10].Id, knocking);
        Assert.Equal([rows[3].Id, rows[10].Id, rows[20].Id, rows[40].Id], shown.Select(r => r.Id));
        Assert.Equal(46, more);
    }

    [Fact]
    public void AtMostSixAndTheFollowedOneAlwaysAmongThem()
    {
        var rows = Rows(50).Select(r => r with { Favourite = true }).ToList();
        var (shown, more) = CornerModel.Shown(rows, Tokens.Zones, rows[49].Id, NoKnock);
        Assert.Equal(Tokens.Zones.PortraitsImportantMax, shown.Count);
        Assert.Contains(rows[49], shown);
        Assert.Equal(50 - Tokens.Zones.PortraitsImportantMax, more);

        // In the order of the list, so nobody jumps when one more becomes important.
        Assert.Equal(shown.OrderBy(r => r.Number).Select(r => r.Id), shown.Select(r => r.Id));
    }

    [Fact]
    public void WhoKnocksStaysInTheCornerEvenAmongManyFavourites()
    {
        // Review GW3 #454: a knock is a call for help; six favourites must not push it out to the '+N'.
        var rows = Rows(50).Select((r, i) => i < 10 ? r with { Favourite = true } : r).ToList();
        var knocking = new HashSet<string>(StringComparer.Ordinal) { rows[30].Id };
        var (shown, _) = CornerModel.Shown(rows, Tokens.Zones, null, knocking);
        Assert.Contains(rows[30], shown);
        Assert.Equal(Tokens.Zones.PortraitsImportantMax, shown.Count);
    }

    [Fact]
    public void NobodyImportantLeavesOnlyTheCount()
    {
        var (shown, more) = CornerModel.Shown(Rows(12), Tokens.Zones, null, NoKnock);
        Assert.Empty(shown);
        Assert.Equal(12, more);
    }

    [Fact]
    public void TheWindowSearchesTheNameIgnoringCaseAndAccents()
    {
        var rows = Rows(3);
        rows[1] = rows[1] with { Name = "Jörn" };
        var found = InhabitantListModel.Filter(rows, "jorn", InhabitantFilter.All, NoKnock, Tokens.Bars, r => r.Name!);
        Assert.Equal([rows[1]], found);
        Assert.Equal(rows, InhabitantListModel.Filter(rows, "  ", InhabitantFilter.All, NoKnock, Tokens.Bars, r => r.Name!));
    }

    [Theory]
    [InlineData("Jörn", "jorn")]
    [InlineData("Élise", "elise")]
    [InlineData("Weiß", "weiss")]
    public void FoldDropsCaseAndAccents(string name, string folded) => Assert.Equal(folded, InhabitantListModel.Fold(name));

    [Fact]
    public void TheFiltersPickPinnedAskingKnockingAndWeak()
    {
        var rows = Rows(5);
        rows[0] = rows[0] with { Favourite = true };
        rows[1] = rows[1] with { PuzzlingQuestions = 2 };
        rows[3] = rows[3] with { WillToLivePercent = Tokens.Bars.StateBadBelow - 1 };
        var knocking = new HashSet<string>(StringComparer.Ordinal) { rows[2].Id };
        IReadOnlyList<InhabitantRow> Of(InhabitantFilter f) => InhabitantListModel.Filter(rows, string.Empty, f, knocking, Tokens.Bars, r => r.Name!);
        Assert.Equal([rows[0]], Of(InhabitantFilter.Pinned));
        Assert.Equal([rows[1]], Of(InhabitantFilter.Asking));
        Assert.Equal([rows[2]], Of(InhabitantFilter.Knocking));
        Assert.Equal([rows[3]], Of(InhabitantFilter.Weak));
        Assert.Equal(rows, Of(InhabitantFilter.All));
        Assert.All(InhabitantListModel.Filters, f => Assert.StartsWith("ui.inhabitants.filter.", InhabitantListModel.Key(f), StringComparison.Ordinal));
    }
}
