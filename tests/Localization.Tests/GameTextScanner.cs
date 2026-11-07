using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GlassWorld.Localization.Tests;

/// <summary>A place where the game shows text that is not a translation key.</summary>
/// <param name="File">Repository-relative path.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Text">The offending literal.</param>
public sealed record TextViolation(string File, int Line, string Text)
{
    /// <inheritdoc />
    public override string ToString() => $"{File}:{Line}: \"{Text}\"";
}

/// <summary>A translation key referenced by a scene or by code.</summary>
/// <param name="File">Repository-relative path.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Key">The key.</param>
public sealed record KeyUse(string File, int Line, string Key);

/// <summary>Result of scanning one file.</summary>
/// <param name="Violations">Player-facing literals that are not keys.</param>
/// <param name="Keys">Keys referenced in the file.</param>
public sealed record ScanResult(IReadOnlyList<TextViolation> Violations, IReadOnlyList<KeyUse> Keys);

/// <summary>
/// Finds player-facing text in Godot scenes and C# code that bypasses the translation keys (KR4,
/// ADR-0007). Exceptions by design: ids, log output and prompts never reach a UI text property, so
/// they are not inspected. Text that is built at runtime (for example from a key plus a vocabulary
/// id) cannot be checked statically and is the reviewer's job.
/// </summary>
public static partial class GameTextScanner
{
    /// <summary>Godot properties (scene file names) that show text to the player.</summary>
    private static readonly string[] SceneTextProperties = ["text", "tooltip_text", "placeholder_text", "title"];

    /// <summary>The same properties as C# members of Godot nodes.</summary>
    private static readonly HashSet<string> CodeTextProperties = new(StringComparer.Ordinal) { "Text", "TooltipText", "PlaceholderText", "Title" };

    /// <summary>Setter methods and translation calls whose string arguments must be keys.</summary>
    private static readonly HashSet<string> KeyOnlyMethods = new(StringComparer.Ordinal)
    {
        "SetText", "SetTooltipText", "SetPlaceholder", "SetTitle", "Tr", "TrN", "Translate", "TranslatePlural",
    };

    /// <summary>True if the whole string is a translation key.</summary>
    public static bool IsKey(string value) => KeyPattern().IsMatch(value);

    /// <summary>Scans the content of a Godot text scene (.tscn).</summary>
    public static ScanResult ScanScene(string file, string content)
    {
        var violations = new List<TextViolation>();
        var keys = new List<KeyUse>();
        foreach (Match m in SceneProperty().Matches(content))
        {
            if (!SceneTextProperties.Contains(m.Groups["prop"].Value, StringComparer.Ordinal))
            {
                continue;
            }

            var value = UnescapeScene(m.Groups["value"].Value);
            var line = content.AsSpan(0, m.Index).Count('\n') + 1;
            if (value.Length == 0)
            {
                continue;
            }

            if (IsKey(value))
            {
                keys.Add(new KeyUse(file, line, value));
            }
            else
            {
                violations.Add(new TextViolation(file, line, value));
            }
        }

        return new ScanResult(violations, keys);
    }

    /// <summary>Scans a C# source file of the game or the presentation layer.</summary>
    public static ScanResult ScanCode(string file, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var violations = new List<TextViolation>();
        var keys = new List<KeyUse>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment when CodeTextProperties.Contains(TargetName(assignment.Left)):
                    CheckValue(assignment.Right);
                    break;
                case InvocationExpressionSyntax invocation when KeyOnlyMethods.Contains(MethodName(invocation.Expression)):
                    foreach (var argument in invocation.ArgumentList.Arguments)
                    {
                        CheckValue(argument.Expression);
                    }

                    break;
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) && IsKey(literal.Token.ValueText):
                    keys.Add(new KeyUse(file, Line(literal), literal.Token.ValueText));
                    break;
                default:
                    break;
            }
        }

        return new ScanResult(violations, keys);

        void CheckValue(ExpressionSyntax expression)
        {
            // Only literals that end up as the shown text are checked; Tr(...) and variables pass.
            foreach (var literal in DirectLiterals(expression))
            {
                switch (literal)
                {
                    case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } s when s.Token.ValueText.Length > 0 && !IsKey(s.Token.ValueText):
                        violations.Add(new TextViolation(file, Line(s), s.Token.ValueText));
                        break;
                    case InterpolatedStringExpressionSyntax i:
                        var text = string.Concat(i.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText));
                        if (text.Any(char.IsLetter))
                        {
                            violations.Add(new TextViolation(file, Line(i), i.ToString()));
                        }

                        break;
                    default:
                        break;
                }
            }
        }
    }

    /// <summary>String literals that are the value itself or a branch/operand of it (not arguments of calls).</summary>
    private static IEnumerable<ExpressionSyntax> DirectLiterals(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax or InterpolatedStringExpressionSyntax:
                yield return expression;
                break;
            case ParenthesizedExpressionSyntax p:
                foreach (var e in DirectLiterals(p.Expression))
                {
                    yield return e;
                }

                break;
            case ConditionalExpressionSyntax c:
                foreach (var e in DirectLiterals(c.WhenTrue).Concat(DirectLiterals(c.WhenFalse)))
                {
                    yield return e;
                }

                break;
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var e in DirectLiterals(b.Left).Concat(DirectLiterals(b.Right)))
                {
                    yield return e;
                }

                break;
            default:
                break;
        }
    }

    /// <summary>Godot scene strings escape quotes, backslashes and line breaks C-style.</summary>
    private static string UnescapeScene(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                result.Append(value[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => value[i],
                });
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }

    private static string TargetName(ExpressionSyntax left) => left switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string MethodName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // Same shape as the key pattern in contracts/schemas/i18n.schema.json.
    [GeneratedRegex(@"^(vocab|ui|msg|time)(\.[a-z][a-z0-9_]*){2,4}$")]
    private static partial Regex KeyPattern();

    // prop = "value" at the start of a line; value may span lines and contain escaped quotes.
    [GeneratedRegex("""^(?<prop>[a-z_]+) = "(?<value>(?:[^"\\]|\\.)*)"$""", RegexOptions.Multiline)]
    private static partial Regex SceneProperty();
}
