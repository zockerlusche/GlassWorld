using System.Globalization;
using System.Text;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;

namespace GlassWorld.Simulation.Headless;

/// <summary>
/// Machine-readable lines for core events and chronicle entries (D9 tooling): <c>key=value</c> pairs
/// separated by single spaces, invariant culture. A missing value is <c>-</c>; inside values, <c>%</c>,
/// whitespace and control characters are percent-encoded (a space becomes <c>%20</c>), so every line
/// splits on spaces.
/// </summary>
internal static class EventLines
{
    /// <summary><c>EVENT tick=.. kind=.. inhabitant=.. action=.. target=.. detail=..</c></summary>
    public static string Event(WorldEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return string.Create(CultureInfo.InvariantCulture, $"EVENT tick={e.Tick} kind={Escape(e.Kind)} inhabitant={Escape(e.InhabitantId)} action={Escape(e.Action)} target={Escape(e.TargetId)} detail={Escape(e.Detail)}");
    }

    /// <summary><c>CHRONICLE tick=.. kind=.. id=..</c> (id = the entry's running number).</summary>
    public static string Chronicle(ChronicleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Create(CultureInfo.InvariantCulture, $"CHRONICLE tick={record.Tick} kind={Escape(record.Kind)} id={record.Seq}");
    }

    /// <summary>Escapes one value (see the class summary).</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var plain = true;
        foreach (var c in value)
        {
            if (c == '%' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                plain = false;
                break;
            }
        }

        if (plain)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c == '%' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
