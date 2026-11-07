using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// Settings (B6 R4): language, UI size and text size (separate, B2513), contrast, reduced motion,
/// hints, hiding the UI and the volume per bus (B8). Every change takes effect at once through
/// <see cref="Changed"/>; the caller stores the settings in the user folder, never in a save game.
/// AI level and computing night follow with B7.
/// </summary>
public partial class SettingsWindow : GwWindow
{
    private PlayerSettings _settings = new();

    /// <summary>Raised after every change with the new, already clamped settings.</summary>
    public event Action<PlayerSettings>? Changed;

    /// <summary>Builds the controls for the current settings.</summary>
    public void Build(PlayerSettings settings, SettingsLimits limits, SoundMix mix, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(mix);
        ArgumentNullException.ThrowIfNull(style);
        Setup("ui.settings.title", style);
        _settings = settings;

        Section("ui.settings.section.language");
        var language = new OptionButton();
        foreach (var locale in limits.Locales.Order(StringComparer.Ordinal))
        {
            language.AddItem("ui.settings.language." + locale);
            language.SetItemMetadata(language.ItemCount - 1, locale);
            if (locale == (settings.Language ?? TranslationServer.GetLocale()))
            {
                language.Selected = language.ItemCount - 1;
            }
        }

        language.ItemSelected += index => Apply(_settings with { Language = language.GetItemMetadata((int)index).AsString() });
        Row("ui.settings.language", language);

        Section("ui.settings.section.display");
        Row("ui.settings.ui_scale", SizeSlider(limits.Tokens.UiScale, settings.UiScale, v => _settings with { UiScale = v }, style.Tokens.Window.CardBarWidth));
        Row("ui.settings.text_scale", SizeSlider(limits.Tokens.TextScale, settings.TextScale, v => _settings with { TextScale = v }, style.Tokens.Window.CardBarWidth));
        Row("ui.settings.high_contrast", Toggle(settings.HighContrast, v => _settings with { HighContrast = v }));
        Row("ui.settings.reduce_motion", Toggle(settings.ReduceMotion, v => _settings with { ReduceMotion = v }));
        Row("ui.settings.hide_ui", Toggle(settings.HideUi, v => _settings with { HideUi = v }));

        Section("ui.settings.section.hints");
        var duration = new SpinBox { MinValue = 1, MaxValue = limits.Tokens.Toast.MaxDurationFactor, Step = 1, Value = settings.HintDurationFactor };
        duration.ValueChanged += v => Apply(_settings with { HintDurationFactor = (int)v });
        Row("ui.settings.hint_duration", duration);
        Row("ui.settings.hints_until_dismissed", Toggle(settings.HintsUntilDismissed, v => _settings with { HintsUntilDismissed = v }));

        Section("ui.settings.section.audio");
        foreach (var bus in mix.Buses)
        {
            var id = BusKey(bus.Id);
            var slider = new HSlider
            {
                MinValue = limits.VolumeDb.Min,
                MaxValue = limits.VolumeDb.Max,
                Step = 1,
                Value = settings.BusVolumesDb.TryGetValue(id, out var db) ? db : bus.VolumeDb,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(style.Tokens.Window.CardBarWidth, 0),
            };
            slider.ValueChanged += v => Apply(_settings with { BusVolumesDb = new Dictionary<string, double>(_settings.BusVolumesDb, StringComparer.Ordinal) { [id] = v } });
            Row("ui.settings.volume." + id, slider);
        }

        AddAction("ui.common.close").Pressed += Close;
    }

    /// <summary>Id of a bus in the data and in the settings file (e.g. <c>music</c>).</summary>
    public static string BusKey(MixBusId id)
    {
#pragma warning disable CA1308 // The data ids are lower-case ASCII.
        return id.ToString().ToLowerInvariant();
#pragma warning restore CA1308
    }

    private void Apply(PlayerSettings settings)
    {
        _settings = settings;
        Changed?.Invoke(settings);
    }

    private HBoxContainer SizeSlider(UiScaleRange range, double value, Func<double, PlayerSettings> change, float minWidth)
    {
        // The value label is a number, not a key; the slider applies on release so the window does not
        // jump under the mouse while the size changes.
        var box = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var slider = new HSlider { MinValue = range.Min, MaxValue = range.Max, Step = range.Step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(minWidth, 0) };
        var label = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = Percent(value) };
        slider.ValueChanged += v => label.Text = Percent(v);
        slider.DragEnded += changed =>
        {
            if (changed)
            {
                Apply(change(slider.Value));
            }
        };
        box.AddChild(slider);
        box.AddChild(label);
        return box;
    }

    private CheckButton Toggle(bool value, Func<bool, PlayerSettings> change)
    {
        var toggle = new CheckButton { ButtonPressed = value };
        toggle.Toggled += v => Apply(change(v));
        return toggle;
    }

    private void Section(string key) => Content.AddChild(new Label { Text = key, ThemeTypeVariation = UiTheme.SubheaderLabel });

    private void Row(string key, Control control)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = key, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(control);
        Content.AddChild(row);
    }

    private static string Percent(double value) => ((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture) + " %";
}
