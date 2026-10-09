# Contributing

## Tests
- Requirements: .NET 10 SDK; Node.js on `PATH` for the browser parity tests.
- Run: `dotnet test Aibysitter.slnx`
- SQL Server tests run when `AIBYSITTER_TEST_SQL` holds a connection string, and are skipped otherwise. In CI they and the parity tests are required.
- Browser smoke tests (`tests/Aibysitter.Web.Smoke`) run when `AIBYSITTER_SMOKE_URL` names a running site, and are skipped otherwise. Start the site with `ASPNETCORE_ENVIRONMENT=Smoke` and `RateLimiting__Lint__PermitLimit=1000`. The browser is the installed Google Chrome, or the Chromium executable in `AIBYSITTER_SMOKE_BROWSER`. Requests outside localhost fail the test. CI job: `smoke`.

## Pull requests
- Every pull request gets CI and the Aibysitter GitHub App's check, `Aibysitter review`.
- `.github/aibysitter.json` ignores the check docs, the changelog and the P018 source for content checks, `tests/**` for P005, and the P001 source for P001: they quote the patterns the checks look for.
- New packages need agreement in an issue first.

## Rule changes
- A change to an R rule or P check needs a measurement note in the pull request: corpus or repos, sample size, findings, false positives per finding from two judges, before and after.
- An R rule change bumps the ruleset version and adds a changelog entry; a P check change adds an App checks changelog entry.

## Releases
- CLI: bump `<Version>` in `src/Aibysitter.Cli/Aibysitter.Cli.csproj` and the `cli-version` default in `action.yml` in the same pull request. After merge, tag `cli-v<Version>` on `main`; `release-cli.yml` publishes to nuget.org.
- Action: after the CLI version is on nuget.org, tag `v<major>.<minor>.<patch>` on `main`, move `v<major>`, and publish the release to the Marketplace.

## False positives
Report with the False positive issue template: rule ID, the line, why it is wrong.
