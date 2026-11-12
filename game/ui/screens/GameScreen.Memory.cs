using System.Globalization;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// The memory page of an inhabitant (B8 R16, B6 R19, B2518, Marcel W1603): its history from what the world kept - the
/// chronicle events it took part in and its conversations with the player - in a window, opened from its card or from
/// the chronicle (also for one who has died), with its favourite mark and an export as a readable file.
/// </summary>
public partial class GameScreen
{
    /// <summary>Opens the memory page of an inhabitant (living or not); returns the window, or null without a world.</summary>
    public GwWindow? OpenMemory(string inhabitantId)
    {
        if (_style is null || _presenter?.Host is not { } host)
        {
            return null;
        }

        _chronicle?.Sync(host.Chronicle);
        var name = MemoryName(inhabitantId);
        var items = MemoryItems(inhabitantId);
        var window = new GwWindow { Name = "Memory" };
        window.Setup("ui.memory.title", _style);
        window.SetTitleText(TextFill.Fill(Tr("ui.memory.title"), new Dictionary<string, string> { ["name"] = name }));

        // What the page is, and that what the player says is kept (review GW1 #285).
        window.Content.AddChild(new Label
        {
            Text = TextFill.Fill(Tr("ui.memory.intro"), new Dictionary<string, string> { ["name"] = name }),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        });
        if (items.Count == 0)
        {
            window.Content.AddChild(new Label { Text = "ui.memory.empty", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }

        long? day = null;
        foreach (var item in items)
        {
            var itemDay = host.World.Calendar.At(item.Tick).Day;
            if (itemDay != day)
            {
                day = itemDay;
                window.Content.AddChild(new Label { Text = MemoryDay(item.Tick), ThemeTypeVariation = UiTheme.SubheaderLabel, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled });
            }

            window.Content.AddChild(new Label { Text = MemoryLine(item, name), AutowrapMode = TextServer.AutowrapMode.WordSmart, AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled });
        }

        // The favourite mark (B6 R19), here also for one who has died (A1565).
        var favourite = new CheckButton { Text = "ui.card.favourite", ButtonPressed = host.Favourites.IsFavourite(inhabitantId) };
        favourite.Toggled += on => host.Decisions.Submit(new Simulation.Decisions.PlayerCommand(
            on ? Simulation.Player.FavouritesSystem.Mark : Simulation.Player.FavouritesSystem.Unmark, inhabitantId));
        window.Content.AddChild(favourite);

        window.AddAction("ui.memory.export").Pressed += () => ExportMemory(inhabitantId);
        window.AddAction("ui.common.close").Pressed += window.Close;
        ShowWindow(window);
        return window;
    }

    /// <summary>
    /// Writes the memory page as a readable HTML page and a text file into the player's folder (local only, like the
    /// chronicle export) and names the folder in a calm hint. Returns the HTML path, or null.
    /// </summary>
    public string? ExportMemory(string inhabitantId)
    {
        if (_presenter?.Host is not { } host)
        {
            return null;
        }

        _chronicle?.Sync(host.Chronicle);
        var name = MemoryName(inhabitantId);
        var items = MemoryItems(inhabitantId);
        var title = TextFill.Fill(Tr("ui.memory.title"), new Dictionary<string, string> { ["name"] = name });
        var folder = System.IO.Path.Combine(OS.GetUserDataDir(), "memories");
        var stem = ChronicleExport.FileStem(title, DateTimeOffset.Now);
        var html = System.IO.Path.Combine(folder, stem + ".html");
        long DayOf(long tick) => host.World.Calendar.At(tick).Day;
        string DayTitle(long day) => MemoryDay(day * host.World.Calendar.TicksPerDay);
        try
        {
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(html, MemoryPage.Html(items, title, TranslationServer.GetLocale(), DayOf, DayTitle, i => MemoryLine(i, name)));
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, stem + ".txt"), MemoryPage.Text(items, title, DayOf, DayTitle, i => MemoryLine(i, name)));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"memory export failed: {e.Message}");
            ShowHint(new ToastMessage("msg.hint.memory_export_failed", new Dictionary<string, string>(), null));
            return null;
        }

        ShowHint(new ToastMessage("msg.hint.memory_exported", new Dictionary<string, string> { ["folder"] = folder }, null));
        return html;
    }

    /// <summary>Whether an inhabitant still lives in the world (a chronicle line of one who has died opens its memories).</summary>
    private bool Lives(string inhabitantId) => _presenter?.Host?.Inhabitants.All.Any(i => i.Id == inhabitantId) == true;

    private IReadOnlyList<MemoryItem> MemoryItems(string inhabitantId) =>
        _presenter?.Host is { } host
            ? MemoryPage.Items(inhabitantId, (_chronicle?.Entries ?? []).Reverse(), host.Conversation.HistoryOf(inhabitantId))
            : [];

    private string MemoryName(string inhabitantId) =>
        _feed?.Inhabitants().FirstOrDefault(r => r.Id == inhabitantId) is { } row ? DisplayName(row) : ChronicleNamesNow().Inhabitant(inhabitantId);

    private string MemoryDay(long tick)
    {
        var time = _presenter!.Host!.World.Calendar.At(tick);
        return TextFill.Fill(Tr("ui.chronicle.day"), new Dictionary<string, string>
        {
            ["season"] = Tr(MemoryPage.SeasonKey(time.SeasonId)),
            ["day"] = time.DayOfSeason.ToString(CultureInfo.InvariantCulture),
            ["year"] = time.Year.ToString(CultureInfo.InvariantCulture),
        });
    }

    /// <summary>One line of the page: a chronicle event as the chronicle says it, or what was said - the inhabitant's words as they were said.</summary>
    private string MemoryLine(MemoryItem item, string name)
    {
        if (item.Event is { } entry)
        {
            return ChronicleLine(entry);
        }

        var talk = item.Talk!;
        return TextFill.Fill(Tr(talk.FromPlayer ? "ui.memory.player_said" : "ui.memory.inhabitant_said"), new Dictionary<string, string> { ["name"] = name, ["text"] = talk.Text });
    }
}
