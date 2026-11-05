using System.Text;

namespace OrchestrationEngines.Mermaid;

/// <summary>
/// An extension on strings to produce Mermaid-safe node identifiers.
/// </summary>
public static class StringExtension
{
  /// <summary>
  /// Converts a string into a Mermaid-safe node identifier.
  /// Only letters, digits, and underscores are allowed.
  /// </summary>
  /// <param name="identifier">The identifier.</param>
  /// <returns>The sanitized version of <paramref name="identifier"/>; an underscore when it is empty.</returns>
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
