using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// A1791 (Marcel W2242, D1192 #5/#24): a click on a thing in the world opens a small window - its neutral name, who built
/// it or invented its kind, what each inhabitant believes about it and where it has that from (in words, never by colour
/// alone), and the ground it stands on with how fast one walks there (TL #27543). It only reads; the world is not touched.
/// </summary>
public partial class GameScreen
{
    private GwWindow? _thingWindow;
    private Label? _writingHover; // A1356: what is written, near the pointer
    private ulong _writingHoverAt;

    /// <summary>The thing a click at a point on the ground means (within the data's radius), or null.</summary>
    private string? ThingAt(Vector3 point)
    {
        if (_feed?.Snapshot is not { } snapshot || _style is null || _presenter is null)
        {
            return null;
        }

        // Only what the island draws: not a type without a shape (sea water), not a thing in a hand (review GW3 #527).
        var carried = snapshot.Inhabitants.Select(i => i.Held).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var least = _style.Tokens.Window.ThingPickM;
        return ThingInfo.Nearest(snapshot.Objects, point.X, point.Z, thing => _presenter.PickRadius(thing, least, carried));
    }

    /// <summary>Opens the info window of a thing (one at a time); returns it, or null when the thing is gone.</summary>
    public GwWindow? OpenThing(string objectId)
    {
        if (_style is null || _presenter?.Host is not { } host || ThingInfo.Of(host, objectId) is not { } info)
        {
            return null;
        }

        if (_thingWindow is not null && IsInstanceValid(_thingWindow))
        {
            _thingWindow.Close();
        }

        var name = ThingName(info.TypeId);
        var window = new GwWindow { Name = "Thing" };
        window.Setup("ui.thing.title", _style);
        window.SetTitleText(name);

        // As wide as the card (zones.card_width), so the lines of who knows what read as lines, not as a column of words.
        window.Content.CustomMinimumSize = new Vector2(_style.Tokens.Zones.CardWidth, 0);
        Label Line(string text, string? variation = null) => new()
        {
            Text = text,
            ThemeTypeVariation = variation ?? string.Empty,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
        };

        if (info.BuilderId is { } builder)
        {
            window.Content.AddChild(Line(TextFill.Fill(Tr("ui.thing.built_by"), new Dictionary<string, string> { ["name"] = NameOfId(builder) })));
        }
        else if (info.InventorId is { } inventor)
        {
            window.Content.AddChild(Line(TextFill.Fill(Tr("ui.thing.invented_by"), new Dictionary<string, string> { ["name"] = NameOfId(inventor) })));
        }

        // A1356 (B8-11): what is written on it, in the player's language, by whom and how legible.
        if (WritingLine(objectId) is { } written)
        {
            window.Content.AddChild(Line(written));
        }

        if (info.Ground is { } ground)
        {
            window.Content.AddChild(Line(GroundLine(ground)));
        }

        window.Content.AddChild(Line(Tr("ui.thing.known"), UiTheme.SubheaderLabel));
        if (info.Knowers.Count == 0)
        {
            window.Content.AddChild(Line(Tr("ui.thing.nobody_knows")));
        }

        foreach (var knower in info.Knowers)
        {
            var facts = string.Join(", ", knower.Facts.Select(FactText));
            window.Content.AddChild(Line(TextFill.Fill(Tr("ui.thing.knows"), new Dictionary<string, string> { ["name"] = NameOfId(knower.InhabitantId), ["facts"] = facts })));
        }

        _thingWindow = window;
        ShowWindow(window);
        return window;
    }

    /// <summary>A1356: the line that says what is written on a thing, or null when nothing is.</summary>
    private string? WritingLine(string objectId)
    {
        if (_presenter?.Host is not { } host || host.Writing.On(objectId) is not { } text || _presenter.WritingLookData is not { } look)
        {
            return null;
        }

        var says = WritingLook.Says(text.KnowledgeRef, key => Tr(key), ThingName) ?? Tr("ui.writing.unknown");
        return TextFill.Fill(Tr("ui.writing.on"), new Dictionary<string, string>
        {
            ["text"] = says,
            ["author"] = NameOfId(text.AuthorId),
            ["legibility"] = Tr(WritingLook.LegibilityKey(text.Legibility, look.FaintBelow, look.HardBelow)),
        });
    }

    /// <summary>
    /// A1356 (B8-11): when the pointer rests over a written thing, a small line near it says what is written (checked a
    /// few times a second, not every motion).
    /// </summary>
    private void HoverWriting(Vector2 position)
    {
        var now = Time.GetTicksMsec();
        if (now - _writingHoverAt < 150)
        {
            return;
        }

        _writingHoverAt = now;
        var line = WorldPoint(position) is { } ground && ThingAt(ground) is { } thing ? WritingLine(thing) : null;
        if (_writingHover is null)
        {
            if (line is null || _style is null)
            {
                return;
            }

            _writingHover = new Label { Name = "WritingHover", AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled, ThemeTypeVariation = UiTheme.SmallLabel, MouseFilter = Control.MouseFilterEnum.Ignore };
            AddChild(_writingHover);
        }

        _writingHover.Visible = line is not null;
        if (line is not null)
        {
            _writingHover.Text = line;
            _writingHover.Position = position + new Vector2(16, 16);
        }
    }

    /// <summary>One belief in words: the property, 'not' when it believes the opposite, and where it has it from.</summary>
    private string FactText(ThingFact fact)
    {
        // A property inside the sentence starts small ('nicht tragbar', 'salzig'); the vocabulary writes it as a heading.
        var word = Tr(VocabKeys.Property(fact.PropertyId));
        var property = word.Length > 0 ? char.ToLower(word[0], System.Globalization.CultureInfo.InvariantCulture) + word[1..] : word;
        var what = fact.Value ? property : TextFill.Fill(Tr("ui.thing.fact_not"), new Dictionary<string, string> { ["property"] = property });
        return TextFill.Fill(Tr("ui.thing.fact"), new Dictionary<string, string> { ["fact"] = what, ["source"] = Tr(fact.SourceKey) });
    }

    /// <summary>The ground in words: its kind, and that one walks slower there (with how fast).</summary>
    private string GroundLine(GroundInfo ground)
    {
        var values = new Dictionary<string, string> { ["ground"] = Tr(VocabKeys.Biome(ground.Biome)), ["percent"] = Number(ground.SpeedPercent) };
        return TextFill.Fill(Tr(ground.Slower ? "ui.thing.ground_slow" : "ui.thing.ground"), values);
    }

    /// <summary>The inhabitant's ground for its card (TL #27543: 'rocks_hills = Fels, langsamer'), or empty without a map.</summary>
    private string CardGroundLine(string inhabitantId) =>
        _presenter?.Host is { } host && _feed?.Snapshot?.Inhabitants.FirstOrDefault(i => i.Id == inhabitantId) is { } inhabitant
            && ThingInfo.GroundAt(host, inhabitant.Position) is { } ground
            ? GroundLine(ground)
            : string.Empty;

    /// <summary>UI tour: closes the thing's info again.</summary>
    public void CloseThingForShots()
    {
        if (_thingWindow is not null && IsInstanceValid(_thingWindow))
        {
            _thingWindow.Close();
        }
    }

    /// <summary>UI tour: opens the info of the first thing that some inhabitant knows something about (else the first thing).</summary>
    public string? OpenSomeThingForShots()
    {
        if (_presenter?.Host is not { } host)
        {
            return null;
        }

        // Only what a click could open (drawn, not in a hand), as in the game (review GW3 #527).
        var types = host.Inhabitants.All.SelectMany(i => i.Beliefs.All.Select(b => b.TypeId)).ToHashSet(StringComparer.Ordinal);
        var carried = host.Inhabitants.All.Select(i => i.Held).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var pickable = host.Objects.All.Where(o => _presenter.PickRadius(o, 1, carried) is not null).ToList();
        var thing = pickable.FirstOrDefault(o => types.Contains(o.TypeId)) ?? pickable.FirstOrDefault();
        return thing is not null && OpenThing(thing.Id) is not null ? thing.Id : null;
    }
}
