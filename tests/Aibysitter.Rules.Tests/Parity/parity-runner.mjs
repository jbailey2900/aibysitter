// Runs the browser lint engine for RulesParityTests.
// Usage: node parity-runner.mjs <wwwroot/js directory> <lint|patterns>
// lint:     stdin [{name, text, format, disable}] -> stdout [{name, format, findings, suppressed, score}]
// patterns: stdin {patterns:[key], lines:[]} -> stdout {key: [[index, length], ...] per line, or {error}}
// share:    stdin {encode:[state], decode:[fragment], options} -> stdout {encoded:[fragment], decoded:[result]}
// time-patterns: stdin [{key, text}] -> stdout [ms]   best of 3 after a warm-up, matchAll of the pattern
// time-lint:     stdin [text] -> stdout [ms]          best of 3 after a warm-up, analyze with format Auto
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { join } from "node:path";

const [jsDir, mode] = process.argv.slice(2);
const input = JSON.parse(readFileSync(0, "utf8"));
const engine = await import(pathToFileURL(join(jsDir, "lint-engine.mjs")).href);
const generated = (await import(pathToFileURL(join(jsDir, "generated", "rules-patterns.mjs")).href)).default;

function lint(items) {
  return items.map(({ name, text, format, disable }) => {
    const result = engine.analyze(text, format, disable ?? []);
    return { name, format: result.format, findings: result.findings, suppressed: result.suppressed, score: engine.score(result.findings) };
  });
}

function matchPositions(key, lines) {
  const p = generated.patterns[key];
  let re;
  try {
    re = new RegExp(p.source, p.flags + "g");
  } catch (e) {
    return { error: e.message };
  }
  return lines.map((line) => [...line.matchAll(re)].map((m) => [m.index, m[0].length]));
}

async function share({ encode, decode, options }) {
  const link = await import(pathToFileURL(join(jsDir, "share-link.mjs")).href);
  return {
    encoded: await Promise.all(encode.map((state) => link.encode(state))),
    decoded: await Promise.all(decode.map((fragment) => link.decode(fragment, options))),
  };
}

function bestOfThree(action) {
  action();
  let best = Infinity;
  for (let i = 0; i < 3; i++) {
    const start = performance.now();
    action();
    best = Math.min(best, performance.now() - start);
  }
  return best;
}

function timePatterns(items) {
  return items.map(({ key, text }) => {
    const p = generated.patterns[key];
    const re = new RegExp(p.source, p.flags + "g");
    return bestOfThree(() => [...text.matchAll(re)].length);
  });
}

const timeLint = (texts) => texts.map((text) => bestOfThree(() => engine.analyze(text, "Auto", [])));

const output = mode === "time-patterns" ? timePatterns(input)
  : mode === "time-lint" ? timeLint(input)
  : mode === "patterns"
  ? Object.fromEntries(input.patterns.map((key) => [key, matchPositions(key, input.lines)]))
  : mode === "share" ? await share(input) : lint(input);
process.stdout.write(JSON.stringify(output));
