using System.Diagnostics;

namespace Aibysitter.Rules.Tests;

internal static class Timing
{
    public const int LimitMs = 100;

    /// <summary>Best of three runs after one warm-up, in milliseconds.</summary>
    public static double BestOfThree(System.Action action)
    {
        action();
        var best = double.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    public static void AssertFast(string name, System.Action action)
    {
        var ms = BestOfThree(action);
        Assert.True(ms < LimitMs, $"{name}: {ms:F0} ms (limit {LimitMs} ms)");
    }
}
