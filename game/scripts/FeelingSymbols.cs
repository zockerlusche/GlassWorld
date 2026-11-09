using System.Reflection;
using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Feeling symbols above the heads (B2541 part 4, B2543) in comic style: <see cref="EmotionSymbolPlanner"/>
/// decides what shows, this node draws billboards above the figures. Short feelings rise and fade,
/// lasting ones circle; with "reduce motion" they only fade in and out in place. Switched off in the
/// settings, nothing shows. Real seconds drive it, so it also runs while the world is paused.
/// </summary>
public partial class FeelingSymbols : Node3D
{
    /// <summary>Icon size in pixels (the billboard scales it to the data's size in metres).</summary>
    private const int IconPx = 128;

    /// <summary>No conditions until GW1's core has them (B2543, 15.11.).</summary>
    private static readonly IReadOnlyCollection<string> NoConditions = [];

    private readonly Dictionary<string, ImageTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<SymbolView, List<Sprite3D>> _sprites = [];
    private EmotionSymbols? _data;
    private EmotionSymbolPlanner? _planner;
    private WorldFeed? _feed;
    private SimulationHost? _host;
    private Node? _figures;
    private Func<bool> _enabled = () => true;
    private Func<bool> _reduceMotion = () => false;
    private IReadOnlyList<SymbolView>? _frozen;
    private double _clock;

    /// <summary>UI tokens and scale for the screen size of the symbols (F5284); null keeps the world size.</summary>
    public GlassWorld.Presentation.Ui.UiStyle? Style { get; set; }

    /// <summary>Loads the symbol set; false (and an error) if the data is broken.</summary>
    /// <param name="host">The world.</param>
    /// <param name="figures">Node whose children are the figures, named by inhabitant id.</param>
    /// <param name="enabled">The player's setting (read every frame).</param>
    /// <param name="reduceMotion">"Reduce motion" (read every frame).</param>
    public bool Setup(SimulationHost host, Node figures, Func<bool> enabled, Func<bool> reduceMotion)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _figures = figures ?? throw new ArgumentNullException(nameof(figures));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _reduceMotion = reduceMotion ?? throw new ArgumentNullException(nameof(reduceMotion));
        try
        {
            _data = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<EmotionSymbols>(Read("content.ui.emotion_symbols.json"));
            _planner = new EmotionSymbolPlanner(_data, [.. host.Content.Vocabularies[VocabularyKind.Mood].Entries.Select(e => e.Id)]);
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushError($"feeling symbols: {e.Message}");
            return false;
        }

        foreach (var symbol in _data.Symbols)
        {
            var image = Image.CreateFromData(IconPx, IconPx, false, Image.Format.Rgba8, SymbolIcons.Render(symbol.Shape, symbol.Fill, IconPx));
            _textures[symbol.Id] = ImageTexture.CreateFromImage(image);
        }

        _feed = new WorldFeed(host);
        return true;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_planner is null || _feed is null || _host is null)
        {
            return;
        }

        _clock += delta;
        var events = _feed.NewEvents();
        if (!_enabled())
        {
            Show([]);
            return;
        }

        if (_frozen is not null)
        {
            Show(_frozen);
            return;
        }

        var subjects = _host.Inhabitants.All.Select(i => new SymbolSubject(i.Id, i.Moods, NoConditions)).ToList();
        Show(_planner.Update(_clock, events, subjects));
    }

    /// <summary>Screenshot run only: every symbol of the data above the inhabitants in turn, still in place.</summary>
    public void ShowAllForShots()
    {
        if (_data is null || _host is null)
        {
            return;
        }

        var who = _host.Inhabitants.All.Select(i => i.Id).ToList();
        var lasting = _data.Triggers.Where(t => t.Mode == EmotionTriggerMode.Holds).Select(t => t.Symbol).ToHashSet(StringComparer.Ordinal);
        _frozen = [.. _data.Symbols.Select((s, i) => new SymbolView(
            who[i % who.Count],
            s.Id,
            lasting.Contains(s.Id) ? SymbolMotion.Orbit : SymbolMotion.Rise,
            lasting.Contains(s.Id) ? _clock : _clock - (_data.Rise.DurationS * 0.35)))];
        _reduceMotion = () => true;
    }

    /// <summary>Screenshot run only: all icons side by side (art check, D355).</summary>
    public Image IconSheet()
    {
        var symbols = _data?.Symbols.ToList() ?? [];
        var sheet = Image.CreateEmpty(Math.Max(1, symbols.Count) * IconPx, IconPx, false, Image.Format.Rgba8);
        sheet.Fill(new Color(0.93f, 0.95f, 0.97f));
        for (var i = 0; i < symbols.Count; i++)
        {
            var icon = _textures[symbols[i].Id].GetImage();
            sheet.BlendRect(icon, new Rect2I(0, 0, IconPx, IconPx), new Vector2I(i * IconPx, 0));
        }

        return sheet;
    }

    /// <summary>Smoke run only: every symbol has its texture, an orbiting symbol becomes its copies, clearing removes them.</summary>
    /// <returns>Empty if fine, otherwise the first problem.</returns>
    public string SelfCheck()
    {
        if (_data is null || _host is null)
        {
            return "feeling symbols not set up";
        }

        if (_data.Symbols.FirstOrDefault(s => !_textures.TryGetValue(s.Id, out var t) || t.GetWidth() != IconPx) is { } missing)
        {
            return $"no texture for '{missing.Id}'";
        }

        var who = _host.Inhabitants.All.First().Id;
        Show([new SymbolView(who, _data.Symbols.First().Id, SymbolMotion.Orbit, _clock)]);
        var copies = _sprites.Values.Sum(s => s.Count);
        Show([]);
        if (copies != _data.Orbit.Copies)
        {
            return $"an orbiting symbol gave {copies} billboards, expected {_data.Orbit.Copies}";
        }

        return _sprites.Count == 0 ? string.Empty : "symbols left after clearing";
    }

    private void Show(IReadOnlyList<SymbolView> views)
    {
        foreach (var gone in _sprites.Keys.Where(k => !views.Contains(k)).ToList())
        {
            foreach (var sprite in _sprites[gone])
            {
                sprite.QueueFree();
            }

            _sprites.Remove(gone);
        }

        foreach (var view in views)
        {
            if (_figures?.GetNodeOrNull<Node3D>(view.InhabitantId) is not { } figure || _data!.Symbols.FirstOrDefault(s => s.Id == view.Symbol) is not { } symbol)
            {
                continue;
            }

            if (!_sprites.TryGetValue(view, out var sprites))
            {
                var count = view.Motion == SymbolMotion.Orbit ? _data.Orbit.Copies : 1;
                sprites = [.. Enumerable.Range(0, count).Select(_ => NewSprite(symbol))];
                _sprites[view] = sprites;
            }

            Place(view, figure, sprites);
        }
    }

    private Sprite3D NewSprite(EmotionSymbol symbol)
    {
        var sprite = new Sprite3D
        {
            Texture = _textures[symbol.Id],
            PixelSize = (float)(symbol.SizeM / IconPx),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Shaded = false,
            Transparent = true,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        };
        AddChild(sprite);
        return sprite;
    }

    private void Place(SymbolView view, Node3D figure, List<Sprite3D> sprites)
    {
        var data = _data!;
        var anchor = figure.GetNodeOrNull<Node3D>("HeadAnchor")?.GlobalPosition ?? (figure.GlobalPosition + new Vector3(0, (float)data.FallbackHeadM, 0));

        // One factor for symbol and offsets (F5284, review GW1 #141): from afar the layout around the head grows
        // as a whole, so a bigger symbol does not reach into the head and orbiting ones do not overlap.
        var f = Style is { } style ? GlassWorld.Game.World.HeadSymbolScale.FactorAt(this, anchor, sprites[0].PixelSize * IconPx, style.Tokens, style.ContentScaleFactor) : 1f;
        var head = anchor + new Vector3(0, (float)data.HeadOffsetM * f, 0);
        foreach (var sprite in sprites)
        {
            sprite.Scale = new Vector3(f, f, f);
        }

        var still = _reduceMotion();
        var age = _clock - view.StartedAt;
        if (view.Motion == SymbolMotion.Rise)
        {
            var t = Math.Clamp(age / data.Rise.DurationS, 0, 1);
            var share = data.Rise.FadeInShare;
            var alpha = t < share ? t / share : 1 - ((t - share) / (1 - share));
            sprites[0].GlobalPosition = head + new Vector3(0, ((float)data.Rise.StartM + (still ? 0 : (float)(data.Rise.RiseM * t))) * f, 0);
            sprites[0].Modulate = new Color(1, 1, 1, (float)alpha);
            Refine(sprites);
            return;
        }

        for (var k = 0; k < sprites.Count; k++)
        {
            var angle = (still ? 0 : 2 * Math.PI * age / data.Orbit.PeriodS) + (k * 2 * Math.PI / sprites.Count);
            sprites[k].GlobalPosition = head + (new Vector3((float)(data.Orbit.RadiusM * Math.Cos(angle)), 0, (float)(data.Orbit.RadiusM * Math.Sin(angle))) * f);
            sprites[k].Modulate = new Color(1, 1, 1, (float)(data.Orbit.FadeInS <= 0 ? 1 : Math.Clamp(age / data.Orbit.FadeInS, 0, 1)));
        }

        Refine(sprites);
    }

    /// <summary>Close up the symbol is nearer or further than the head: its own size from its own distance (offsets stay).</summary>
    private void Refine(List<Sprite3D> sprites)
    {
        if (Style is not { } style)
        {
            return;
        }

        foreach (var sprite in sprites)
        {
            GlassWorld.Game.World.HeadSymbolScale.Apply(sprite, sprite.PixelSize * IconPx, style.Tokens, style.ContentScaleFactor);
        }
    }

    /// <summary>
    /// Smoke only (review GW1 #141): every symbol shown, placed for the current camera, has a screen height within
    /// the data's floor and ceiling. Empty when all do.
    /// </summary>
    public string ScreenSizeCheck()
    {
        if (_data is null || _host is null || _figures is null || Style is not { } style)
        {
            return "feeling symbols not set up";
        }

        var who = _host.Inhabitants.All.First().Id;
        if (GetViewport().GetCamera3D() is not { } camera || _figures.GetNodeOrNull<Node3D>(who) is not { } figure)
        {
            return "no camera or figure";
        }

        // Look at the head from the usual distance (floor) and from close up (ceiling), then put the camera back.
        var kept = camera.GlobalTransform;
        var problem = string.Empty;
        foreach (var away in new[] { new Vector3(0, 14, 38), new Vector3(0, 0.5f, 6) })
        {
            // Aimed at where the symbol starts: the head anchor (as Place uses it) plus the offsets.
            var head = (figure.GetNodeOrNull<Node3D>("HeadAnchor")?.GlobalPosition ?? (figure.GlobalPosition + new Vector3(0, (float)_data.FallbackHeadM, 0)))
                + new Vector3(0, (float)(_data.HeadOffsetM + _data.Rise.StartM), 0);
            camera.GlobalPosition = head + away;
            camera.LookAt(head, Vector3.Up);
            Show([.. _data.Symbols.Select(s => new SymbolView(who, s.Id, SymbolMotion.Rise, _clock))]);
            problem = _sprites.Values.SelectMany(s => s)
                .Select(s => GlassWorld.Game.World.HeadSymbolScale.CheckOnScreen(s, s.PixelSize * IconPx, style.Tokens, style.ContentScaleFactor))
                .FirstOrDefault(p => p.Length > 0) ?? string.Empty;
            Show([]);
            if (problem.Length > 0)
            {
                problem = $"{away.Length():0} m away: {problem}";
                break;
            }
        }

        camera.GlobalTransform = kept;
        return problem;
    }

    private static string Read(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
