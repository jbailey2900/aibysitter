// Browser port of Aibysitter.Rules (LintEngine, RulesFile, Scorer, R001-R005, R007-R016).
// Patterns, rule metadata, scoring, and limits come from generated/rules-patterns.mjs, built from the C# definitions.
// RulesParityTests asserts this file and the C# engine produce identical results.
import gen from "./generated/rules-patterns.mjs";

const { rulesetVersion, patterns, rules, scoring, formats, limits } = gen;
const ruleById = new Map(rules.map((r) => [r.id, r]));
const cache = new Map();

/** Compiled generated pattern. extra adds flags: "g" for all matches, "d" for group indices. */
function rx(key, extra = "") {
  const id = key + "/" + extra;
  let re = cache.get(id);
  if (!re) {
    const p = patterns[key];
    if (!p) throw new Error("Unknown pattern " + key);
    re = new RegExp(p.source, p.flags + extra);
    cache.set(id, re);
  }
  re.lastIndex = 0;
  return re;
}

const test = (key, s) => rx(key).test(s);
const match = (key, s) => rx(key).exec(s);
const matchAll = (key, s, extra = "") => [...s.matchAll(rx(key, "g" + extra))];
const count = (key, s) => matchAll(key, s).length;
const replace = (key, s, by) => s.replace(rx(key, "g"), by);

// .NET char.IsWhiteSpace, string.Trim, IsNullOrWhiteSpace, ToLowerInvariant.
const WS = /[\t\n\v\f\r\x85\p{Z}]/u;
const LEAD_WS = /^[\t\n\v\f\r\x85\p{Z}]+/u;
const trimStart = (s) => s.replace(LEAD_WS, "");

// A loop, not /[…]+$/: a regex retries from every whitespace run and is quadratic on long non-trailing runs.
function trimEnd(s) {
  let end = s.length;
  while (end > 0 && WS.test(s[end - 1])) end--;
  return end === s.length ? s : s.slice(0, end);
}
const trim = (s) => trimEnd(trimStart(s));
const isBlank = (s) => trim(s).length === 0;

function lowerInvariant(s) {
  let out = "";
  for (const ch of s) {
    const lower = ch.toLowerCase();
    out += [...lower].length === 1 ? lower : ch;
  }
  return out;
}

function trimChars(s, chars, start = true, end = true) {
  let a = 0;
  let b = s.length;
  while (start && a < b && chars.includes(s[a])) a++;
  while (end && b > a && chars.includes(s[b - 1])) b--;
  return s.slice(a, b);
}

const distinct = (items) => [...new Set(items)];
const quoted = (items) => distinct(items).map((t) => `"${t}"`).join(", ");

function finding(ruleId, line, message, fixHint) {
  return { ruleId, line, message, fixHint };
}

// ---- Frontmatter ----

function splitValue(value) {
  let v = trim(value);
  if (v.startsWith("[") && v.endsWith("]")) v = v.slice(1, -1);
  return v.split(",").map((s) => trimChars(trim(s), "\"'")).filter((s) => s.length > 0);
}

function readFrontmatter(raw, lineCount) {
  if (lineCount < 2 || trimEnd(raw[0]) !== "---") return null;
  const entries = new Map();
  let last = null;
  for (let i = 1; i < Math.min(lineCount, limits.frontmatterMaxLines); i++) {
    const text = raw[i];
    const closer = trimEnd(text);
    if (closer === "---" || closer === "...") return frontmatter(i + 1, entries);
    const pair = match("Frontmatter.PairRegex", text);
    if (pair) {
      last = pair.groups.key;
      entries.set(last, { line: i + 1, values: splitValue(pair.groups.value ?? "") });
      continue;
    }
    const item = match("Frontmatter.ItemRegex", text);
    if (item && last !== null) entries.get(last).values.push(...splitValue(item.groups.value));
  }
  return null;
}

function frontmatter(endLine, entries) {
  return {
    endLine,
    keys: [...entries.keys()],
    has: (key) => entries.has(key),
    lineOf: (key) => entries.get(key)?.line ?? 1,
    values: (key) => entries.get(key)?.values ?? [],
  };
}

// ---- RulesFile ----

function makeLine(number, text, isHeading, isInCodeFence, isFrontmatter = false) {
  const blank = isBlank(text);
  const directive = !isInCodeFence && !isFrontmatter && test("Suppressions.DirectiveRegex", text);
  const prose = !blank && !isHeading && !isInCodeFence && !isFrontmatter && !directive;
  return { number, text, isHeading, isInCodeFence, isFrontmatter, isBlank: blank, isDirective: directive, isProse: prose };
}

function closesFence(fence, text, opening) {
  const marker = fence[1];
  return marker[0] === opening[0] && marker.length >= opening.length && isBlank(text.slice(fence.index + fence[0].length));
}

function splitLines(text) {
  const raw = text.replaceAll("\r\n", "\n").replaceAll("\r", "\n").split("\n");
  const lineCount = raw.length > 0 && raw[raw.length - 1].length === 0 ? raw.length - 1 : raw.length;
  return { raw, lineCount };
}

function parseBody(raw, lineCount, first, lines) {
  let inFence = false;
  let fenceOpenLine = 0;
  let fenceMarker = null;
  for (let i = first; i < lineCount; i++) {
    const number = i + 1;
    const text = raw[i];
    const fence = match("RulesFile.FenceRegex", text);
    if (fence && (!inFence || closesFence(fence, text, fenceMarker))) {
      if (!inFence) {
        fenceMarker = fence[1];
        fenceOpenLine = number;
      }
      inFence = !inFence;
      lines.push(makeLine(number, text, false, true));
      continue;
    }
    const headingMatch = inFence ? null : match("RulesFile.HeadingRegex", text);
    const heading = headingMatch !== null && headingMatch[2] !== undefined;
    lines.push(makeLine(number, text, heading, inFence));
  }
  return inFence ? fenceOpenLine : null;
}

function resolveFormat(format, fm) {
  if (format !== "Auto") return format;
  return fm && fm.keys.some((k) => formats.cursorKeys.includes(k)) ? "CursorMdc" : "Markdown";
}

export function parse(text, format = "Auto") {
  const { raw, lineCount } = splitLines(text);
  const fm = readFrontmatter(raw, lineCount);
  const lines = [];
  const first = fm ? fm.endLine : 0;
  for (let i = 0; i < first; i++) lines.push(makeLine(i + 1, raw[i], false, false, true));
  const unclosedFenceLine = parseBody(raw, lineCount, first, lines);
  return { lines, unclosedFenceLine, frontmatter: fm, format: resolveFormat(format, fm), suppressions: suppressionsFrom(lines) };
}

// ---- Suppressions ----

function suppressionsFrom(lines) {
  const fileWide = new Set();
  const byLine = new Map();
  for (const line of lines.filter((l) => l.isDirective)) {
    const m = match("Suppressions.DirectiveRegex", line.text);
    const ids = matchAll("Suppressions.IdRegex", m.groups.ids).map((x) => x[0].toUpperCase());
    if (m.groups.next === undefined) {
      ids.forEach((id) => fileWide.add(id));
      continue;
    }
    if (!byLine.has(line.number + 1)) byLine.set(line.number + 1, new Set());
    ids.forEach((id) => byLine.get(line.number + 1).add(id));
  }
  const id = (f) => f.ruleId.toUpperCase();
  return { isSuppressed: (f) => fileWide.has(id(f)) || (byLine.get(f.line)?.has(id(f)) ?? false) };
}

// ---- Text helpers (TextNormalizer, InstructionText) ----

function normalize(text) {
  const s = replace("TextNormalizer.ListMarkerRegex", trim(text), "");
  return lowerInvariant(trim(replace("TextNormalizer.WhitespaceRegex", s, " ")));
}

const isTableRow = (text) => trimStart(text).startsWith("|");
const withoutCode = (text) => replace("InstructionText.CodeSpanRegex", text, " CODE ");

function content(text) {
  let s = withoutCode(text);
  s = replace("InstructionText.LinkRegex", s, "$1");
  s = replace("InstructionText.UrlRegex", s, " CODE ");
  s = replace("InstructionText.PathRegex", s, " CODE ");
  s = replace("InstructionText.FileNameRegex", s, " CODE ");
  s = replace("InstructionText.ListMarkerRegex", trim(s), "");
  s = s.replaceAll("**", "").replaceAll("__", "");
  s = replace("InstructionText.LabelRegex", s, "");
  return trim(s);
}

const clauses = (text) => content(text).split(rx("InstructionText.ClauseSplitRegex")).map(trim).filter((c) => c.length > 0);

function hasConcreteTarget(after) {
  return after.includes("CODE")
    || after.includes("(")
    || after.toLowerCase().includes("e.g.")
    || [...after].filter((c) => c === ",").length >= 2
    || trimEnd(after).endsWith(":");
}

function isInstruction(sentence) {
  if (test("InstructionText.CatalogEntryRegex", sentence)) return false;
  const c = content(sentence);
  const keepLabel = trim(replace("InstructionText.ListMarkerRegex", trim(withoutCode(sentence)), "").replaceAll("**", "").replaceAll("__", ""));
  if (test("InstructionText.ModalRegex", c)) return true;
  if (clauses(sentence).some((cl) => test("InstructionText.ImperativeStartRegex", cl))) return true;
  if (test("InstructionText.DirectiveLabelRegex", keepLabel)) return true;
  const words = count("InstructionText.WordRegex", c);
  return test("InstructionText.ListItemRegex", sentence) && !test("InstructionText.LabelRegex", keepLabel)
    && words > 0 && words <= limits.terseRuleMaxWords
    && !test("InstructionText.FiniteVerbRegex", c) && !test("InstructionText.DeterminerStartRegex", c);
}
const isListItem = (text) => test("InstructionText.ListItemRegex", text);
const isHtmlComment = (text) => test("InstructionText.HtmlCommentRegex", text);
const sentences = (text) => text.split(rx("InstructionText.SentenceSplitRegex"));

const isDocumentationDump = (file) =>
  file.lines.filter((l) => !l.isInCodeFence && test("InstructionText.MdxComponentRegex", l.text)).length >= limits.documentationDumpMinComponents;

/** InstructionText.Units: list items with continuation lines, or runs of paragraph lines. */
function units(file) {
  const out = [];
  if (isDocumentationDump(file)) return out;
  let current = null;
  for (const line of file.lines) {
    if (!line.isProse || isTableRow(line.text)) {
      current = null;
      continue;
    }
    if (!current || isListItem(line.text)) {
      current = [];
      out.push(current);
    }
    current.push(line);
  }
  return out;
}

/** InstructionText.Join: trimmed lines joined with one space; start offset of each line's untrimmed text. */
function joinUnit(unit) {
  let text = "";
  const starts = new Map();
  for (const line of unit) {
    if (text.length > 0) text += " ";
    starts.set(line, text.length - (line.text.length - trimStart(line.text).length));
    text += trim(line.text);
  }
  return { text, starts };
}

function sentenceSpans(text) {
  const spans = [];
  let start = 0;
  for (const b of matchAll("InstructionText.SentenceSplitRegex", text)) {
    spans.push({ start, length: b.index - start });
    start = b.index + b[0].length;
  }
  spans.push({ start, length: text.length - start });
  return spans;
}

/** InstructionText.InstructionLines: numbers of lines an instruction sentence overlaps. */
function instructionLines(file) {
  const result = new Set();
  for (const unit of units(file)) {
    const { text, starts } = joinUnit(unit);
    const instructions = sentenceSpans(text).filter((sp) => isInstruction(text.substr(sp.start, sp.length)));
    for (const line of unit) {
      const start = starts.get(line) + (line.text.length - trimStart(line.text).length);
      const end = start + trim(line.text).length;
      if (instructions.some((sp) => sp.start < end && sp.start + sp.length > start)) result.add(line.number);
    }
  }
  return result;
}

const after = (s, m) => s.slice(m.index + m[0].length);
const proseLines = (file) => file.lines.filter((l) => l.isProse && !isTableRow(l.text));

// ---- Rules ----

function r001(file) {
  const out = [];
  for (const unit of units(file)) {
    const { text, starts } = joinUnit(unit);
    const spans = sentenceSpans(text);
    const instruction = spans.map((sp) => isInstruction(text.substr(sp.start, sp.length)));
    const inScope = isListItem(unit[0].text)
      ? spans.map(() => instruction.includes(true))
      : instruction.map((v, i) => v || (i > 0 && instruction[i - 1]));
    for (const line of unit.filter((l) => !isHtmlComment(l.text))) {
      const masked = line.text.replace(rx("RationaleProse.QuotedRegex", "g"), (m) => " ".repeat(m.length));
      const terms = matchAll("RationaleProse.PhraseRegex", masked)
        .filter((m) => inScope[sentenceIndex(spans, starts.get(line) + m.index)])
        .map((m) => lowerInvariant(m[0]));
      if (terms.length > 0) {
        out.push(finding("R001", line.number, `Rationale prose: ${quoted(terms)}`, "Remove the explanation. State the instruction only."));
      }
    }
  }
  return out;
}

function sentenceIndex(spans, offset) {
  let i = 0;
  while (i + 1 < spans.length && spans[i + 1].start <= offset) i++;
  return i;
}

function r002(file) {
  const lines = instructionLines(file);
  return file.lines.filter((l) => lines.has(l.number)).flatMap((line) => {
    const terms = [];
    let codeSeen = false;
    for (const clause of clauses(line.text)) {
      const verb = match("VagueVerbs.LeadVerbRegex", clause);
      const rest = verb ? after(clause, verb) : "";
      const term = verb ? lowerInvariant(verb.groups.term) : "";
      const checkableEnsure = !!verb && term === "ensure" && (codeSeen || test("VagueVerbs.CheckableObjectRegex", rest));
      if (verb && trim(rest).length > 0 && !hasConcreteTarget(rest) && !test("VagueVerbs.ResourceObjectRegex", rest)
        && !test("VagueVerbs.MethodOrPurposeRegex", rest) && !checkableEnsure) {
        terms.push(term);
      }
      if (test("VagueVerbs.ImperativeRegex", clause) && !(term === "ensure" && codeSeen) && !test("VagueVerbs.VerificationRegex", clause)) {
        for (const q of matchAll("VagueVerbs.QualifierRegex", clause)) {
          const rest2 = after(clause, q);
          const codeObject = test("VagueVerbs.AsNeededRegex", q[0]) && clause.slice(0, q.index).includes("CODE");
          if (!hasConcreteTarget(rest2) && !test("VagueVerbs.QualifierContextRegex", rest2) && !codeObject) terms.push(lowerInvariant(q[0]));
        }
      }
      codeSeen = codeSeen || clause.includes("CODE");
    }
    return terms.length === 0 ? [] : [finding("R002", line.number, `Vague wording: ${quoted(terms)}`,
      "Name the concrete action, file, or command.")];
  });
}

function r003(file) {
  const seen = new Map();
  const out = [];
  for (const line of file.lines.filter((l) => l.isProse)) {
    const m = match("ContradictoryModals.ModalRegex", normalize(line.text));
    if (!m) continue;
    const positive = m.groups.pos !== undefined;
    const remainder = trimChars(m.groups.rest, ".!;: ", false, true);
    if (remainder.length === 0) continue;
    const earlier = seen.get(`${!positive}\u0000${remainder}`);
    if (earlier !== undefined) {
      out.push(finding("R003", line.number, `Contradicts line ${earlier}: "${remainder}" is both required and forbidden.`,
        "Keep one instruction and delete the other."));
    }
    const key = `${positive}\u0000${remainder}`;
    if (!seen.has(key)) seen.set(key, line.number);
  }
  return out;
}

function r004(file) {
  const max = limits.fileMaxLines;
  return file.lines.length > max
    ? [finding("R004", max + 1, `File has ${file.lines.length} lines; limit is ${max}.`, "Cut or split the file.")]
    : [];
}

const indentOf = (text) => {
  const t = text.replaceAll("\t", "    ");
  return t.length - trimStart(t).length;
};

/** DuplicateLines.SectionPosition: innermost section opener above line i, as "kind/position", or null. */
function sectionPosition(lines, i) {
  let minIndent = indentOf(lines[i].text);
  let position = 1;
  let pendingBlank = false;
  let gap = false;
  for (let j = i - 1; j >= 0; j--) {
    const line = lines[j];
    if (line.isBlank) {
      pendingBlank = true;
      continue;
    }
    if (line.isHeading) return `h${match("DuplicateLines.HeadingLevelRegex", line.text)[1].length}/${position}`;
    const indent = indentOf(line.text);
    if (test("DuplicateLines.BoldLabelRegex", line.text)) return gap ? null : `b/${position}`;
    if (isListItem(line.text) && indent < minIndent) return `l${indent}/${position}`;
    minIndent = Math.min(minIndent, indent);
    gap = gap || pendingBlank;
    pendingBlank = false;
    position++;
  }
  return null;
}

function r005(file) {
  const { lines } = file;
  const keys = lines.map((l) => (l.isBlank ? null : normalize(l.text)));
  const shapes = keys.map((k) => (k === null ? null : replace("DuplicateLines.DigitsRegex", replace("DuplicateLines.CodeSpanRegex", k, "`"), "0")));
  const instr = instructionLines(file);
  const isContinuation = (i) => i > 0 && lines[i - 1].isProse && !isListItem(lines[i].text)
    && test("DuplicateLines.LowercaseStartRegex", lines[i].text) && !test("DuplicateLines.SentenceEndRegex", lines[i - 1].text);
  const isIndentedCode = (i) => i > 0 && lines[i - 1].isBlank && test("DuplicateLines.IndentedRegex", lines[i].text) && !isListItem(lines[i].text);
  const candidates = [];
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (instr.has(line.number) && !isHtmlComment(line.text) && !test("DuplicateLines.HorizontalRuleRegex", line.text)
      && count("DuplicateLines.WordRegex", normalize(line.text)) >= limits.duplicateMinWords
      && !isContinuation(i) && !isIndentedCode(i)) candidates.push(i);
  }
  const counts = new Map();
  for (const i of candidates) counts.set(keys[i], (counts.get(keys[i]) ?? 0) + 1);
  const sameNeighbour = (first, repeat, offset) => {
    const a = first + offset;
    const b = repeat + offset;
    return a >= 0 && b >= 0 && a < shapes.length && b < shapes.length && b !== first && shapes[a] !== null && shapes[a] === shapes[b];
  };
  const firstSeen = new Map();
  const out = [];
  for (const i of candidates) {
    const key = keys[i];
    if (!firstSeen.has(key)) {
      firstSeen.set(key, i);
      continue;
    }
    const first = firstSeen.get(key);
    const section = sectionPosition(lines, first);
    if (counts.get(key) >= limits.duplicateTemplateCount || sameNeighbour(first, i, -1) || sameNeighbour(first, i, 1)
      || (section !== null && section === sectionPosition(lines, i))) continue;
    out.push(finding("R005", lines[i].number, `Duplicate of line ${lines[first].number}.`, "Delete the repeated line."));
  }
  return out;
}

function hedgesIn(clause) {
  const found = [];
  if (!test("HedgedInstructions.DescriptiveStartRegex", clause)) {
    found.push(...matchAll("HedgedInstructions.HedgeRegex", clause)
      .map((m) => replace("HedgedInstructions.WhitespaceRegex", lowerInvariant(m[0]), " ")));
  }
  const consider = match("HedgedInstructions.ConsiderRegex", clause);
  if (consider) {
    const rest = after(clause, consider);
    if (!hasConcreteTarget(rest) && !test("HedgedInstructions.ConsiderObjectRegex", rest) && !test("HedgedInstructions.LabelUseRegex", rest)) found.push("consider");
  }
  return found;
}

function r007(file) {
  const lines = instructionLines(file);
  return file.lines.filter((l) => lines.has(l.number)).flatMap((line) => {
    const hedges = clauses(replace("HedgedInstructions.QuotedRegex", line.text, " QUOTE ")).flatMap(hedgesIn);
    return hedges.length === 0 ? [] : [finding("R007", line.number, `Hedge: ${quoted(hedges)}`,
      "State the instruction without the hedge, or state the condition that makes it apply.")];
  });
}

function r008(file) {
  const total = file.lines.length;
  const counted = file.lines.filter((l) => l.isProse || l.isHeading);
  const rfc = counted.some((l) => test("EmphasisInflation.RfcKeywordsRegex", withoutCode(l.text)));
  const key = rfc ? "EmphasisInflation.EmphasisWithoutKeywordsRegex" : "EmphasisInflation.EmphasisRegex";
  const emphasis = counted.filter((l) => test(key, withoutCode(l.text)));
  const allowed = Math.max(limits.emphasisMinAllowed, Math.ceil(limits.emphasisPerHundred * total / 100));
  return emphasis.length > allowed
    ? [finding("R008", emphasis[allowed].number, `${emphasis.length} emphasis lines in ${total} lines; limit is ${allowed}.`,
      "Keep emphasis for the few rules that override others. State the rest plainly.")]
    : [];
}

const SECRET_KINDS = [
  ["Private key", "PrivateKeyRegex"],
  ["AWS access key ID", "AwsKeyRegex"],
  ["GitHub token", "GitHubTokenRegex"],
  ["Slack token", "SlackTokenRegex"],
  ["Anthropic API key", "AnthropicKeyRegex"],
  ["OpenAI API key", "OpenAiKeyRegex"],
  ["Stripe live key", "StripeKeyRegex"],
  ["Google API key", "GoogleKeyRegex"],
  ["JSON Web Token", "JwtRegex"],
  ["Password in connection string", "ConnectionPasswordRegex"],
  ["Credentials in URL", "UrlCredentialsRegex"],
];

const redact = (value) => (value.length <= 8 ? "****" : value.slice(0, 4) + "…");

function findSecrets(text) {
  const found = [];
  for (const [kind, key] of SECRET_KINDS) {
    for (const m of matchAll("SecretPatterns." + key, text, "d")) {
      const hasV = m.groups?.v !== undefined;
      const value = hasV ? m.groups.v : m[0];
      const column = (hasV ? m.indices.groups.v[0] : m.index) + 1;
      if (test("SecretPatterns.PlaceholderRegex", value) || test("SecretPatterns.CommonValueRegex", value) || found.some((f) => f.column === column)) continue;
      found.push({ kind, column, redacted: redact(value) });
    }
  }
  return found.sort((a, b) => a.column - b.column);
}

function r009(file) {
  return file.lines.filter((l) => !l.isBlank).flatMap((line) => {
    const secrets = findSecrets(line.text);
    return secrets.length === 0 ? [] : [finding("R009", line.number,
      `Possible secret: ${secrets.map((s) => `${s.kind} (${s.redacted})`).join(", ")}`,
      "Remove the value and rotate it. Reference an environment variable or secret store by name.")];
  });
}

function r010(file) {
  return file.lines.filter((l) => (l.isProse || l.isHeading) && !isTableRow(l.text)).flatMap((line) => {
    const m = match("PersonaPreamble.PersonaRegex", withoutCode(replace("PersonaPreamble.QuotedRegex", line.text, " QUOTE ")));
    return m ? [finding("R010", line.number, `Persona preamble: "${trim(m[0])}"`,
      "Remove the persona. State the project's facts and rules.")] : [];
  });
}

const headingLevel = (text) => match("EmptySections.LevelRegex", text)?.[1].length ?? 0;

function sectionIsEmpty(lines, empty, i) {
  const level = headingLevel(lines[i].text);
  for (let j = i + 1; j < lines.length; j++) {
    if (lines[j].isHeading) {
      const next = headingLevel(lines[j].text);
      return next < level ? next > 1 || empty[j] : next === level;
    }
    if (!lines[j].isBlank && !isCommentLine(lines[j])) return false;
  }
  return true;
}

const isCommentLine = (line) => line.isDirective || isHtmlComment(line.text);
const headingName = (text) => trim(replace("EmptySections.HeadingTextRegex", text, ""));

function r011(file) {
  const { lines } = file;
  const empty = new Array(lines.length).fill(false);
  for (let i = lines.length - 1; i >= 0; i--) {
    if (lines[i].isHeading) empty[i] = sectionIsEmpty(lines, empty, i);
  }
  const plainText = file.format === "CursorRules" || file.format === "WindsurfRules";
  const sameLevel = (index, level) => index >= 0 && index < lines.length && lines[index].isHeading && headingLevel(lines[index].text) === level;
  const out = [];
  let seenHeading = false;
  let introduced = false;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.isHeading) {
      if (line.isProse && !isCommentLine(line)) introduced = trimEnd(line.text).endsWith(":");
      continue;
    }
    const level = headingLevel(line.text);
    const name = headingName(line.text);
    const exempt = (level === 1 && !seenHeading)
      || introduced
      || name.includes("`") || (count("EmptySections.WordRegex", name) >= limits.headingInstructionMinWords && isInstruction(name))
      || (plainText && (sameLevel(i - 1, level) || sameLevel(i + 1, level)));
    seenHeading = true;
    if (!empty[i] || exempt) continue;
    out.push(finding("R011", line.number, `Section "${name}" has no content.`,
      "Add the rules for this section, or delete the heading."));
  }
  return out;
}

function r012(file) {
  const line = file.unclosedFenceLine;
  if (line === null) return [];
  const rest = file.lines.length - line;
  return [finding("R012", line,
    `Code fence opened on line ${line} is never closed; the remaining ${rest} line${rest === 1 ? "" : "s"} render${rest === 1 ? "s" : ""} as code.`,
    "Close the fence with a matching ``` or ~~~ line.")];
}

function r013(file) {
  if (isDocumentationDump(file)) return [];
  const max = limits.paragraphMaxWords;
  const hasInstruction = (paragraph) => {
    const all = sentences(trim(paragraph));
    return all.filter(isInstruction).length * limits.paragraphInstructionShareDenominator >= all.length;
  };
  const isNonParagraph = (l) => test("ProseParagraph.NonParagraphRegex", l.text);
  const out = [];
  let start = 0;
  let words = 0;
  let text = "";
  let inList = false;
  for (const line of [...file.lines, null]) {
    if (line && isNonParagraph(line) && line.isProse) inList = true;
    else if (!line || line.isBlank || line.isHeading || line.isInCodeFence) inList = false;
    if (line && !inList && line.isProse && !isTableRow(line.text) && !isNonParagraph(line)) {
      if (words === 0) start = line.number;
      words += count("ProseParagraph.WordRegex", withoutCode(line.text));
      text += trim(line.text) + " ";
      continue;
    }
    if (words > max && hasInstruction(text)) {
      out.push(finding("R013", start, `Paragraph of ${words} words; limit is ${max}.`, "Split it into list items, one instruction each."));
    }
    words = 0;
    text = "";
  }
  return out;
}

function r014(file) {
  return proseLines(file).flatMap((line) => {
    if (test("UnverifiableCrossReference.AnchorRegex", line.text)) return [];
    const m = match("UnverifiableCrossReference.ReferenceRegex", line.text);
    return m ? [finding("R014", line.number, `Cross-reference without a target: "${trim(m[0])}"`,
      "Name the section, file, or command it refers to.")] : [];
  });
}

function r015(file) {
  if (file.format !== "CursorMdc") return [];
  const fm = file.frontmatter;
  if (!fm) {
    return [finding("R015", 1, "Cursor rule has no frontmatter.", "Start the file with a --- block containing description, globs, and alwaysApply.")];
  }
  const out = fm.keys.filter((k) => !formats.cursorKeys.includes(k)).sort()
    .map((k) => finding("R015", fm.lineOf(k), `Unknown frontmatter key "${k}"; Cursor reads description, globs, alwaysApply.`,
      "Remove the key or fix its spelling."));
  const always = fm.values("alwaysApply");
  if (fm.has("alwaysApply") && !(always.length === 1 && (always[0] === "true" || always[0] === "false"))) {
    out.push(finding("R015", fm.lineOf("alwaysApply"), "alwaysApply must be true or false.", "Set alwaysApply: true or alwaysApply: false."));
  }
  return out;
}

function r016(file) {
  const fm = file.frontmatter;
  if (file.format !== "CursorMdc" || !fm) return [];
  const always = fm.values("alwaysApply");
  const alwaysOn = always.length === 1 && always[0] === "true";
  return !alwaysOn && fm.values("globs").length === 0 && fm.values("description").length === 0
    ? [finding("R016", 1, "Manual rule: Cursor includes it only when @-mentioned.",
      "To apply it automatically, set alwaysApply: true, add globs, or add a description.")]
    : [];
}

const EVALUATORS = { R001: r001, R002: r002, R003: r003, R004: r004, R005: r005, R007: r007, R008: r008, R009: r009, R010: r010, R011: r011, R012: r012, R013: r013, R014: r014, R015: r015, R016: r016 };

for (const r of rules) {
  if (!EVALUATORS[r.id]) throw new Error(`No browser implementation for ${r.id}`);
}

// ---- LintEngine, Scorer ----

const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

/** Same result shape as LintEngine.Analyze: findings, suppressed, and the resolved format. */
/** disable: rule IDs to skip; their findings are never produced. */
export function analyze(text, format = "Auto", disable = []) {
  const file = parse(text, format);
  const off = new Set(disable.map((id) => id.toUpperCase()));
  const all = rules.filter((r) => !off.has(r.id)).flatMap((r) => EVALUATORS[r.id](file))
    .map((f, i) => ({ f, i }))
    .sort((a, b) => a.f.line - b.f.line || ordinal(a.f.ruleId, b.f.ruleId) || a.i - b.i)
    .map((x) => x.f);
  const findings = all.filter((f) => !file.suppressions.isSuppressed(f));
  const suppressed = all.filter((f) => file.suppressions.isSuppressed(f));
  return { findings, suppressed, format: file.format };
}

/** Same result as Scorer.Score with the engine's rule severities. */
export function score(findings) {
  const counts = new Map();
  for (const f of findings) counts.set(f.ruleId, (counts.get(f.ruleId) ?? 0) + 1);
  const deductionsByRule = [...counts.keys()].sort(ordinal).map((id) => {
    const severity = ruleById.get(id).severity;
    return { ruleId: id, severity, points: Math.min(scoring.perRuleCap, counts.get(id) * scoring.weights[severity]) };
  });
  const deductionsBySeverity = scoring.severityOrder
    .map((severity) => {
      const total = deductionsByRule.filter((d) => d.severity === severity).reduce((s, d) => s + d.points, 0);
      return { severity, total, applied: Math.min(scoring.caps[severity], total), present: deductionsByRule.some((d) => d.severity === severity) };
    })
    .filter((d) => d.present)
    .map(({ present, ...d }) => d);
  const value = Math.max(0, scoring.maxScore - deductionsBySeverity.reduce((s, d) => s + d.applied, 0));
  const grade = scoring.grades.find((g) => value >= g.min).grade;
  return { value, grade, deductionsByRule, deductionsBySeverity };
}

export const ruleInfo = (id) => ruleById.get(id);
export { rulesetVersion };
export const formatName = (format) => formats.names[format];
