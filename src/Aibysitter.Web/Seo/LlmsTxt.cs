using System.Text;
using Aibysitter.Packs;
using Aibysitter.Rules;
using Aibysitter.Rules.PullRequests;
using Aibysitter.Web.Infrastructure;
using Aibysitter.Web.Linting;

namespace Aibysitter.Web.Seo;

/// <summary>llms.txt (llmstxt.org): H1, summary blockquote, then H2 sections of links. Rules come from the docs.</summary>
public static class LlmsTxt
{
    public const string CliUrl = "https://www.nuget.org/packages/Aibysitter.Cli";

    public const string ActionUrl = "https://github.com/marketplace/actions/aibysitter-rules-lint";

    /// <summary>The only links outside the site.</summary>
    public static IReadOnlyList<string> ExternalLinks { get; } = [CliUrl, ActionUrl];

    public static string Build(SiteOptions site, PackCatalog packs)
    {
        var text = new StringBuilder();
        text.Append("# aibysitter\n\n");
        text.Append("> Lints the rules files AI coding agents read (CLAUDE.md, AGENTS.md, Cursor rules, Copilot instructions, GEMINI.md, .windsurfrules) and reviews agent-authored pull requests with a GitHub App. Free, no account.\n\n");
        text.Append($"Ruleset version: {RulesetVersion.Current}.\n\n");

        text.Append("## Tools\n\n");
        Link(text, site, "Lint", "/Lint", "paste a rules file or name a public GitHub repository; findings by line, a fix for each, and a score");
        Link(text, site, "API", "/API", $"POST {LintApi.Path} with JSON; same rules and results as the lint page; no key");
        Link(text, site, "Hooks", "/Hooks", "Git pre-commit and Claude Code hooks that run the aibysitter CLI on rules files");
        Link(text, site, "GitHub App", "/GitHub", "checks on agent-authored pull requests; install at " + Aibysitter.Web.GitHub.GitHubAppLinks.InstallUrl);
        Link(text, site, "Config generator", "/GitHub/Config", $"builds {RepoConfig.FilePath} for the GitHub App");
        text.Append($"- [CLI]({CliUrl}): `dotnet tool install --global Aibysitter.Cli` (nuget.org); lint, fix, init from packs\n");
        text.Append($"- [GitHub Action]({ActionUrl}): `jbailey2900/aibysitter@v1`, Marketplace `aibysitter-rules-lint`\n");
        text.Append('\n');

        text.Append("## Rules packs\n\n");
        foreach (var pack in packs.All)
        {
            Link(text, site, pack.Manifest.Title, $"/Packs#{pack.Id}", $"{pack.Manifest.Description} Compose with `aibysitter init --packs {pack.Id} --format claude`.");
        }

        text.Append($"- [Packs registry]({site.Url(RulesPacks.PackRegistryEndpoints.Path)}): JSON, schema version {PackCatalog.SchemaVersion}\n\n");

        Section(text, site, "Rules", RuleDocs.All);
        Section(text, site, "Pull request checks", PullRequestCheckDocs.All);

        text.Append("## Optional\n\n");
        Link(text, site, "Methodology", "/Rules/Methodology", "how rules are chosen and scored");
        Link(text, site, "Changelog", "/Rules/Changelog", "ruleset and check changes by version");
        Link(text, site, "Gallery", "/Gallery", "public-domain rules files, each scored");
        Link(text, site, "Incidents", "/Incidents", "postmortems of AI coding agent changes, with the checks that would have caught them; CC BY 4.0");
        Link(text, site, "Stats", "/Stats", "daily usage counts: lints, findings by rule, App reviews, badge requests");
        Link(text, site, "Privacy", "/Privacy", "what is read, kept and logged");
        return text.ToString();
    }

    private static void Section(StringBuilder text, SiteOptions site, string heading, IEnumerable<RuleDoc> docs)
    {
        text.Append($"## {heading}\n\n");
        foreach (var doc in docs)
        {
            Link(text, site, $"{doc.Id} {doc.Name}", SiteMap.RulePath(doc.Id), doc.Summary);
        }

        text.Append('\n');
    }

    private static void Link(StringBuilder text, SiteOptions site, string name, string path, string note) =>
        text.Append($"- [{name}]({site.Url(path)}): {note}\n");
}
