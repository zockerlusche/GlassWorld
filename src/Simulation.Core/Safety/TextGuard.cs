using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Simulation.Safety;

/// <summary>Result of a content check.</summary>
/// <param name="Allowed">True if the text passes the deterministic check.</param>
/// <param name="Category">Blocked category (rules data), null if allowed or too long.</param>
/// <param name="Reason"><c>term</c>, <c>pattern</c> or <c>too_long</c> when blocked.</param>
/// <param name="Match">What matched (for the log, never shown to the player).</param>
public sealed record TextVerdict(bool Allowed, string? Category = null, string? Reason = null, string? Match = null)
{
    /// <summary>The verdict for an unremarkable text.</summary>
    public static readonly TextVerdict Pass = new(true);
}

/// <summary>
/// Deterministic content check for player and inhabitant texts (B6 R13, B2472, ages 12+), the core's
/// part of the two-stage check (ADR-0005). Same algorithm and data as the AI service
/// (<c>ai-service/src/glassworld_ai/safety.py</c>, <c>content/safety/rules.json</c>), proven by the
/// shared edge cases in <c>tests/safety/cases.json</c>:
/// a platform-independent fold (full-width forms → ASCII, a/o/u + combining diaeresis → umlaut, other
/// combining marks and invisible format characters removed, control characters → spaces) and lower case
/// – no Unicode normalisation library, because invariant globalization would skip it in tests;
/// too long → blocked; category patterns on that text; words (letters/digits, <c>*</c> = any letter)
/// in four variants (plain, leetspeak, each with runs of single letters joined); allowed phrases
/// removed; terms of both languages matched as whole-word sequences; the longest match decides.
/// </summary>
public sealed class TextGuard
{
    private readonly int _maxLength;
    private readonly Dictionary<char, char> _leet;
    private readonly List<string[]> _allowed;
    private readonly List<(string Category, List<string[]> Terms, List<Regex> Patterns)> _categories;

    private TextGuard(SafetyRules rules)
    {
        _maxLength = rules.MaxLength;
        _leet = rules.Leet.ToDictionary(m => m.From[0], m => m.To[0]);
        _allowed = [.. rules.AllowedWords.Select(Split)];
        _categories =
        [
            .. rules.Categories.Select(c => (
                Name(c.Id),
                c.TermsDe.Concat(c.TermsEn).Select(Split).ToList(),
                c.Patterns.Select(p => new Regex(p, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)).ToList())),
        ];
    }

    /// <summary>Parses and validates <c>content/safety/rules.json</c>.</summary>
    public static TextGuard FromJson(string json) => new(ContractSerializer.Deserialize<SafetyRules>(json));

    /// <summary>The fold described above, then lower case.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var category = char.GetUnicodeCategory(ch);
            if (ch is >= '\uFF01' and <= '\uFF5E')
            {
                result.Append((char)(ch - 0xFEE0));
            }
            else if (ch == '\u0308' && result.Length > 0 && Umlaut(result[^1]) is { } umlaut)
            {
                result[^1] = umlaut;
            }
            else if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
            {
                continue;
            }
            else
            {
                result.Append(category == UnicodeCategory.Control ? ' ' : ch);
            }
        }

#pragma warning disable CA1308 // Lower case on purpose: same rule as the Python side (str.lower keeps ß).
        return result.ToString().ToLowerInvariant();
#pragma warning restore CA1308
    }

    private static char? Umlaut(char c) => c switch
    {
        'a' => 'ä',
        'o' => 'ö',
        'u' => 'ü',
        'A' => 'Ä',
        'O' => 'Ö',
        'U' => 'Ü',
        _ => null,
    };

    /// <summary>Deterministic verdict for one text.</summary>
    public TextVerdict Check(string text)
    {
        var normalized = Normalize(text);
        if (normalized.EnumerateRunes().Count() > _maxLength)
        {
            return new TextVerdict(false, Reason: "too_long");
        }

        var plain = Words(normalized);
        var leet = Words(new string([.. normalized.Select(c => _leet.TryGetValue(c, out var to) ? to : c)]));
        var variants = new List<List<string>> { plain, leet, JoinSingleLetters(plain), JoinSingleLetters(leet) };
        foreach (var phrase in _allowed)
        {
            variants = [.. variants.Select(v => RemovePhrase(v, phrase))];
        }

        (int Length, int Order, TextVerdict Verdict)? best = null;
        for (var order = 0; order < _categories.Count; order++)
        {
            var (category, terms, patterns) = _categories[order];
            foreach (var pattern in patterns)
            {
                var found = pattern.Match(normalized);
                if (found.Success && Better(1, order, best))
                {
                    best = (1, order, new TextVerdict(false, category, "pattern", found.Value));
                }
            }

            foreach (var term in terms)
            {
                if (variants.Any(v => Contains(v, term)) && Better(term.Length, order, best))
                {
                    best = (term.Length, order, new TextVerdict(false, category, "term", string.Join(' ', term)));
                }
            }
        }

        return best?.Verdict ?? TextVerdict.Pass;
    }

    /// <summary>Longer match wins; on equal length the earlier category in the data wins.</summary>
    private static bool Better(int length, int order, (int Length, int Order, TextVerdict Verdict)? best) =>
        best is null || length > best.Value.Length || (length == best.Value.Length && order < best.Value.Order);

    private static string[] Split(string phrase) => phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '*')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static List<string> JoinSingleLetters(List<string> words)
    {
        var result = new List<string>();
        var run = new List<string>();
        foreach (var word in words)
        {
            if (word.Length == 1 && char.IsLetter(word[0]))
            {
                run.Add(word);
                continue;
            }

            Flush();
            result.Add(word);
        }

        Flush();
        return result;

        void Flush()
        {
            if (run.Count > 1)
            {
                result.Add(string.Concat(run));
            }
            else
            {
                result.AddRange(run);
            }

            run.Clear();
        }
    }

    private static bool WordEquals(string token, string termWord) =>
        token.Length == termWord.Length && token.Zip(termWord).All(p => p.First == p.Second || (p.First == '*' && char.IsLetter(p.Second)));

    private static bool Contains(List<string> tokens, string[] term)
    {
        for (var start = 0; start + term.Length <= tokens.Count; start++)
        {
            var match = true;
            for (var i = 0; i < term.Length && match; i++)
            {
                match = WordEquals(tokens[start + i], term[i]);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> RemovePhrase(List<string> tokens, string[] phrase)
    {
        var result = new List<string>();
        for (var i = 0; i < tokens.Count;)
        {
            if (i + phrase.Length <= tokens.Count && phrase.Select((w, k) => tokens[i + k] == w).All(x => x))
            {
                i += phrase.Length;
                continue;
            }

            result.Add(tokens[i]);
            i++;
        }

        return result;
    }

    private static string Name(SafetyCategoryId id) => id switch
    {
        SafetyCategoryId.Sexual => "sexual",
        SafetyCategoryId.Violence => "violence",
        SafetyCategoryId.Self_harm => "self_harm",
        SafetyCategoryId.Hate => "hate",
        SafetyCategoryId.Personal_data => "personal_data",
        SafetyCategoryId.Drugs => "drugs",
        SafetyCategoryId.Injection => "injection",
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "unknown safety category"),
    };
}
