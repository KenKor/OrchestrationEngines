using System.Text;

namespace OrchestrationEngines.Mermaid;

/// <summary>
/// Extensions on strings to produce Mermaid-safe node identifiers and labels.
/// </summary>
internal static class StringExtension
{
  /// <summary>
  /// Converts a string into a Mermaid-safe node identifier.
  /// Only letters, digits, and underscores are allowed.
  /// </summary>
  /// <param name="identifier">The identifier.</param>
  /// <returns>The sanitized version of <paramref name="identifier"/>; an underscore when it is empty.</returns>
  internal static string ForMermaid(this string identifier)
  {
    if (string.IsNullOrWhiteSpace(identifier))
      return "_";

    var sb = new StringBuilder(identifier.Length);

    foreach (var c in identifier)
      sb.Append(char.IsLetterOrDigit(c) ? c : '_');

    return sb.ToString();
  }

  /// <summary>
  /// Writes <paramref name="text"/> for a quoted Mermaid label: '#', quotes, backticks, '&lt;' and '&gt;' as Mermaid
  /// entity codes, so they show as text, and a line break as a space, so the node stays on one line.
  /// </summary>
  internal static string ForMermaidLabel(this string text) => text
    .Replace("#", "#35;")
    .Replace("\"", "#quot;")
    .Replace("`", "#96;")
    .Replace("<", "#lt;")
    .Replace(">", "#gt;")
    .Replace("\r\n", " ")
    .Replace('\r', ' ')
    .Replace('\n', ' ');
}
