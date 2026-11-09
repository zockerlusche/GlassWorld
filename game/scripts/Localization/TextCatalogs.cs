using System.Reflection;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using Godot;

namespace GlassWorld.Game.Localization;

/// <summary>
/// Loads the player-facing texts from content/i18n (embedded into the game assembly at build time,
/// ADR-0007) into Godot's TranslationServer, so scenes and code only use keys. Invalid catalogs are
/// refused with an error in the log; the game then shows keys instead of texts.
/// </summary>
public static class TextCatalogs
{
    /// <summary>Languages the player can choose (B2470). The first one is the fallback.</summary>
    public static readonly IReadOnlyList<string> SupportedLocales = ["en", "de"];

    private const string ResourcePrefix = "i18n.";

    private static readonly Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>> Loaded = new(StringComparer.Ordinal);

    /// <summary>Number of keys per catalog after <see cref="Install"/>.</summary>
    public static int KeyCount { get; private set; }

    /// <summary>Registers all embedded catalogs and selects the system language if it is supported.</summary>
    /// <returns>True if every supported language was loaded and all catalogs have the same keys.</returns>
    public static bool Install()
    {
        var ok = true;
        foreach (var locale in SupportedLocales)
        {
            var entries = Read(locale);
            if (entries is null)
            {
                ok = false;
                continue;
            }

            var translation = new Translation { Locale = locale };
            foreach (var (key, text) in entries)
            {
                translation.AddMessage(key, text);
            }

            TranslationServer.AddTranslation(translation);
            Loaded[locale] = entries;
        }

        ok &= SameKeys();

        // The no-answer line arrives as a key (W1701); the bubble shows it in the current language, or
        // nothing while the catalogs lack the key (never a raw key).
        GlassWorld.Presentation.World.TalkBubble.Translate = key => TranslationServer.Translate(key).ToString() is var text && text != key ? text : string.Empty;
        KeyCount = Loaded.Count > 0 ? Loaded.Values.First().Count : 0;
        var system = OS.GetLocaleLanguage();
        SetLanguage(SupportedLocales.Contains(system, StringComparer.Ordinal) ? system : SupportedLocales[0]);
        return ok;
    }

    /// <summary>Switches the language of the running game; controls update automatically.</summary>
    public static void SetLanguage(string locale)
    {
        if (!SupportedLocales.Contains(locale, StringComparer.Ordinal))
        {
            GD.PushError($"i18n: unsupported locale '{locale}'");
            return;
        }

        TranslationServer.SetLocale(locale);
    }

    /// <summary>
    /// Self-check for the headless smoke run: in every language each key resolves to the text of its
    /// catalog, and switching back restores the starting language.
    /// </summary>
    /// <returns>Empty if fine, otherwise a description of the first problem.</returns>
    public static string SelfCheck()
    {
        var start = TranslationServer.GetLocale();
        try
        {
            foreach (var (locale, entries) in Loaded)
            {
                SetLanguage(locale);
                foreach (var (key, text) in entries)
                {
                    var shown = TranslationServer.Translate(key).ToString();
                    if (!string.Equals(shown, text, StringComparison.Ordinal))
                    {
                        return $"{locale}: '{key}' shows '{shown}', expected '{text}'";
                    }
                }
            }

            return string.Empty;
        }
        finally
        {
            TranslationServer.SetLocale(start);
        }
    }

    private static List<KeyValuePair<string, string>>? Read(string locale)
    {
        var name = ResourcePrefix + locale + ".json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null)
        {
            GD.PushError($"i18n: catalog '{name}' is not embedded");
            return null;
        }

        I18n catalog;
        try
        {
            using var reader = new StreamReader(stream);
            catalog = ContractSerializer.Deserialize<I18n>(reader.ReadToEnd());
        }
        catch (ContractViolationException e)
        {
            GD.PushError($"i18n: '{name}' violates schema '{e.Schema}': {string.Join("; ", e.Errors)}");
            return null;
        }

        if (LocaleId(catalog.Locale) != locale)
        {
            GD.PushError($"i18n: '{name}' declares another locale");
            return null;
        }

        // Beyond the schema: unique keys and no texts that are only whitespace.
        var entries = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Text) || !seen.Add(entry.Key))
            {
                GD.PushError($"i18n: '{name}' has a blank or duplicate entry ('{entry.Key}')");
                return null;
            }

            entries.Add(new KeyValuePair<string, string>(entry.Key, entry.Text));
        }

        return entries;
    }

    private static string LocaleId(Locale locale) => locale switch
    {
        Locale.De => "de",
        Locale.En => "en",
        _ => string.Empty,
    };

    private static bool SameKeys()
    {
        var sets = Loaded.Values.Select(e => e.Select(p => p.Key).ToHashSet(StringComparer.Ordinal)).ToList();
        if (sets.Skip(1).All(s => s.SetEquals(sets[0])))
        {
            return true;
        }

        GD.PushError("i18n: catalogs do not have the same keys");
        return false;
    }
}
