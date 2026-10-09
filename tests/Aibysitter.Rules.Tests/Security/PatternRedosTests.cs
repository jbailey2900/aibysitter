using System.Reflection;
using System.Text.RegularExpressions;
using Aibysitter.Rules.Tests.Parity;
using Xunit.Abstractions;

namespace Aibysitter.Rules.Tests;

/// <summary>Each fixed pattern on its adversarial 100,000-character line, in .NET and, for exported patterns, in node.</summary>
public class PatternRedosTests(ITestOutputHelper output)
{
    public static TheoryData<string, string> Cases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var p in SecurityInputs.Patterns)
            {
                data.Add(p.Key, p.Name);
            }

            return data;
        }
    }

    internal static Regex Pattern(string key)
    {
        var (typeName, method) = (key[..key.IndexOf('.')], key[(key.IndexOf('.') + 1)..]);
        var type = typeof(LintEngine).Assembly.GetTypes().Single(t => t.Name == typeName && !t.IsNested);
        return (Regex)type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, null)!;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DotNet_MatchesFast(string key, string name)
    {
        var regex = Pattern(key);
        var text = SecurityInputs.Patterns.Single(p => p.Name == name).Text;

        Timing.AssertFast($"{key} on {name}", () => regex.Matches(text).Count.ToString());
    }

    [Fact]
    public void Node_ExportedPatternsMatchFast()
    {
        if (NodeRunner.FindNodeOrSkip(output) is not { } node)
        {
            return;
        }

        var exported = SecurityInputs.Patterns.Where(p => p.Exported).ToList();
        var times = NodeRunner.TimePatterns(node, exported.Select(p => (p.Key, p.Text)).ToList());

        var slow = exported.Zip(times).Where(t => t.Second >= Timing.LimitMs).Select(t => $"{t.First.Key} on {t.First.Name}: {t.Second:F0} ms").ToList();
        Assert.True(slow.Count == 0, string.Join("; ", slow));
    }

    [Fact]
    public void Node_ReportInputsAndTrailingWhitespace_LintFast()
    {
        if (NodeRunner.FindNodeOrSkip(output) is not { } node)
        {
            return;
        }

        var inputs = SecurityInputs.Report.Append(new SecurityInputs.Input("trailing-ws", new string(' ', SecurityInputs.Long) + "x")).ToList();
        var times = NodeRunner.TimeLint(node, inputs.Select(i => "# T\n\n## S\n" + i.Text).ToList());

        var slow = inputs.Zip(times).Where(t => t.Second >= Timing.LimitMs).Select(t => $"{t.First.Name}: {t.Second:F0} ms").ToList();
        Assert.True(slow.Count == 0, string.Join("; ", slow));
    }
}
