using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class ActivityDiaryTests
{
    [Fact]
    public void ALineIsAddedOnlyWhenTheActivityChangesAndIdleIsLeftOut()
    {
        var diary = new ActivityDiary(10);
        diary.Note("a", "untersucht Brennnessel", false, 1, "08:00");
        diary.Note("a", "untersucht Brennnessel", false, 1, "08:05");
        diary.Note("a", "steht da", true, 1, "08:10");
        diary.Note("a", "untersucht Brennnessel", false, 1, "08:20");
        diary.Note("a", "trinkt am Bach", false, 2, "09:00");
        diary.Note("b", "schläft", false, 2, "09:00");

        Assert.Equal(["trinkt am Bach", "untersucht Brennnessel", "untersucht Brennnessel"], diary.Of("a").Select(l => l.Text));
        Assert.Equal((2, "09:00"), (diary.Of("a")[0].Day, diary.Of("a")[0].Time));
        Assert.Single(diary.Of("b"));
        Assert.Empty(diary.Of("nobody"));
    }

    [Fact]
    public void OnlyTheNewestLinesAreKeptPerInhabitant()
    {
        var diary = new ActivityDiary(2);
        foreach (var text in new[] { "eins", "zwei", "drei" })
        {
            diary.Note("a", text, false, 1, "10:00");
        }

        Assert.Equal(["drei", "zwei"], diary.Of("a").Select(l => l.Text));
    }
}
