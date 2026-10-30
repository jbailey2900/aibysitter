using System.Globalization;
using System.Text.RegularExpressions;

namespace Aibysitter.Rules.PullRequests;

/// <summary>
/// An exact version (<c>1.2.3</c> or <c>[1.2.3]</c>) or a NuGet interval: <c>[a,b]</c>, <c>[a,b)</c>, <c>(a,b]</c>, <c>(a,b)</c>,
/// <c>[a,)</c>, <c>(,b]</c>. A bare version is exact (NuGet reads it as a minimum).
/// </summary>
public sealed partial record VersionRange(PackageVersion? Min, bool MinInclusive, PackageVersion? Max, bool MaxInclusive, string Text)
{
    public static bool TryParse(string? text, out VersionRange? range)
    {
        range = null;
        var spec = text?.Trim();
        if (string.IsNullOrEmpty(spec))
        {
            return false;
        }

        if (PackageVersion.TryParse(spec, out var exact))
        {
            range = new(exact, true, exact, true, spec);
            return true;
        }

        var m = IntervalRegex().Match(spec);
        if (!m.Success)
        {
            return false;
        }

        var minText = m.Groups["min"].Value.Trim();
        var maxText = m.Groups["max"].Value.Trim();
        PackageVersion? min = null, max = null;
        if ((minText.Length > 0 && !PackageVersion.TryParse(minText, out min)) || (maxText.Length > 0 && !PackageVersion.TryParse(maxText, out max)))
        {
            return false;
        }

        if (!m.Groups["comma"].Success)
        {
            // [1.2.3]: exact; (1.2.3) and an empty [] are not valid.
            if (m.Groups["open"].Value != "[" || m.Groups["close"].Value != "]" || min is null)
            {
                return false;
            }

            range = new(min, true, min, true, spec);
            return true;
        }

        if ((min is null && max is null) || (min is not null && max is not null && min.CompareTo(max) > 0))
        {
            return false;
        }

        range = new(min, m.Groups["open"].Value == "[", max, m.Groups["close"].Value == "]", spec);
        return true;
    }

    public bool Contains(PackageVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var aboveMin = Min is null || (MinInclusive ? version.CompareTo(Min) >= 0 : version.CompareTo(Min) > 0);
        var belowMax = Max is null || (MaxInclusive ? version.CompareTo(Max) <= 0 : version.CompareTo(Max) < 0);
        return aboveMin && belowMax;
    }

    public override string ToString() => Text;

    [GeneratedRegex(@"^(?<open>[\[(])(?<min>[^,\])]*)(?<comma>,(?<max>[^\])]*))?(?<close>[\])])$")]
    private static partial Regex IntervalRegex();
}

/// <summary>1–4 numeric parts and an optional <c>-prerelease</c>; missing parts are 0; a prerelease sorts before its release.</summary>
public sealed partial record PackageVersion(IReadOnlyList<int> Parts, string? Prerelease) : IComparable<PackageVersion>
{
    public static bool TryParse(string? text, out PackageVersion? version)
    {
        version = null;
        var m = VersionRegex().Match(text?.Trim() ?? string.Empty);
        if (!m.Success)
        {
            return false;
        }

        var parts = new List<int>();
        foreach (var part in m.Groups["parts"].Value.Split('.'))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }

            parts.Add(n);
        }

        version = new(parts, m.Groups["pre"].Success ? m.Groups["pre"].Value : null);
        return true;
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < 4; i++)
        {
            var c = Part(i).CompareTo(other.Part(i));
            if (c != 0)
            {
                return c;
            }
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Prerelease, other.Prerelease),
        };
    }

    private int Part(int i) => i < Parts.Count ? Parts[i] : 0;

    [GeneratedRegex(@"^(?<parts>\d+(?:\.\d+){0,3})(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionRegex();
}
