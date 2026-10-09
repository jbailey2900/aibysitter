using System.Text;

namespace Aibysitter.Web.GitHub;

/// <summary>A file read from GitHub. <see cref="Text"/> is null when it was not decoded; <see cref="Skipped"/> says why.</summary>
public sealed record FileContent(string? Text, long Size, string? Skipped)
{
    public const int MaxFileBytes = 100 * 1024;
    public const string TooLarge = "over 100 KB";
    public const string NotUtf8 = "not UTF-8";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Decodes <paramref name="bytes"/> as strict UTF-8 unless <paramref name="size"/> is over <see cref="MaxFileBytes"/>.</summary>
    public static FileContent From(long size, Func<byte[]> bytes)
    {
        if (size > MaxFileBytes)
        {
            return new FileContent(null, size, TooLarge);
        }

        try
        {
            return new FileContent(StrictUtf8.GetString(bytes()).TrimStart('﻿'), size, null);
        }
        catch (DecoderFallbackException)
        {
            return new FileContent(null, size, NotUtf8);
        }
    }

    public static FileContent FromText(string text) => From(Encoding.UTF8.GetByteCount(text), () => Encoding.UTF8.GetBytes(text));
}

/// <summary>Bytes of file content one review may read; files that are skipped or past the budget are recorded.</summary>
public sealed class ContentBudget
{
    public const long MaxJobBytes = 2 * 1024 * 1024;
    public const string LimitReached = "2 MB review limit reached";

    private readonly List<(string Path, string Reason)> skipped = [];
    private long used;
    private bool exhausted;

    public IReadOnlyList<(string Path, string Reason)> Skipped => skipped;

    /// <summary>The file's text, or null when it is missing, skipped, or would pass <see cref="MaxJobBytes"/>.</summary>
    public async Task<string?> ReadAsync(string path, Func<Task<FileContent?>> fetch)
    {
        if (exhausted)
        {
            skipped.Add((path, LimitReached));
            return null;
        }

        var content = await fetch();
        if (content is null)
        {
            return null;
        }

        if (content.Skipped is { } reason)
        {
            skipped.Add((path, reason));
            return null;
        }

        if (used + content.Size > MaxJobBytes)
        {
            exhausted = true;
            skipped.Add((path, LimitReached));
            return null;
        }

        used += content.Size;
        return content.Text;
    }

    /// <summary>One summary note per reason: "Not read (over 100 KB): a.cs, b.cs." with at most 10 names.</summary>
    public IEnumerable<string> Notes() =>
        skipped.GroupBy(s => s.Reason).Select(g =>
        {
            var names = g.Select(s => s.Path).Distinct(StringComparer.Ordinal).ToList();
            var listed = string.Join(", ", names.Take(10));
            return $"Not read ({g.Key}): {listed}{(names.Count > 10 ? $", and {names.Count - 10} more" : "")}.";
        });
}
