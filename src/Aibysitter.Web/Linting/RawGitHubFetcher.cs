using System.Net;
using System.Text;

namespace Aibysitter.Web.Linting;

public enum FetchStatus
{
    Found,
    NotFound,
    TooLarge,
    TimedOut,
    Unreachable,

    /// <summary>The file is a link whose target is missing.</summary>
    LinkTargetMissing,

    /// <summary>The file links to another link; links are followed one level.</summary>
    LinkTooDeep,

    /// <summary>The file (or its link target) is not valid UTF-8; it is not linted.</summary>
    NotUtf8,
}

/// <param name="FileName">The supported file chosen, when one was found.</param>
/// <param name="OtherFiles">Other supported files present on the default branch, in fixed order.</param>
/// <param name="LinkTarget">When <see cref="FileName"/> is a symlink: the repository path it points to, whose content is <see cref="Content"/>.</param>
/// <param name="NextLink">For <see cref="FetchStatus.LinkTooDeep"/>: where the target itself points.</param>
public sealed record FetchResult(FetchStatus Status, string? FileName, string? Content, IReadOnlyList<string> OtherFiles, string? LinkTarget = null, string? NextLink = null);

/// <summary>
/// Reads a rules file from raw.githubusercontent.com, default branch only. Every supported name is probed in parallel;
/// the preferred name wins when present, otherwise the first in <see cref="FileNames"/> order. Redirects are followed
/// only within raw.githubusercontent.com.
/// </summary>
public sealed class RawGitHubFetcher(HttpClient http)
{
    public const string Host = "raw.githubusercontent.com";
    public const int MaxBytes = 100 * 1024;
    /// <summary>Redirects followed per <see cref="FetchAsync"/>, shared by every probe and the link follow.</summary>
    public const int MaxRedirects = 3;

    /// <summary>Redirect hops left in one fetch.</summary>
    private sealed class RedirectBudget
    {
        private int remaining = MaxRedirects;

        public bool TryTake() => Interlocked.Decrement(ref remaining) >= 0;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static readonly IReadOnlyList<string> FileNames =
        ["CLAUDE.md", "AGENTS.md", ".github/copilot-instructions.md", "GEMINI.md", ".cursorrules", ".windsurfrules"];

    private enum ProbeState
    {
        Missing,
        Found,
        TooLarge,
        NotUtf8,
        TimedOut,
        Failed,
    }

    private sealed record Probe(string FileName, ProbeState State, string? Content);

    /// <summary>Total time allowed for all probes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public static Uri RawUrl(RepoRef repo, string fileName) => new($"https://{Host}/{repo.Owner}/{repo.Repo}/HEAD/{fileName}");

    public async Task<FetchResult> FetchAsync(RepoRef repo, string? preferred, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Timeout);
        var redirects = new RedirectBudget();
        var probes = await Task.WhenAll(FileNames.Select(name => ProbeAsync(RawUrl(repo, name), name, redirects, budget.Token)));
        cancellationToken.ThrowIfCancellationRequested();
        var chosen = Choose(probes, preferred);
        return chosen.Status == FetchStatus.Found && LinkPath.TryResolve(chosen.FileName!, chosen.Content!, out var target)
            ? await FollowAsync(repo, chosen, target, redirects, budget.Token)
            : chosen;
    }

    /// <summary>Fetches a symlink's target once, inside the same time budget. A target that is itself a link is not followed.</summary>
    private async Task<FetchResult> FollowAsync(RepoRef repo, FetchResult link, string target, RedirectBudget redirects, CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(RawUrl(repo, target), target, redirects, cancellationToken);
        var result = link with { Content = null, LinkTarget = target };
        return probe.State switch
        {
            ProbeState.Found when LinkPath.TryResolve(target, probe.Content!, out var next) => result with { Status = FetchStatus.LinkTooDeep, NextLink = next },
            ProbeState.Found => result with { Content = probe.Content },
            ProbeState.TooLarge => result with { Status = FetchStatus.TooLarge },
            ProbeState.NotUtf8 => result with { Status = FetchStatus.NotUtf8 },
            ProbeState.TimedOut => result with { Status = FetchStatus.TimedOut },
            ProbeState.Failed => result with { Status = FetchStatus.Unreachable },
            _ => result with { Status = FetchStatus.LinkTargetMissing },
        };
    }

    private static FetchResult Choose(Probe[] probes, string? preferred)
    {
        var present = probes.Where(p => p.State is ProbeState.Found or ProbeState.TooLarge or ProbeState.NotUtf8).ToList();
        var chosen = present.FirstOrDefault(p => p.FileName == preferred) ?? present.FirstOrDefault();
        if (chosen is null)
        {
            return new FetchResult(EmptyStatus(probes), null, null, []);
        }

        var others = present.Where(p => p != chosen).Select(p => p.FileName).ToList();
        return chosen.State switch
        {
            ProbeState.Found => new FetchResult(FetchStatus.Found, chosen.FileName, chosen.Content, others),
            ProbeState.NotUtf8 => new FetchResult(FetchStatus.NotUtf8, chosen.FileName, null, others),
            _ => new FetchResult(FetchStatus.TooLarge, chosen.FileName, null, others),
        };
    }

    private static FetchStatus EmptyStatus(Probe[] probes) =>
        probes.Any(p => p.State == ProbeState.TimedOut) ? FetchStatus.TimedOut
        : probes.Any(p => p.State == ProbeState.Failed) ? FetchStatus.Unreachable
        : FetchStatus.NotFound;

    private async Task<Probe> ProbeAsync(Uri url, string fileName, RedirectBudget redirects, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!IsRedirect(response.StatusCode))
                {
                    return await ReadAsync(response, fileName, cancellationToken);
                }

                if (NextHop(url, response.Headers.Location) is not { } next || !redirects.TryTake())
                {
                    return new Probe(fileName, ProbeState.Missing, null);
                }

                url = next;
            }
        }
        catch (OperationCanceledException)
        {
            return new Probe(fileName, ProbeState.TimedOut, null);
        }
        catch (HttpRequestException)
        {
            return new Probe(fileName, ProbeState.Failed, null);
        }
    }

    private static async Task<Probe> ReadAsync(HttpResponseMessage response, string fileName, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new Probe(fileName, ProbeState.Missing, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            return new Probe(fileName, ProbeState.Failed, null);
        }

        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            return new Probe(fileName, ProbeState.TooLarge, null);
        }

        var bytes = await ReadCappedAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
        if (bytes is null)
        {
            return new Probe(fileName, ProbeState.TooLarge, null);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return new Probe(fileName, ProbeState.NotUtf8, null);
        }


        return text.Length > LintLimits.MaxContentLength
            ? new Probe(fileName, ProbeState.TooLarge, null)
            : new Probe(fileName, ProbeState.Found, text);
    }

    /// <summary>The body, or null when it exceeds <see cref="MaxBytes"/>.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>The redirect target when it stays on https://raw.githubusercontent.com; otherwise null.</summary>
    private static Uri? NextHop(Uri current, Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        var next = location.IsAbsoluteUri ? location : new Uri(current, location);
        return next.Scheme == Uri.UriSchemeHttps && next.Host == Host && next.IsDefaultPort && next.UserInfo.Length == 0 ? next : null;
    }
}
