using System.Text.RegularExpressions;

namespace Aibysitter.Rules;

/// <summary>Helpers for reading a prose line as instruction clauses.</summary>
internal static partial class InstructionText
{
    /// <summary>True for markdown table rows.</summary>
    public static bool IsTableRow(string text) => text.TrimStart().StartsWith('|');

    /// <summary>
    /// Line content with list marker, emphasis markers, and a leading <c>Label:</c> removed;
    /// inline code, link targets, URLs, and paths replaced by <c>␣CODE␣</c>.
    /// </summary>
    public static string Content(string text)
    {
        var s = CodeSpanRegex().Replace(text, " CODE ");
        s = LinkRegex().Replace(s, "$1");
        s = UrlRegex().Replace(s, " CODE ");
        s = PathRegex().Replace(s, " CODE ");
        s = FileNameRegex().Replace(s, " CODE ");
        s = ListMarkerRegex().Replace(s.Trim(), string.Empty);
        s = s.Replace("**", string.Empty).Replace("__", string.Empty);
        s = LabelRegex().Replace(s, string.Empty);
        return s.Trim();
    }

    /// <summary>Line text with inline code replaced by <c>␣CODE␣</c>; nothing else removed.</summary>
    public static string WithoutCode(string text) => CodeSpanRegex().Replace(text, " CODE ");

    /// <summary>Content split into sentence and clause units.</summary>
    public static IEnumerable<string> Clauses(string text) =>
        ClauseSplitRegex().Split(Content(text)).Select(c => c.Trim()).Where(c => c.Length > 0);

    /// <summary>
    /// True when the text after a matched term names a concrete target: inline code, a path, a parenthesized list,
    /// an "e.g." example, an enumeration of three or more items, or a trailing colon introducing a list.
    /// </summary>
    public static bool HasConcreteTarget(string afterTerm) =>
        afterTerm.Contains("CODE", StringComparison.Ordinal)
        || afterTerm.Contains('(')
        || afterTerm.Contains("e.g.", StringComparison.OrdinalIgnoreCase)
        || afterTerm.Count(c => c == ',') >= 2
        || afterTerm.TrimEnd().EndsWith(':');

    /// <summary>
    /// True when the sentence is an instruction: a clause opens with a directive word or an imperative verb (optionally
    /// after a leading "if / when / ideally … ," clause), or a leading <c>Label:</c> opens with a directive word ("Be concise:"); or the sentence contains
    /// a modal (including "you can / could / may / might"); or it is a list item of at most <see cref="MaxTerseRuleWords"/> words with no verb form from
    /// <c>FiniteVerbRegex</c>, no leading <c>Label:</c> and no leading determiner, pronoun or gerund (a terse rule such as
    /// "- One concept per file."). A list item labelled with inline code or an identifier is a catalog entry, not an
    /// instruction.
    /// </summary>
    public static bool IsInstruction(string sentence)
    {
        if (CatalogEntryRegex().IsMatch(sentence))
        {
            return false;
        }

        var content = Content(sentence);
        return ModalRegex().IsMatch(content)
            || Clauses(sentence).Any(c => ImperativeStartRegex().IsMatch(c))
            || DirectiveLabelRegex().IsMatch(WithoutLabelRemoval(sentence))
            || (ListItemRegex().IsMatch(sentence)
                && !LabelRegex().IsMatch(WithoutLabelRemoval(sentence))
                && WordRegex().Count(content) is > 0 and <= MaxTerseRuleWords
                && !FiniteVerbRegex().IsMatch(content)
                && !DeterminerStartRegex().IsMatch(content));
    }

    /// <summary>Most words in a list item read as a terse rule without a verb.</summary>
    public const int MaxTerseRuleWords = 12;

    /// <summary>Line content as in <see cref="Content"/>, keeping a leading <c>Label:</c>.</summary>
    private static string WithoutLabelRemoval(string text)
    {
        var s = ListMarkerRegex().Replace(WithoutCode(text).Trim(), string.Empty);
        return s.Replace("**", string.Empty).Replace("__", string.Empty).Trim();
    }

    /// <summary>
    /// Numbers of lines that an instruction sentence overlaps. Sentences are read across each unit (<see cref="Units"/>).
    /// </summary>
    public static IReadOnlySet<int> InstructionLines(RulesFile file)
    {
        var result = new HashSet<int>();
        foreach (var unit in Units(file))
        {
            var (text, starts) = Join(unit);
            var instructions = SentenceSpans(text).Where(s => IsInstruction(text.Substring(s.Start, s.Length))).ToList();
            foreach (var line in unit)
            {
                var start = starts[line] + (line.Text.Length - line.Text.TrimStart().Length);
                var end = start + line.Text.Trim().Length;
                if (instructions.Any(s => s.Start < end && s.Start + s.Length > start))
                {
                    result.Add(line.Number);
                }
            }
        }

        return result;
    }

    /// <summary>Text split at sentence ends.</summary>
    public static IEnumerable<string> Sentences(string text) => SentenceSplitRegex().Split(text);

    /// <summary>
    /// Instruction units: a list item with its continuation lines, or a run of paragraph lines. Units end at blank lines,
    /// headings, code fences, frontmatter, suppression comments and table rows; a list marker starts a new unit.
    /// A documentation dump (<see cref="IsDocumentationDump"/>) has none.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<RulesLine>> Units(RulesFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var units = new List<IReadOnlyList<RulesLine>>();
        if (IsDocumentationDump(file))
        {
            return units;
        }

        List<RulesLine>? current = null;
        foreach (var line in file.Lines)
        {
            if (!line.IsProse || IsTableRow(line.Text))
            {
                current = null;
                continue;
            }

            if (current is null || ListItemRegex().IsMatch(line.Text))
            {
                current = [];
                units.Add(current);
            }

            current.Add(line);
        }

        return units;
    }

    /// <summary>Fewest MDX component lines outside code fences for a file to be read as pasted documentation.</summary>
    public const int DocumentationDumpMinComponents = 20;

    /// <summary>
    /// True when the file has <see cref="DocumentationDumpMinComponents"/> or more MDX component lines outside code fences:
    /// pasted product documentation. It has no instruction units.
    /// </summary>
    public static bool IsDocumentationDump(RulesFile file) =>
        file.Lines.Count(l => !l.IsInCodeFence && MdxComponentRegex().IsMatch(l.Text)) >= DocumentationDumpMinComponents;

    /// <summary>A unit's lines trimmed and joined with one space, with each line's start offset in the joined text.</summary>
    public static (string Text, IReadOnlyDictionary<RulesLine, int> Starts) Join(IReadOnlyList<RulesLine> unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var text = new System.Text.StringBuilder();
        var starts = new Dictionary<RulesLine, int>(ReferenceEqualityComparer.Instance);
        foreach (var line in unit)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            starts[line] = text.Length - (line.Text.Length - line.Text.TrimStart().Length);
            text.Append(line.Text.Trim());
        }

        return (text.ToString(), starts);
    }

    /// <summary>Sentence spans of the text: start index and length, in order.</summary>
    public static IReadOnlyList<(int Start, int Length)> SentenceSpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var spans = new List<(int, int)>();
        var start = 0;
        foreach (Match boundary in SentenceSplitRegex().Matches(text))
        {
            spans.Add((start, boundary.Index - start));
            start = boundary.Index + boundary.Length;
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    /// <summary>True for list-item lines.</summary>
    public static bool IsListItem(string text) => ListItemRegex().IsMatch(text);

    /// <summary>True for a line that is one HTML comment.</summary>
    public static bool IsHtmlComment(string text) => HtmlCommentRegex().IsMatch(text);

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex(@"\[([^\[\]]*)\]\([^)\[]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<![\w.-])[\w.-]*/[\w./-]+")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"(?<![\w-])[\w-]+\.(?:md|mdc|json|ya?ml|toml|txt|cs|csproj|ts|tsx|js|jsx|py|go|rs|rb|sh|ps1|sql|xml|html|css)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?")]
    private static partial Regex ListMarkerRegex();

    [GeneratedRegex(@"^[^:.;!?]{1,40}:\s+")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"(?<=[.;!?])\s+|(?<!\s)\s+(?:—|–|-)\s+")]
    private static partial Regex ClauseSplitRegex();

    /// <summary>Sentence ends: ".", "!", "?" or ";" followed by whitespace, not after "e.g.", "i.e.", "etc." or "vs.".</summary>
    [GeneratedRegex(@"(?<!\b(?:e\.g|i\.e|etc|vs)\.)(?<=[.;!?])\s+", RegexOptions.IgnoreCase)]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\s*<!--.*-->\s*$")]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'_.-]*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"^(?:be|keep|use|avoid|prefer|never|always|don't|do\s+not|make\s+sure|stay|stick)\b[^:.;!?]{0,40}:", RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveLabelRegex();

    /// <summary>Copulas, auxiliaries and common third-person verbs: a sentence with one describes rather than instructs.</summary>
    [GeneratedRegex(@"\b(?:is|are|was|were|isn't|aren't|has|have|had|does|did|will|would|can|could|may|might|lives|runs|uses|contains|includes|returns|provides|handles|requires|needs|makes|takes|gives|shows|holds|stores|reads|writes|calls|wraps|exports|adds|prevents|flags|fails|passes|means|helps|ensures|allows|lets|supports|depends|works|starts|loads|sends|creates|generates|builds|owns|sits|goes|comes|becomes|exists|lists|describes|defines|covers|maps|points|refers)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FiniteVerbRegex();

    [GeneratedRegex(@"^(?:the|a|an|this|that|these|those|it|its|our|their|his|her|we|they|he|she|i|there|here|each|every|all|some|most|many|both|either|neither|which|who|what|CODE|[a-z]+ing)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeterminerStartRegex();

    /// <summary>
    /// Modal or obligation anywhere in the sentence. "do not / don't" after a subject pronoun ("we do not", "that do not")
    /// is descriptive; "never" counts only at a clause start (<c>ImperativeStartRegex</c>).
    /// </summary>
    [GeneratedRegex(@"\b(?:must|mustn't|should|shouldn't|shall|needs?\s+to|ha(?:ve|s)\s+to|(?:is|are)\s+required|you\s+(?:can|could|may|might))\b|\b(?=do\s+not\b|don't\b)(?<!\b(?:we|they|it|i|which|that|who|these|those|people|users)\s+(?:[a-z]+ly\s+)?)(?:do\s+not|don't)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModalRegex();

    /// <summary>MDX documentation components at a line start: pasted product documentation.</summary>
    [GeneratedRegex(@"^\s*</?(?:Note|Tip|Warning|Info|Frame|Tabs?|Accordion(?:Group)?|CodeGroup|Steps?|Card(?:Group)?|Check|Expandable|ResponseField|ParamField)\b")]
    private static partial Regex MdxComponentRegex();

    /// <summary>A list item labelled with inline code or an identifier ("- `tool_name` — …", "- **kebab-name**: …"): a catalog entry.</summary>
    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+(?:\*\*|__)?(?:`[^`]+`|[a-z0-9]+(?:[-_][a-z0-9]+)+)(?:\*\*|__)?\s*(?:[:—–]|-\s)")]
    private static partial Regex CatalogEntryRegex();

    /// <summary>
    /// Clause opens, optionally after one leading "if / when / before … ," clause, with a directive word or a common imperative verb.
    /// </summary>
    [GeneratedRegex(@"^(?:(?:if|when|whenever|where|wherever|before|after|unless|once|while|for|ideally|preferably)\b[^,]{0,80},\s*)?(?:(?:always|must|should|never|please|do\s+not|don't|make\s+sure)\b|(?:(?:only|also|then|just)\s+)?(?:use|add|run|create|check|keep|avoid|follow|write|call|set|make|prefer|return|update|verify|validate|select|export|edit|assign|move|organize|configure|cache|continue|read|merge|adapt|wrap|log|put|place|define|store|import|include|apply|choose|pick|install|build|deploy|commit|review|refactor|implement|handle|manage|ensure|treat|throw|catch|raise|split|sort|mark|limit|scale|tune|register|inject|convert|escape|sanitize|encode|close|dispose|release|retry|notify|load|save|fetch|send|clean|remove|delete|replace|rename|extend|override|reuse|share|enable|disable|initialize|init|stop|do|be|try|ask|test|document|let|wait|yield|report|respond|reply|explain|mention|start|look|find|search|prefix|name|format|lint|push|open|ignore|skip|leave|give|provide|generate|output|print|show|list|state|note|remember|consider|think|plan|confirm|comment|declare|separate|group|order|focus|stick|target|match|mirror|copy|pass|prompt|tell|summarize|describe|reference|link|cite|flag|emit|exit|abort|fail|drop|batch|lock|pin|bump|tag|label|track|measure|profile|benchmark|monitor|watch|parse|serialize|render|display|answer|clarify|request|query|refer|see|assume|go|get|design|automate|annotate|attach|avoid|break|bundle|capture|change|clear|collect|combine|compare|compile|compose|compute|connect|construct|contain|convey|correct|count|cover|debug|decide|declare|delegate|deprecate|derive|detect|determine|develop|disallow|distinguish|divide|download|draft|duplicate|edit|embed|emphasize|enforce|enter|establish|estimate|evaluate|examine|exclude|execute|expand|expect|explore|expose|express|extract|favor|favour|fill|filter|finish|fix|follow|force|forbid|forward|gather|grant|guard|guide|hide|highlight|hold|identify|implement|improve|increase|indent|inform|inherit|insert|inspect|instantiate|integrate|introduce|invoke|isolate|iterate|join|justify|keep|kill|lazy-load|lead|learn|lift|lower|maintain|map|maximize|migrate|minimize|mock|modify|mount|name|navigate|nest|normalize|obey|observe|obtain|offer|omit|optimize|organise|paginate|patch|pause|perform|persist|pick|poll|populate|post|preserve|prevent|process|produce|protect|prune|publish|pull|purge|qualify|quote|reach|rebase|rebuild|receive|record|recover|redact|reduce|reject|reload|repeat|rephrase|reproduce|require|reset|resize|resolve|restart|restore|restrict|resume|rethink|reveal|revert|rewrite|rotate|route|scan|schedule|scope|secure|seed|sign|simplify|specify|squash|stage|standardize|stash|stream|strip|structure|stub|submit|substitute|suggest|supply|suppress|switch|sync|throttle|toggle|trace|transform|translate|trigger|trim|trust|turn|unify|unwrap|upgrade|upload|validate|vary|version|walk|warn|work|wrap|yield|zip)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex ImperativeStartRegex();
}
