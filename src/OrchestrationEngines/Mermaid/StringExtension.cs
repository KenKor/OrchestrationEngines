using System.Text;

namespace OrchestrationEngines.Mermaid;

/// <summary>
/// An extension on strings to to produce mermaid-safe node identifier.
/// </summary>
public static class StringExtension
{
    /// <summary>
    /// Converts a string into a Mermaid-safe node identifier.
    /// Only letters, digits, and underscores are allowed.
    /// </summary>
    /// <param name="identifier">The identifier.</param>
    /// <param identifier="identifier"></param>
    /// <summary>The sanitized version <paramref name="identifier"/>.</summary>
    public static string ForMermaid(this string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return "_";

        var sb = new StringBuilder(identifier.Length);

        foreach (var c in identifier)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');

        return sb.ToString();
    }
}
