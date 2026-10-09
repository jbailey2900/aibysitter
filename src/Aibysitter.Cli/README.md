# aibysitter

Lints rules files for AI coding agents: CLAUDE.md, AGENTS.md, GEMINI.md, Cursor rules (`.cursor/rules/*.mdc`, `.cursorrules`), `.github/copilot-instructions.md`, `.windsurfrules`. Same rules, ruleset version and scoring as [aibysitting.net](https://aibysitting.net). Runs offline.

```
aibysitter lint <file|-> [--format <name>] [--disable R002,R005] [--json] [--fail-on-error] [--fail-below <A|B|C|D>] [--stdin-path <path>]
aibysitter init --packs starter,aspnet-web-api --format claude [--title <text>] [--output <path>] [--force]
aibysitter fix <file|-> [--dry-run] [--disable R011]
aibysitter hook claude-code [--disable R004]
aibysitter packs
```

`init` writes a rules file composed from rules packs and prints its score. Packs: [aibysitting.net/Packs](https://aibysitting.net/Packs).

Exit codes: 0 ok, 1 threshold failed, 2 usage error, 3 file not readable or not writable, 4 lint timed out (a pattern ran longer than 1 second).

Rules: [aibysitting.net/Rules](https://aibysitting.net/Rules).
