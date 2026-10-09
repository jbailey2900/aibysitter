![aibysitter: Babysitting the AI.](docs/banner.png)

# Aibysitter

Babysitting the AI. Tooling for supervising AI coding agents: it reviews what an agent wrote after the fact. It does not run or constrain agents at runtime.

Live at [aibysitting.net](https://aibysitting.net). Free, no accounts.

## Rules-file linter

Lints `CLAUDE.md`, `AGENTS.md`, and `GEMINI.md` (any directory), Cursor rules (`.cursor/rules/*.mdc`, root `.cursorrules`), `.github/copilot-instructions.md`, and root `.windsurfrules`. The format is auto-detected or chosen on the page. Frontmatter is skipped by every rule except R009, R015 and R016. Paste a file at [aibysitting.net/Lint](https://aibysitting.net/Lint). With JavaScript on, the file is linted in the browser and not sent; without it, the server lints it. A public repository can be linted by URL: the first of CLAUDE.md, AGENTS.md, .github/copilot-instructions.md, GEMINI.md, .cursorrules, .windsurfrules on its default branch, fetched from raw.githubusercontent.com.

| ID | Rule | Severity |
|---|---|---|
| R001 | RationaleProse | Info |
| R002 | VagueVerbs | Warning |
| R003 | ContradictoryModals | Error |
| R004 | FileLength (over 200 lines) | Warning |
| R005 | DuplicateLines | Warning |
| R007 | HedgedInstructions | Warning |
| R008 | EmphasisInflation (over 5 per 100 lines, at least 3 allowed) | Warning |
| R009 | SecretsInRulesFile | Error |
| R010 | PersonaPreamble | Info |
| R011 | EmptySections | Warning |
| R012 | UnclosedCodeFence | Error |
| R013 | ProseParagraph (over 80 words) | Info |
| R014 | UnverifiableCrossReference | Info |
| R015 | FrontmatterFields (Cursor .mdc only) | Warning |
| R016 | ManualCursorRule (Cursor .mdc only) | Info |

R006 (MissingIdentifiers, Warning) is App-only and runs through P014: it checks that paths, package scripts, make targets, and MSBuild targets a rules file names exist in the repository.

Suppress a rule with an HTML comment on its own line: `<!-- aibysitter-disable R002 -->` (whole file) or `<!-- aibysitter-disable-next-line R002 -->` (next line). Suppressed findings are listed and not scored.

Aibysitter lint score: 100, minus 10 per Error, 4 per Warning, 1 per Info. Each rule deducts at most 30. Total deductions per severity are capped: Error 40, Warning 30, Info 10, so the lowest possible score is 20. Grades: A ≥ 90, B ≥ 80, C ≥ 70, D ≥ 60, F below.

Rules: [aibysitting.net/Rules](https://aibysitting.net/Rules). Ruleset version and changes: [aibysitting.net/Rules/Changelog](https://aibysitting.net/Rules/Changelog). Methodology: [aibysitting.net/Rules/Methodology](https://aibysitting.net/Rules/Methodology).

## GitHub App

Install the App, add a three-line config, and make **Aibysitter review** a required check. A pull request that adds an unpinned action, a new dependency, a leaked key or a security exemption then cannot merge until a human looks.

```json
{
  "conclusion": "fail-on-warnings"
}
```

Save it as `.github/aibysitter.json` on the default branch, then require the **Aibysitter review** status check on that branch (**Settings → Rules → Rulesets**, or **Settings → Branches**).

- `fail-on-errors` is the lighter setting: only Error findings block.
- `advisory` is the default and the trial mode: every finding is an annotation and nothing blocks.
- Bot dependency bumps (Dependabot, Renovate) block too, by design: a bot bumping a dependency is a change a human should sign off on.

Every review posts a check named `Aibysitter review`, with an annotation on each flagged line.

| ID | Check | Severity |
|---|---|---|
| P001 | PlaceholderIdentifiers | Error |
| P002 | TodoStubs | Warning |
| P004 | OutOfScopeFiles | Error |
| P005 | SecretsInDiff | Error |
| P006 | SkippedTests | Error |
| P007 | SuppressedDiagnostics | Warning |
| P008 | DeletedTests (test files) | Error |
| P009 | SwallowedExceptions | Warning |
| P010 | NewDependencies | Warning |
| P011 | CiConfigEdited | Info |
| P013 | CommittedArtifacts | Error |
| P014 | RulesFileLint (R001–R016 on changed rules files) | Per rule |
| P015 | UnpinnedActions | Warning |
| P016 | SecurityExemptions | Warning |
| P018 | BrowserPolicyLoosened | Warning |
| P019 | ConfigTodos | Warning |

All config keys, read from the pull request's base branch:

```json
{
  "scope": ["src/**", "tests/**"],
  "conclusion": "fail-on-warnings",
  "disable": ["P002"],
  "comment": true
}
```

- `scope`: path globs from the repo root. `**` must be a whole segment; `{a,b}`, `[...]`, `!` and `\` are rejected. Turns on P004, and is read only by P004. Not set: P004 is off.
- `conclusion`: `fail-on-warnings` fails the check on any Warning or Error finding (P014: Error only) or config error. `fail-on-errors` fails it on any Error finding or config error. `advisory` (default) reports findings as neutral.
- `disable`: check IDs to skip, and rule IDs (R001–R016) to skip inside P014.
- `ignore`: path globs, or `{ "paths": [...], "checks": [...] }`, skipped by content checks (not P004, P008, P011, P013, P014).
- The config is read from the base branch; a pull request that changes it is reviewed with the base version.
- `comment`: `true` posts one PR comment with the summary and up to 25 linked findings, updated on each new commit. Default `false`.

Install: [github.com/apps/aibysitter](https://github.com/apps/aibysitter/installations/new). Setup and configuration: [docs/installing-on-your-repos.md](docs/installing-on-your-repos.md). Details: [aibysitting.net/GitHub](https://aibysitting.net/GitHub).

## API

`POST /api/lint` with JSON `{ "content": "...", "format": "Auto", "disable": ["R013"] }` returns findings, suppressed findings, score, grade, detected format and ruleset version. Same limits as the lint page; no key. Details: [aibysitting.net/API](https://aibysitting.net/API).

## CLI

`aibysitter`, a .NET tool using the same rules, ruleset version and scoring as the site. Runs offline. On nuget.org:

```
dotnet tool install --global Aibysitter.Cli
```

```
aibysitter lint <file|-> [--format <name>] [--disable R002,R005] [--json] [--fail-on-error] [--fail-below <A|B|C|D>] [--stdin-path <path>]
aibysitter init --packs starter,aspnet-web-api --format claude [--title <text>] [--output <path>] [--force]
aibysitter fix <file|-> [--dry-run] [--disable R011]
aibysitter hook claude-code [--disable R004]
aibysitter packs
```

- Format: `--format`, else from the file path (as in the list above), else detected from content. `-` reads standard input.
- `--json`: the `/api/lint` response fields, plus `file`.
- `--stdin-path <path>`: with `-`, names standard input for format detection and the report.
- `fix`: fixes R011 (deletes empty headings) and R012 (closes the fence at end of file), repeated until none apply; writes in place. `--dry-run` prints a unified diff and exits 1 when there are changes. With `-`, the fixed text goes to standard output.
- `hook claude-code`: Claude Code `PostToolUse` hook; exits 2 with Error and Warning findings on stderr when the edited file is a rules file. Hooks: [`hooks/`](hooks/) and [aibysitting.net/Hooks](https://aibysitting.net/Hooks).
- `init` writes a rules file composed from rules packs (below), then prints its score. `--format`: claude, agents, gemini, copilot, cursor, cursorrules, windsurf. Default output: where that format goes (`CLAUDE.md`, `.github/copilot-instructions.md`, `.cursor/rules/<pack>.mdc`, ...). An existing file needs `--force`.
- Exit codes: 0 ok, 1 a `--fail-*` threshold failed or `fix --dry-run` found changes, 2 usage error, 3 file not readable or not writable. Without a `--fail-*` flag the exit code is 0 whatever the findings.
- No length limit.

## Rules packs

Sections for a rules file, grouped by stack: [aibysitting.net/Packs](https://aibysitting.net/Packs), with each section's lint score. Machine-readable: `GET /packs/registry.json` (schema version 1). Content is CC0.

A pack is a folder under [`src/Aibysitter.Packs/Content/`](src/Aibysitter.Packs/Content/):

- `pack.json`: `schemaVersion` (1), `id` (kebab-case, the folder name), `title`, `description`, `tags`, `targets` (formats the pack is written for), `license`, optional `standalone` (`true`: the pack is used only on its own; `starter` is one).
- `intro.md` (optional): text under the H1, written only when the pack is used alone.
- `NN-name.md`: one section each, in file order. One H2 heading and its body; no H1.

Pack text never names a rules file directly; it writes `{{rules-file}}`, replaced with the output path (`CLAUDE.md`, `.github/copilot-instructions.md`, `.cursor/rules/<pack>.mdc`, ...). Loading rejects literal rules-file names and unknown `{{...}}` tokens.

Composition: `# title`, the intro when one pack is chosen, then sections in pack order. A heading in more than one pack becomes one section at its first position, with later packs' lines added and repeated list items dropped. Cursor `.mdc` output gets `description` and `alwaysApply: true` frontmatter.

## GitHub Action

`action.yml` lints rules files with the CLI and posts a check run named `Aibysitter rules` with an annotation per finding. On the [GitHub Marketplace](https://github.com/marketplace/actions/aibysitter-rules-lint).

```yaml
permissions:
  contents: read
  checks: write
steps:
  - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
  - uses: jbailey2900/aibysitter@v1
    with:
      fail-on-error: "true"
```

| Input | Default | |
|---|---|---|
| `files` | empty | Paths or globs, space- or newline-separated. Globs match files git tracks or would track; `**/` matches zero or more folders. Empty: the supported rules files tracked in the repository. |
| `fail-on-error` | `false` | Fail when any Error finding remains. |
| `fail-below` | empty | Fail when a file's grade is below A, B, C or D. |
| `cli-version` | the CLI version released with this tag | `Aibysitter.Cli` version installed from nuget.org. Empty: build the CLI from the action's source (about a minute). |
| `token` | `github.token` | Needs `checks: write`. Without it, annotations are written to the log (GitHub shows 10 per type per step) and the job summary lists all findings. |

Outputs: `grade` and `score` (lowest across files), `findings` (total), `report` (path to the merged JSON).

Check conclusion: `failure` when a threshold fails, `neutral` with findings, `success` with none or no files. Annotation levels: Error → failure, Warning → warning, Info → notice. Runs on Linux, Windows and macOS runners.

## Score history and badges

Lint by URL stores each public result: repository, file, score, grade, ruleset version and time. The result page lists the last 10 for that repository and file. Kept up to 400 days, at most 500 rows per repository and file. Private repositories cannot be read, so nothing is stored for them.

Badge: `https://aibysitting.net/badge/{owner}/{repo}.svg`, optional `?file=` (one of the supported file names). Default: the first file found, in the lint-by-URL order. Cached for an hour; a refresh fetches, lints and stores a row. No file found: grey `unknown`.

## Gallery

Example rules files, each linted and scored: [aibysitting.net/Gallery](https://aibysitting.net/Gallery).

- `GET /gallery/{id}/{file}`: the rules file
- `GET /gallery/{id}/badge.svg`: score badge
- `GET /registry.json`: all entries, schema version 1

Entries live in [`src/Aibysitter.Web/Gallery/Content/`](src/Aibysitter.Web/Gallery/Content/), one folder per entry holding only `entry.json`: metadata, `file` (and `path` for `.mdc`), `pack`, and optional `title`. The content is that rules pack composed in the entry's format, so the gallery and `aibysitter init --packs <pack> --format <format>` produce the same file.

## Repository layout

| Path | Contents |
|---|---|
| `src/Aibysitter.Rules` | Lint rules, scoring, pull request checks |
| `src/Aibysitter.Cli` | `aibysitter` dotnet tool |
| `src/Aibysitter.Packs` | Rules packs (content), loader, validation, composer |
| `action.yml`, `action/` | GitHub Action wrapping the CLI; fixtures in `tests/action-fixtures` |
| `src/Aibysitter.Rules.Browser` | Build-time exporter: rule patterns and constants for the browser lint engine |
| `src/Aibysitter.Web` | ASP.NET Core Razor Pages site, GitHub App webhook, gallery, notes (`Notes/Content`); browser lint engine in `wwwroot/js` |
| `tests/Aibysitter.Rules.Tests` | xUnit tests; fixtures in `tests/fixtures` |
| `tests/Aibysitter.Web.Smoke` | Playwright smoke tests against a running site (CI job `smoke`) |

Requires the .NET 10 SDK. Build and test: `dotnet test Aibysitter.slnx`. The browser parity tests need Node.js on PATH: without it they pass with a SKIPPED message locally and fail when `CI` is set. Smoke tests are skipped unless `AIBYSITTER_SMOKE_URL` is set; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Privacy

What the site and the App read, keep, and log: [aibysitting.net/Privacy](https://aibysitting.net/Privacy).

## Self-hosting

See [docs/self-hosting.md](docs/self-hosting.md).

## License

- Code: MIT. See [LICENSE](LICENSE).
- Gallery content (`src/Aibysitter.Web/Gallery/Content/`): CC0 1.0. See [its LICENSE](src/Aibysitter.Web/Gallery/Content/LICENSE).
- JetBrains Mono font (`src/Aibysitter.Web/wwwroot/fonts`): SIL Open Font License 1.1. See [OFL.txt](src/Aibysitter.Web/wwwroot/fonts/OFL.txt).

## Security

See [SECURITY.md](SECURITY.md).
