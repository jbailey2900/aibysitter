#!/usr/bin/env bash
# Lints rules files with the aibysitter CLI and merges the per-file JSON into one report.
# Inputs (env): INPUT_FILES, INPUT_FAIL_ON_ERROR, INPUT_FAIL_BELOW, AIBYSITTER_CLI (command), AIBYSITTER_OUT (base folder).
# Writes: report.json in a new folder under $AIBYSITTER_OUT, step outputs (grade, score, findings, report, status), job summary.
# status: ok | threshold | error. This script exits 0; the action's last step fails on threshold or error.
set -uo pipefail

# jq on Windows writes CRLF.
jq() { command jq "$@" | tr -d '\r'; }

# A fresh folder per run: the action can run several times in one job, and each step's report output must stay valid.
mkdir -p "${AIBYSITTER_OUT:?}"
out=$(mktemp -d "$AIBYSITTER_OUT/run.XXXXXX")
read -r -a cli <<< "${AIBYSITTER_CLI:?}"
mkdir -p "$out/files"
: "${GITHUB_OUTPUT:=/dev/null}"
: "${GITHUB_STEP_SUMMARY:=/dev/null}"

# Files git tracks or would track (repository-relative paths).
candidates() {
  git ls-files -z --cached --others --exclude-standard | tr '\0' '\n'
}

# Supported rules files, as RulesFormats.FromFileName recognises them.
discover() {
  candidates | grep -E \
    -e '(^|/)(CLAUDE|AGENTS|GEMINI)\.md$' \
    -e '^\.cursorrules$' \
    -e '^\.windsurfrules$' \
    -e '^\.github/copilot-instructions\.md$' \
    -e '(^|/)\.cursor/rules/([^/]+/)*[A-Za-z0-9][A-Za-z0-9_.-]*\.mdc$' || true
}

# Glob to anchored ERE: "**/" matches zero or more folders, "*" and "?" stay within one folder, [...] and [!...] are classes.
glob_regex() {
  GLOB="$1" awk 'BEGIN {
    g = ENVIRON["GLOB"]; n = length(g); r = ""
    for (i = 1; i <= n; i++) {
      c = substr(g, i, 1)
      if (c == "*") {
        if (substr(g, i + 1, 2) == "*/") { r = r "(.*/)?"; i += 2 }
        else if (substr(g, i + 1, 1) == "*") { r = r ".*"; i += 1 }
        else r = r "[^/]*"
      } else if (c == "?") {
        r = r "[^/]"
      } else if (c == "[" && (j = index(substr(g, i + 1), "]")) > 1) {
        class = substr(g, i + 1, j - 1)
        if (substr(class, 1, 1) == "!") class = "^" substr(class, 2)
        r = r "[" class "]"; i += j
      } else if (index(".^$+(){}|\\[]", c)) {
        r = r "\\" c
      } else {
        r = r c
      }
    }
    print "^" r "$"
  }'
}

# Explicit paths and globs, whitespace-separated. Globs match candidates(); a path without wildcards is passed through so
# the CLI reports it when missing.
expand() {
  local token
  set -f
  for token in $INPUT_FILES; do
    token=$(normalize "$token")
    if [[ "$token" == *[\*\?\[]* ]]; then
      candidates | grep -E -e "$(glob_regex "$token")" || true
    else
      printf '%s\n' "$token"
    fi
  done
  set +f
}

normalize() {
  local path="$1"
  path="${path#"${GITHUB_WORKSPACE:-$PWD}"/}"
  path="${path#./}"
  printf '%s' "$path"
}

files=()
if [ -n "${INPUT_FILES//[[:space:]]/}" ]; then
  while IFS= read -r f; do files+=("$f"); done < <(expand | awk 'NF && !seen[$0]++')
else
  while IFS= read -r f; do files+=("$f"); done < <(discover | sort -u)
fi

args=()
[ "${INPUT_FAIL_ON_ERROR:-false}" = "true" ] && args+=(--fail-on-error)
[ -n "${INPUT_FAIL_BELOW:-}" ] && args+=(--fail-below "$INPUT_FAIL_BELOW")

status=ok
errors=()
reports=()
i=0
# ${a[@]+"${a[@]}"}: bash 3.2 treats an empty array as unset under set -u.
for file in ${files[@]+"${files[@]}"}; do
  i=$((i + 1))
  json="$out/files/$i.json"
  "${cli[@]}" lint "$file" --json ${args[@]+"${args[@]}"} > "$json" 2> "$out/files/$i.err"
  code=$?
  case $code in
    0) reports+=("$json") ;;
    1) reports+=("$json"); [ "$status" = ok ] && status=threshold ;;
    *) status=error; errors+=("$file: $(tr '\n' ' ' < "$out/files/$i.err")"); rm -f "$json" ;;
  esac
done

# No glob over $out: on Windows it holds backslashes, which a glob reads as escapes.
if [ ${#reports[@]} -gt 0 ]; then
  jq -s --arg status "$status" '{status: $status, files: .}' ${reports[@]+"${reports[@]}"} > "$out/report.json"
else
  jq -n --arg status "$status" '{status: $status, files: []}' > "$out/report.json"
fi

grade=$(jq -r '[.files[].grade] | max // ""' "$out/report.json")
score=$(jq -r '[.files[].score] | min // ""' "$out/report.json")
findings=$(jq -r '[.files[].findings | length] | add // 0' "$out/report.json")
{
  echo "grade=$grade"
  echo "score=$score"
  echo "findings=$findings"
  echo "report=$out/report.json"
  echo "status=$status"
} >> "$GITHUB_OUTPUT"

{
  echo "## Aibysitter rules lint"
  echo
  if [ ${#files[@]} -eq 0 ]; then
    echo "No rules files found."
  elif [ "$(jq '.files | length' "$out/report.json")" -gt 0 ]; then
    echo "| File | Score | Grade | Findings |"
    echo "|---|---|---|---|"
    jq -r '.files[] | "| `\(.file)` | \(.score) | \(.grade) | \(.findings | length) |"' "$out/report.json"
    if [ "$findings" -gt 0 ]; then
      echo
      echo "| Location | Severity | Rule | Message | Fix |"
      echo "|---|---|---|---|---|"
      jq -r '.files[] | .file as $f | .findings[] | "| `\($f):\(.line)` | \(.severity) | \(.rule) | \(.message | gsub("\\|"; "\\|")) | \(.fixHint | gsub("\\|"; "\\|")) |"' "$out/report.json"
    fi
  fi
  for e in ${errors[@]+"${errors[@]}"}; do
    echo
    echo "Error: $e"
  done
} >> "$GITHUB_STEP_SUMMARY"

for e in ${errors[@]+"${errors[@]}"}; do
  e=${e//\%/%25}
  e=${e//$'\r'/%0D}
  e=${e//$'\n'/%0A}
  echo "::error title=aibysitter::$e"
done
exit 0
