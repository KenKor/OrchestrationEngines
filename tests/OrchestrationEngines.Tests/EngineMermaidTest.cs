using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Mermaid;
using Xunit;
using static OrchestrationEngines.Tests.GraphEngine;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Tests for <see cref="EngineMermaidExtensions.ReportDependenciesMermaid"/>: every node has its own id, and names
/// and fail reasons are written so that Mermaid shows them as text.
/// </summary>
public class EngineMermaidTest
{
  private static readonly Regex NodeLine = new(@"^    (\S+)\[""(.*)""\]$");

  /// <summary>
  /// Task names that differ only in characters an id cannot hold ("A-B", "A B", "A_B") each get their own node, and
  /// an edge points at the node of the task it names.
  /// </summary>
  [Fact]
  public async Task ReportDependenciesMermaid_NamesThatMakeTheSameId_GetOneNodeEach()
  {
    var engine = await ReadyAsync(new GraphEngine(T("A-B"), T("A B"), T("A_B"), T("C", "A-B")));

    var lines = Lines(engine.ReportDependenciesMermaid());
    var ids = lines.Select(l => NodeLine.Match(l)).Where(m => m.Success)
      .ToDictionary(m => TaskName(m.Groups[2].Value), m => m.Groups[1].Value);

    ids.Should().HaveCount(4);
    ids.Values.Should().OnlyHaveUniqueItems();
    lines.Should().Contain($"    {ids["A-B"]} --> {ids["C"]}");
  }

  /// <summary>
  /// A task named "end" does not get the id "end", which Mermaid reads as the end of a subgraph.
  /// </summary>
  [Fact]
  public async Task ReportDependenciesMermaid_TaskNamedEnd_GetsAnotherId()
  {
    var engine = await ReadyAsync(new GraphEngine(T("end")));

    var node = Lines(engine.ReportDependenciesMermaid()).Select(l => NodeLine.Match(l)).Single(m => m.Success);

    node.Groups[1].Value.Should().NotBe("end");
  }

  /// <summary>
  /// Quotes, '#', '&lt;' and '&gt;' in a task name and in a fail reason are written as Mermaid entity codes, and a
  /// line break in a fail reason does not end the node's line.
  /// </summary>
  [Fact]
  public async Task ReportDependenciesMermaid_MarkupInNameAndReason_IsWrittenAsText()
  {
    const string name = "Say \"hi\" #1 <b>";
    var engine = new GraphEngine(T(name))
    {
      Work = (_, _, _) => throw new InvalidOperationException("Bad \"x\"\nsecond line"),
    };
    await engine.StartAsync();
    await engine.Tasks[name].InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.Tasks[name].IsFailed.Should().BeTrue();

    var lines = Lines(engine.ReportDependenciesMermaid());
    var labels = lines.Select(l => NodeLine.Match(l)).Where(m => m.Success).Select(m => m.Groups[2].Value).ToList();

    labels.Should().HaveCount(2, "one node for the task and one for its fail reason");
    labels.Should().Contain(l => l.StartsWith("Say #quot;hi#quot; #35;1 #lt;b#gt; (", StringComparison.Ordinal));
    labels.Should().Contain(l => l.Contains("Bad #quot;x#quot; second line"));
    labels.Should().NotContain(l => l.Contains("\"") || l.Contains("\\"));
    lines.Take(2).Should().Equal("```mermaid", "graph TD");
    lines.Last().Should().Be("```");
    lines.Skip(2).Take(lines.Length - 3).Should().OnlyContain(l => l.StartsWith("    ", StringComparison.Ordinal),
      "every node, edge and style is a line of its own");
  }

  // A task node's label is "<name> (<state>, <duration>)".
  private static string TaskName(string label) => label.Substring(0, label.LastIndexOf(" (", StringComparison.Ordinal));

  private static string[] Lines(string diagram) =>
    diagram.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
}
