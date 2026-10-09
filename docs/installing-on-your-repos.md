# Installing the GitHub App

Install the App, add a three-line config, and make **Aibysitter review** a required check. A pull request that adds an unpinned action, a new dependency, a leaked key or a security exemption then cannot merge until a human looks.

```json
{
  "conclusion": "fail-on-warnings"
}
```

Save it as `.github/aibysitter.json` on the default branch. Then require the **Aibysitter review** status check on that branch: **Settings → Rules → Rulesets**, or **Settings → Branches**.

- `fail-on-errors` is the lighter setting: only Error findings block, such as a leaked key, a placeholder, a skipped or deleted test, a file outside `scope` or a committed `.env`.
- `advisory` is the default and the trial mode: every finding is an annotation and nothing blocks.
- Bot dependency bumps (Dependabot, Renovate) block too, by design: a bot bumping a dependency is a change a human should sign off on.

Install: https://github.com/apps/aibysitter/installations/new

## Install

1. Open https://github.com/apps/aibysitter/installations/new.
2. Pick the account or organization. Installing needs owner or admin rights on it.
3. Choose **Only select repositories** and pick the repositories.
4. Confirm. The App requests: Checks (read and write), Pull requests (read and write), Contents (read), Metadata (read). Pull requests write is used only to post the summary comment when `comment` is `true`.

Change the repository list later under **Configure** for Aibysitter: personal account, **Settings → Applications → Installed GitHub Apps**; organization, **Settings → GitHub Apps**.

## First pull request

Under `advisory` (the default), every review completes as `success` (no findings) or `neutral` (findings). Under `fail-on-warnings` or `fail-on-errors`, a blocking finding completes it as `failure`.

A review that stops on an error completes as **Review failed**: `failure` under `fail-on-warnings` and `fail-on-errors`, `neutral` under `advisory`. A review the server drops (queue full, interrupted 3 times) completes as `neutral`.

- The check is named **Aibysitter review**, in the pull request's **Checks** tab. GitHub lists it as **Aibysitter / Aibysitter review**.
- The summary has one row per check with its finding count, then notes and config errors. Files not read are listed in the notes.
- Each finding is an annotation on the changed line, in **Files changed**. Findings on removed files are listed in the summary.

Checks: [aibysitting.net/Rules](https://aibysitting.net/Rules). What the App reads and keeps: [aibysitting.net/Privacy](https://aibysitting.net/Privacy).

## What we store

- Code is fetched from GitHub for the duration of one review and discarded. Diffs, file contents and finding text are never written to disk, database or logs.
- Results go back to GitHub only: the check run, its annotations and summary, and the PR comment when `comment` is on.
- Stored per review: a daily count by conclusion. A queue file holding the repo, PR number and commit SHAs exists only while the review runs and is deleted when it finishes.
- Logs keep the repo owner and name, PR number, short SHA, delivery and check-run IDs, finding count and any error text for 14 days.

## Config file

Optional. Path: `.github/aibysitter.json`. The App reads it from the base branch commit of the pull request, so a pull request cannot change how it is itself reviewed.

```json
{
  "scope": ["src/**", "tests/**"],
  "conclusion": "fail-on-warnings",
  "disable": ["P011", "R013"]
}
```

| Key | Value | Default |
|---|---|---|
| `scope` | Path globs from the repository root. `**` spans folders and must be a whole segment (`**/*.cs`, not `**.cs`); `*` and `?` stay within one. Case-sensitive. Not supported: `{a,b}`, `[...]`, `!`, `\`. Turns on P004. Read only by P004; other checks review every changed file. | Not set; P004 off |
| `conclusion` | `advisory`: findings report as `neutral` (trial mode). `fail-on-warnings`: any Warning or Error finding fails the check; P014 fails only when a rule fires at Error. `fail-on-errors`: any Error finding fails the check. Info findings never fail the check. Under either fail mode, "Review failed" is `failure`. | `advisory` |
| `disable` | Check IDs (P001–P019 except the withdrawn P003, P012, P017) skip that check. Rule IDs (`R001`–`R016`) skip that rule inside P014. | None |
| `ignore` | Entries are a path glob (skipped by every content check) or `{ "paths": [globs], "checks": [IDs] }` (skipped by those checks only). Globs as in `scope`. P004, P008, P011, P013 and P014 read paths or rules files and do not apply `ignore`. Up to 50 entries. The summary lists the entries and how many changed files they match. The only key that removes files from checks. | None |
| `comment` | `true`: one comment on the pull request with the summary table and up to 25 findings linked to their lines, updated on each new commit. No comment is created while there are no findings; turning it off leaves an existing comment as it is. | `false` |

- Comments and trailing commas are allowed.
- An invalid entry falls back to its default, is listed under **Config errors** in the summary and is annotated on its line of the config file.
- Under `fail-on-warnings` and `fail-on-errors`, any config error fails the check. Under `advisory`, config errors do not change the conclusion.
- Config changes take effect only after they merge to the base branch. Reviews after that, including new commits on pull requests already open against that branch, use the merged version.
- A pull request that edits `.github/aibysitter.json` is reviewed with the base version, and the summary says so. Config errors are reported for the edited version.
- Editing the config file makes it a changed file: outside `scope`, it gets a P004 finding.

Starter config:

```json
{
  "conclusion": "fail-on-warnings",
  "disable": ["P011"]
}
```

## Rules files

P014 lints rules files a pull request adds or changes. R006 checks the paths, scripts, and targets they name. Recognised locations:

| File | Location |
|---|---|
| `CLAUDE.md`, `AGENTS.md`, `GEMINI.md` | Any directory |
| Cursor rules | `.cursor/rules/*.mdc` |
| `.cursorrules`, `.windsurfrules` | Repository root |
| Copilot instructions | `.github/copilot-instructions.md` |

Suppress a rule inside a rules file with a comment on its own line:

```markdown
<!-- aibysitter-disable R013 -->
<!-- aibysitter-disable-next-line R006 -->
```

Precedence: comments in the file, then rule IDs in `disable`, then `"disable": ["P014"]`.

## Failing the build

1. Set `"conclusion": "fail-on-warnings"` (or `"fail-on-errors"` to fail on Error findings only).
2. Under **Settings → Branches** (or **Rules → Rulesets**), require the **Aibysitter review** status check on the default branch.
3. Disable noisy checks first: P011 (CI config edited), R013 (prose paragraphs). P011 is Info; it never fails the check, but it adds annotations.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| No **Aibysitter review** check appears | Repository not selected, or the App lacks Checks permission | Add the repository under **Configure**; accept any pending permission request |
| Check stays **Queued** | The review is waiting behind others, or the server is restarting | Wait a few minutes. Still queued: push a commit. App owner: redeliver the webhook (App settings → Advanced → Recent deliveries) |
| Summary says "Not read (over 100 KB)" or "Not read (not UTF-8)" | The App reads files up to 100 KB of UTF-8 text | None; checks that read file contents skip those files, diff checks still run |
| Summary says "Not read (2 MB review limit reached)" | The App reads at most 2 MB of file content per review | Split the pull request |
| Check completes as **Review failed** | A GitHub API error, or a check hit the 1-second pattern limit | Push a new commit. App owner: redeliver the webhook |
| Summary says "R006 skipped" | The repository file list is too large for one GitHub request | None; R006 does not run on that repository |
| Unexpected P004 findings | `scope` does not cover the path | Add the glob to `scope`, or remove `scope` |
| Summary says "PR comment not posted: the App needs Pull requests: Read and write" | The installation has not accepted the updated permissions | Accept the pending permission request under **Configure** for Aibysitter |
