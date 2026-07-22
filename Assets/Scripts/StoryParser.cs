using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

public class ParsedStory
{
    public string Title;
    public string CrimeSummary;
    public string Victim;
    public string CrimeScene;
    public string Suspects;
    public string Clues;
    public string RealMurderer;
    public string[] InitialActions = new string[3];
}

public class ActionResponse
{
    public string Result;
    public string NewAction;
    public string NewClue;
}

public static class StoryParser
{
    // A candidate section heading line: markdown-marked (leading #'s and/or **bold**),
    // where real content is allowed to continue right after the colon on the same line
    // (e.g. "**Victim:** Ellyn Harnett..."); OR a bare, unmarked line, which must occupy
    // the line by itself with nothing trailing; OR the one confirmed non-colon paraphrase
    // ("What really happened?"), also required to stand alone; OR a pure markdown header
    // with no colon at all (e.g. "#### Crime Summary" on its own line, nothing else) -
    // confirmed in an actual playthrough: the model sometimes formats every section as a
    // bare "####"-prefixed heading with no trailing colon anywhere, which used to fail
    // every alternative here (all of them required a colon), so not one of the 6 real
    // section headers was recognized - only "Initial Player Actions:" survived, purely
    // because that one specific header happened to still have a colon. Every other
    // section's content silently fell into Title (never displayed) instead of its own
    // field. Since this alternative has no colon to mark where the heading text ends, it
    // additionally requires standalone-ness itself (nothing trailing after the closing
    // #'s), same as the bare-line alternative above - a "####" line always safely acts as
    // a boundary-only marker (StripMarkers deletes any "#"-prefixed line's content
    // wholesale anyway), so this can't be mistaken for real body text.
    // OR a bare, unmarked heading with real content trailing on the SAME line (e.g. "Real
    // Murderer: Renna Dill - using the missing key..."), which the standalone-bare
    // alternative above can't catch since it requires nothing after the colon, and the
    // marked-up alternative can't catch since it requires # or ** markup. Confirmed in an
    // actual playthrough - a serious one: the model wrote "Clue: <text>" and "Real
    // Murderer: <text>" as bare same-line labels after the Suspects section, and since
    // neither matched any existing alternative, both (and everything after, up to the next
    // recognized heading) silently became trailing content of Suspects - meaning the
    // murderer reveal itself rendered verbatim on the Story screen, a real spoiler, not
    // just a formatting nit. This alternative alone would be dangerous unrestricted (it
    // would treat any ordinary "Word: sentence." prose as a heading) - what makes it safe
    // is that it's still filtered by the same isKnown-or-standalone gate in Parse() below:
    // since content trails on the same line, standalone is always false for a match here,
    // so it only ever actually becomes a boundary when the label is a recognized keyword
    // (see KnownHeadingKeywords) - an unrecognized bare "Word: sentence" is found as a
    // candidate but then discarded, same as it always was.
    // See Parse() for why an unmarked/unrecognized heading additionally needs to be
    // standalone before it's trusted as a real boundary - this regex only finds candidates.
    private static readonly Regex CandidateHeadingLine = new Regex(
        @"^[ \t]*(?:#{1,6}[ \t]*\**|\*\*)([A-Za-z][A-Za-z0-9 '\-#]{0,40}?)\**[ \t]*:" +
        @"|^[ \t]*([A-Za-z][A-Za-z0-9 '\-#]{0,40}?)[ \t]*:[ \t]*$" +
        @"|^[ \t]*(what\s+really\s+happened)\s*\??[ \t]*$" +
        @"|^[ \t]*#{1,6}[ \t]*\**([A-Za-z][A-Za-z0-9 '\-#]{0,40}?)\**[ \t]*$" +
        @"|^[ \t]*([A-Za-z][A-Za-z0-9 '\-#]{0,40}?)[ \t]*:[ \t]+(?=\S)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Which known field a detected heading's text belongs to, checked by keyword rather
    // than exact wording so paraphrases ("Three Suspects", "At least 5 Clues", "At the
    // scene") still match. Each pattern is deliberately just the single most distinctive
    // word for that field - not the full expected phrase - since the model varies the
    // rest of the heading freely (e.g. "At the scene:" instead of "Crime Scene:", "What
    // happened to victim:" instead of "Victim:"). A heading that matches none of these -
    // "Missing Items:", "Identified Visitors:", or anything else the model invents on its
    // own - is simply discarded below instead of being glued onto whatever wanted section
    // happened to come right before it.
    private static readonly (string Key, string Pattern)[] KnownHeadingKeywords =
    {
        ("CrimeSummary", @"summary"),
        ("Victim", @"victim"),
        ("CrimeScene", @"scene"),
        ("Suspects", @"suspects?"),
        ("Clues", @"clues?(\s*#?\s*\d+)?"),
        ("RealMurderer", @"murderer|what\s+really\s+happened"),
        ("InitialActions", @"actions"),
    };

    private static string ClassifyHeading(string headingText)
    {
        foreach (var (key, pattern) in KnownHeadingKeywords)
            if (Regex.IsMatch(headingText, pattern, RegexOptions.IgnoreCase))
                return key;
        return null;
    }

    // CandidateHeadingLine has five alternatives (marked-up-with-colon, bare-standalone-
    // with-colon, the "what really happened" paraphrase, bare markdown-header-with-
    // no-colon, and bare-with-same-line-content), which land in different capture groups.
    private static string GetHeadingText(Match headingLine)
    {
        if (headingLine.Groups[1].Success) return headingLine.Groups[1].Value;
        if (headingLine.Groups[2].Success) return headingLine.Groups[2].Value;
        if (headingLine.Groups[3].Success) return headingLine.Groups[3].Value;
        if (headingLine.Groups[4].Success) return headingLine.Groups[4].Value;
        return headingLine.Groups[5].Value;
    }

    // Whether nothing but whitespace/asterisks follows a position through to the end of
    // its line - i.e. whether a heading candidate is genuinely alone on its own line.
    private static bool RestOfLineIsBlank(string raw, int index)
    {
        int lineEnd = raw.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = raw.Length;
        return string.IsNullOrWhiteSpace(raw.Substring(index, lineEnd - index).Replace("*", ""));
    }

    public static ParsedStory Parse(string raw)
    {
        var result = new ParsedStory();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        raw = raw.Replace("\r\n", "\n").Trim();

        // A candidate only counts as a real section boundary if either (a) we recognize
        // it as one of our known fields, or (b) it's alone on its own line with nothing
        // trailing. Without requirement (b), an incidental "**Name**: description"
        // sentence used as body-text emphasis - e.g. "**Ashley Voss**: A local street
        // musician..." inside the Victim section itself - would wrongly be treated as a
        // brand-new (unrecognized) heading and swallow the rest of that section, since it
        // has the exact same shape as a real "**Victim:** ..." heading. Requiring
        // standalone-ness for anything we don't already recognize avoids that, while still
        // letting recognized headings have real content trailing on the same line (as
        // "**Victim:** Ellyn Harnett..." does).
        var headingLines = new List<Match>();
        foreach (Match candidate in CandidateHeadingLine.Matches(raw))
        {
            string headingKey = ClassifyHeading(GetHeadingText(candidate));
            bool standalone = RestOfLineIsBlank(raw, candidate.Index + candidate.Length);

            // CandidateHeadingLine's 5th alternative (bare heading, no markup, real
            // content trailing on the same line) is narrower than the other four -
            // confirmed via direct testing that trusting it for every known field is too
            // easy to trip on ordinary narrative prose that happens to start a line with
            // a common word immediately followed by a colon: "Suspects: three people had
            // motive..." as an ordinary Crime Scene sentence wrongly carved out a bogus
            // Suspects heading right there, wiping out the real Crime Scene content that
            // should have followed. Scoped to just "Clues"/"RealMurderer" - the two
            // fields actually confirmed (in a real playthrough, a serious one - the
            // murderer reveal itself leaked onto the Story screen) to use this bare
            // same-line style, rather than opening it up to all seven known fields on
            // pure speculation.
            bool isBareSameLine = candidate.Groups[5].Success;
            bool trusted = headingKey != null
                && (!isBareSameLine || headingKey == "Clues" || headingKey == "RealMurderer");

            if (trusted || standalone)
                headingLines.Add(candidate);
        }

        if (headingLines.Count == 0)
        {
            // The model occasionally abandons every section header and writes a pure
            // narrative instead. Rather than discard the whole response, show it as
            // the Crime Summary so the Story scene isn't left completely blank -
            // Suspects/Clues/Initial Actions still get filled in via their own
            // follow-up requests regardless of the original formatting.
            result.CrimeSummary = StripMarkers(raw);
            return result;
        }

        result.Title = StripMarkers(raw.Substring(0, headingLines[0].Index)).Trim('*', '#', ' ', '\n');

        // Every remaining heading-style line ends whatever section came before it, even
        // if the heading itself isn't one we recognize - this is what stops the model's
        // own invented sections ("Missing Items:", "Identified Visitors:") from silently
        // becoming trailing content of the previous wanted section instead of being
        // dropped. A key can appear more than once (the model sometimes writes "Clue #1:",
        // "Clue #2:", "Clue #3:" instead of one "Clues:" heading with a list under it), so
        // gather every occurrence per key rather than only keeping the first.
        var sectionParts = new Dictionary<string, List<string>>();
        for (int i = 0; i < headingLines.Count; i++)
        {
            string key = ClassifyHeading(GetHeadingText(headingLines[i]));
            if (key == null)
                continue;

            int start = headingLines[i].Index + headingLines[i].Length;
            int end = i + 1 < headingLines.Count ? headingLines[i + 1].Index : raw.Length;
            string content = TrimHallucinatedQuestion(StripMarkers(raw.Substring(start, end - start)));

            if (!sectionParts.TryGetValue(key, out List<string> parts))
                sectionParts[key] = parts = new List<string>();
            parts.Add(content);
        }

        // These five are displayed as whole blocks in StoryDisplay (Clues shown raw, not
        // the item-extracted GameSession.Clues list), so any trailing junk the model tacks
        // on after the real content - a stray fragment, an echoed prompt phrase, a
        // dangling empty list marker, or anything else - needs trimming at the section
        // level: cut at the last genuine sentence and discard whatever follows.
        result.CrimeSummary = TrimTrailingJunk(CombineParts(sectionParts, "CrimeSummary"));
        result.Victim = TrimTrailingJunk(CombineParts(sectionParts, "Victim"));
        result.CrimeScene = TrimTrailingJunk(CombineParts(sectionParts, "CrimeScene"));
        result.Suspects = TrimTrailingJunk(CombineParts(sectionParts, "Suspects"));
        result.Clues = TrimTrailingJunk(CombineParts(sectionParts, "Clues"));
        result.RealMurderer = TrimHallucinatedMenu(TrimTrailingJunk(CombineParts(sectionParts, "RealMurderer")));
        result.InitialActions = ExtractNumberedLines(CombineParts(sectionParts, "InitialActions"));

        // The model doesn't always emit a "Crime Summary:" header - sometimes none of
        // Crime Summary/Victim/Crime Scene appear at all, but just as often Victim and
        // Crime Scene are both properly labeled and it's only the Crime Summary heading
        // that's skipped, with the intro prose left dangling before "Victim:" instead.
        // Either way that leading prose ends up captured in Title (never read anywhere
        // else), so promote it into CrimeSummary whenever CrimeSummary itself is empty -
        // no need to also require Victim/CrimeScene to be missing.
        if (!string.IsNullOrEmpty(result.Title) && string.IsNullOrEmpty(result.CrimeSummary))
        {
            result.CrimeSummary = result.Title;
        }

        return result;
    }

    private static string CombineParts(Dictionary<string, List<string>> sectionParts, string key)
    {
        if (!sectionParts.TryGetValue(key, out List<string> parts) || parts.Count == 0)
            return null;
        if (parts.Count == 1)
            return parts[0];

        // Each repeated heading's body already arrives here as its own clean segment (the
        // "Clue #2:" label itself was consumed as the boundary, not left in the text), so
        // re-present the parts as a bulleted list - ExtractListItems/ExtractSuspectNames
        // downstream already know how to split a bulleted list back into separate items -
        // rather than losing the boundary between them in a flat concatenation.
        var sb = new StringBuilder();
        foreach (string part in parts)
        {
            // A single part can itself already contain its own numbered/bulleted list -
            // e.g. a normal "Clues:" heading with 3 numbered clues underneath, alongside
            // a second "Final Clue:" heading for a 4th. Confirmed in an actual
            // playthrough: flattening a part like that down to one line the same way a
            // genuinely single-item part (like "Final Clue:"'s own one paragraph) is
            // flattened collapsed all 3 clues into one run-on blob, which broke
            // ExtractListItems' downstream ability to split them back apart - and left a
            // stray leading "1." sitting in the middle of what looked like plain prose,
            // which GameManager.TruncateClue then mistook for a complete one-token
            // "sentence" (a digit followed by a period followed by a capital letter
            // satisfies its sentence-end check), truncating the whole clue down to just
            // "1.". Re-splitting a multi-item part into its own items first - falling
            // back to the old whole-part flatten only when a part has no list of its own
            // - keeps every individual item intact regardless of which heading
            // contributed it.
            List<string> ownItems = ExtractListItems(part);
            if (ownItems.Count > 1)
            {
                foreach (string item in ownItems)
                {
                    string flattenedItem = Regex.Replace(item, @"\s+", " ").Trim();
                    if (flattenedItem.Length > 0)
                        sb.Append("- ").Append(flattenedItem).Append('\n');
                }
                continue;
            }

            string flattened = Regex.Replace(part, @"\s+", " ").Trim();
            if (flattened.Length > 0)
                sb.Append("- ").Append(flattened).Append('\n');
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    public static string[] ParseInitialActions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new string[3];

        raw = raw.Replace("\r\n", "\n").Trim();
        Match m = Regex.Match(raw, @"initial\s+player\s+actions\s*:", RegexOptions.IgnoreCase);
        if (!m.Success)
            return new string[3];

        string content = StripMarkers(raw.Substring(m.Index + m.Length));
        return ExtractNumberedLines(content);
    }

    // For the standalone Suspects/Clues follow-up responses (LLMStoryClient.RequestSuspects/
    // RequestClues) - these used to call ExtractSuspectNames/ExtractClueList directly on the
    // FULL raw response, heading included, unlike ParseInitialActions above which already
    // slices past its own "Initial Player Actions:" heading first. Confirmed in an actual
    // playthrough: when the model answers in paragraph form (no numbered/bulleted list),
    // ExtractSuspectEntries' paragraph-fallback treats the response's own leading
    // "Suspects:" line as if it were the first suspect's own paragraph - nothing in that
    // fallback path knows to skip a heading, since Parse() normally strips that boundary
    // out structurally before either extractor ever sees the text. That leaked the literal
    // word "Suspects" into GameSession.SuspectNames, which then displayed as a bare,
    // meaningless guess-screen button in place of a real suspect. Slicing past the heading
    // here - falling back to the whole response if the model skipped the heading entirely,
    // same as ParseRealMurderer already does - fixes it at the source instead of teaching
    // every extractor tier to recognize a heading it was never supposed to see.
    public static List<string> ParseSuspectsFollowUp(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        raw = raw.Replace("\r\n", "\n").Trim();
        Match m = Regex.Match(raw, @"suspects\s*:", RegexOptions.IgnoreCase);
        string content = m.Success ? raw.Substring(m.Index + m.Length) : raw;
        return ExtractSuspectNames(content);
    }

    public static List<string> ParseCluesFollowUp(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        raw = raw.Replace("\r\n", "\n").Trim();
        Match m = Regex.Match(raw, @"clues\s*:", RegexOptions.IgnoreCase);
        string content = m.Success ? raw.Substring(m.Index + m.Length) : raw;
        return ExtractClueList(content);
    }

    // For the standalone Real Murderer follow-up response (a narrow, single-purpose
    // request, unlike the full story). Reuses Parse() so the same heading-paraphrase
    // handling ("What really happened?") applies here too, rather than duplicating it.
    public static string ParseRealMurderer(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Confirmed in an actual playthrough: this narrow follow-up can hallucinate a
        // fake "meta" continuation past the real answer - a "---" divider followed by
        // invented UI copy ("Return to Main Menu... players can now make new choices")
        // and even a fabricated "For debugging purposes, here is how the response was
        // generated" explanation of its own reasoning, which used to survive all the way
        // into the guess-the-murderer reveal shown to the player. Cut it the same way
        // ParseActionResponse already does for the same class of runaway hallucination.
        raw = CutHallucinatedContinuation(raw.Replace("\r\n", "\n").Trim());

        ParsedStory parsed = Parse(raw);
        string result = !string.IsNullOrWhiteSpace(parsed.RealMurderer)
            ? parsed.RealMurderer
            // The model skipped the "Real Murderer:" heading entirely (Rule #1: never
            // trust it to follow instructions) - Parse() already promotes
            // headerless/unclassified content into CrimeSummary as a catch-all, and
            // since this follow-up never asks for anything else, that's very likely
            // just the answer with no other section to have conflated it with.
            : parsed.CrimeSummary;

        if (string.IsNullOrWhiteSpace(result))
            return null;

        // A confirmed edge case within the fallback above: a bare, single-line reply
        // with content trailing on the same line - e.g. "Real Murderer: Ron Tennyson."
        // with no markup at all - isn't recognized as a heading by CandidateHeadingLine
        // (it requires either markup, or the colon to end the line), so the label itself
        // leaks into the fallback text and would have shown up verbatim in the
        // guess-the-murderer reveal. Strip it defensively since this follow-up's context
        // makes it safe to assume the label always means the same thing.
        result = Regex.Replace(result, @"^\s*(real\s+murderer|what\s+really\s+happened)\s*[:?]\s*", "", RegexOptions.IgnoreCase).Trim();

        return TrimHallucinatedMenu(result);
    }

    // A different, subtler hallucination than CutHallucinatedContinuation catches: the
    // model sometimes stops narrating and starts either addressing the player directly
    // ("What would you like the player to do next?", "What would you like to do now?")
    // or listing out speculative bullet points / a menu of possible next actions instead
    // of finishing the narrative it was asked for. Confirmed in an actual playthrough for
    // both fields this guards: the Real Murderer follow-up (a direct player-address
    // followed by a fabricated numbered list of new actions), and the per-turn Result
    // field ("Based on these findings:" plus its own speculative bulleted list, then
    // "What would you like to do now?" plus a numbered menu that competes with the single
    // next action this game already generates for the button). None of
    // CutHallucinatedContinuation's markers (###, ---, ##) appear in either case, so it
    // used to survive all the way into what's shown to the player. Neither a numbered nor
    // bulleted list, nor a direct address to "the player"/"players", should ever
    // legitimately appear in either field, so any of these is treated as a hard stop -
    // cut at the start of whichever offending line comes first, not just the trigger
    // word, so a fragment like "What would you like the" doesn't survive as trailing
    // junk.
    //
    // A further variant confirmed in an actual playthrough: instead of a numbered/
    // bulleted menu, the model can hallucinate a lettered multiple-choice quiz -
    // "What clue does this envelope reveal?" followed by "A. ...", "B. ...", "C. ..." -
    // which the digit/bullet-only marker above doesn't catch at all (A./B. aren't
    // \d+[.\)] or [-*•]), so it used to survive straight into the displayed Result. A
    // single lettered line isn't enough of a signal on its own to treat as a hard stop -
    // it would false-positive on a genuine name written with a spaced initial ("A. J.
    // Reeves refused to comment.") - so this requires at least TWO consecutive lettered-
    // marker lines before it's trusted, which a real multiple-choice list always has but
    // an ordinary sentence with an initial never does. The "what clue does this X
    // reveal?" question itself is also matched directly (same unanchored treatment as
    // "players?" above, since Result is free-form prose with no fixed line shape to
    // anchor to) - without it, trimming just the lettered options left the question
    // dangling on its own as if it were genuine narration, once its own answer choices
    // were gone the question mark satisfied the normal end-of-Result sentence boundary.
    private static readonly Regex HallucinatedMenuMarker = new Regex(
        @"^\s*(?:\d+[.\)]|[-*•])\s|^.*\bplayers?\b.*$|(?:^[ \t]*[A-Za-z][.\)][ \t]+\S[^\n]*\r?\n?){2,}|what\s+clue[^\n?]*\?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string TrimHallucinatedMenu(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        Match m = HallucinatedMenuMarker.Match(text);
        return m.Success ? text.Substring(0, m.Index).Trim() : text;
    }

    // Unlike TrimHallucinatedMenu above (only safe for Result/RealMurderer, which should
    // never contain a numbered list at all), this can't use the same blind "any numbered
    // line" check - Suspects/Clues/InitialActions are exactly the sections that ARE
    // supposed to contain one, so that marker would wrongly cut off the real content at
    // its own first legitimate list item. Confirmed in an actual playthrough: the model
    // hallucinated "What would you like me to say?" plus its own self-answered numbered
    // list right after a genuine "Clues:" list, which - since the question itself doesn't
    // end in a colon and isn't recognized as a heading boundary - silently became trailing
    // content of the Clues section instead of being dropped.
    //
    // Unlike ParseActionResponse's use of the same MenuPromptPattern (deliberately
    // unanchored there, since Result is free-form prose with no fixed shape to anchor
    // to), this one IS anchored to a standalone line. Confirmed necessary via direct
    // testing: a suspect's own quoted dialogue can innocently contain a phrase like "what
    // would you like me to do about the broken lock?" as characterization, sitting
    // mid-sentence inside an otherwise ordinary Suspects paragraph - matching it
    // unanchored wrongly truncated the entire section right there. The genuine
    // hallucination confirmed above always presents as its own interjected line (a full
    // paragraph break before and after), which a quoted rhetorical question buried
    // mid-sentence can't satisfy, so anchoring catches the real case while leaving
    // legitimate narrative content alone.
    private static readonly Regex HallucinatedQuestionLine = new Regex(
        @"^[ \t]*\**[ \t]*" + MenuPromptPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string TrimHallucinatedQuestion(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        Match m = HallucinatedQuestionLine.Match(content);
        return m.Success ? content.Substring(0, m.Index).Trim() : content;
    }

    // Shared with ParseActionResponse (and with FindFakeHeaderCutoff just below) so the
    // "what counts as this response's own action/clue header" definition can't drift out
    // of sync between the two places that need to recognize it.
    private const string ActionHeaderPattern = @"(?:new(?:\s+player['’]?s?)?|next(?:\s+player['’]?s?)?)\s+action\s*:";
    private const string ClueHeaderPattern = @"new\s+clue[^:.?\n]*[:.?]";

    // A header phrase must start its own line - optionally with markdown "#"/"*" noise
    // in front (same leniency as CandidateHeadingLine elsewhere in this file), and now
    // also optionally ONE short leading word ("A new clue discovered:" - confirmed in an
    // actual playthrough, where the bare line-start requirement rejected this because of
    // the leading "A "). Bounded to a short run of letters so it can't swallow a whole
    // hallucinated clause the way an unbounded prefix would - the protection against a
    // hallucinated MID-SENTENCE mention (e.g. "Now, provide a new clue about Ethan Vale,
    // only if you have one...") is still this being anchored to the line start: that
    // phrase sits several words into the middle of its own line with no legitimate
    // header reachable from where the line actually begins, so it still can't satisfy
    // this prefix no matter how many leading words are allowed.
    private const string OptionalLeadWord = @"(?:[A-Za-z]{1,15}[ \t]+)?";
    private const string HeaderLinePrefix = @"^[ \t]*" + OptionalLeadWord + @"#{0,6}[ \t]*\**[ \t]*";

    // "What would you like the detective to do now?"/"What would you like to do now?" -
    // confirmed variants of the model abandoning the "New Player Action:" header and
    // addressing the player directly instead. Deliberately not anchored to a line start
    // like the real headers above (unlike those, this phrase has no fixed markdown
    // styling convention confirmed yet, so line-anchoring would just be guessing) -
    // matched loosely up to whichever terminator (?/.) the model used. Also confirmed in
    // the main story response itself (not just per-turn results): the model can
    // hallucinate this same direct-address interjection - "What would you like me to
    // say?" plus its own self-answered numbered list - immediately after the Clues
    // section, before recovering with the real "Initial Player Actions:" heading. "to
    // say" broadens the original "to do"-only match to cover that variant too, shared
    // by both Parse() (see TrimHallucinatedQuestion) and ParseActionResponse so the two
    // definitions of this hallucination can't drift apart.
    private const string MenuPromptPattern = @"what\s+would\s+you\s+like\b[^\n?.]*\bto\s+(?:do|say)\b[^\n?.]*[?.]";

    // A "##"/"###"-prefixed line is a strong sign of hallucinated continuation in a full
    // story response too - but there it's expected (real section headers), which is why
    // this cutoff was deliberately never applied to Parse(). For this narrow, per-turn
    // response, "##"/"###" was assumed to have no legitimate use at all - confirmed in an
    // actual playthrough that assumption doesn't hold: the model markdown-styles its OWN
    // required "New Player Action:"/"New Clue Discovered:" headers with a "###" prefix
    // too (the prompt only asks for the bare phrase, no particular styling - Rule #1, the
    // model drifts on formatting regardless of what's asked). A blind "\n##" marker
    // mistook the model's own genuine "### New Player Action:" header for a hallucination
    // boundary and cut everything from there on - discarding the real action, the real
    // clue that followed it, and even the real "### End" marker, all of which were still
    // perfectly legitimate. Only treat a "#"-prefixed line as a real hallucination
    // boundary if it ISN'T one of the headers this response is actually supposed to
    // contain.
    private static readonly Regex FakeHeaderLine = new Regex(@"\n(#{2,})[ \t]*\**[ \t]*", RegexOptions.Multiline);

    private static int FindFakeHeaderCutoff(string raw)
    {
        foreach (Match m in FakeHeaderLine.Matches(raw))
        {
            int headingStart = m.Index + m.Length;
            int lineEnd = raw.IndexOf('\n', headingStart);
            if (lineEnd < 0) lineEnd = raw.Length;
            string headingLine = raw.Substring(headingStart, lineEnd - headingStart);

            bool isLegitimate =
                Regex.IsMatch(headingLine, "^" + OptionalLeadWord + ActionHeaderPattern, RegexOptions.IgnoreCase)
                || Regex.IsMatch(headingLine, "^" + OptionalLeadWord + ClueHeaderPattern, RegexOptions.IgnoreCase)
                || Regex.IsMatch(headingLine, @"^end\s*$", RegexOptions.IgnoreCase);

            if (!isLegitimate)
                return m.Index;
        }
        return -1;
    }

    // The position of the LAST legitimate action/clue header anywhere in the raw text -
    // used so an "### End" marker that appears before either of these still-needed
    // headers isn't mistaken for the genuine stop (see CutHallucinatedContinuation).
    private static int LastLegitimateHeaderIndex(string raw)
    {
        int lastIndex = -1;
        foreach (Match m in Regex.Matches(raw, HeaderLinePrefix + ActionHeaderPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
            lastIndex = Math.Max(lastIndex, m.Index);
        foreach (Match m in Regex.Matches(raw, HeaderLinePrefix + ClueHeaderPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
            lastIndex = Math.Max(lastIndex, m.Index);
        return lastIndex;
    }

    // The model sometimes keeps going past the intended answer, hallucinating further
    // content instead of stopping - a "---" divider, restated conversation markers
    // ("### Response:"/"### Prompt:"), a fake "##"-style heading, or a bare "End" line
    // (not always the literal "### End"). None of these should ever legitimately appear
    // inside a short, single-purpose answer, so treat the earliest one found as a hard
    // stop. Deliberately NOT used by the general-purpose Parse() - a full story response
    // legitimately uses "##"/"###" as real section headers, so this blanket cutoff would
    // wrongly truncate the story instead of just removing hallucinated filler; it's only
    // safe for narrow, single-purpose responses (a per-turn action result, or the Real
    // Murderer follow-up) that have no legitimate reason to contain any of these markers.
    private static string CutHallucinatedContinuation(string raw)
    {
        int cutoff = raw.Length;

        // These two are extremely specific, unambiguous signs of the model restarting a
        // fake multi-turn conversation - no genuine single-purpose response would ever
        // contain either, so they're trusted unconditionally, unlike the markers below.
        foreach (string marker in new[] { "### Response:", "### Prompt:" })
        {
            int idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cutoff)
                cutoff = idx;
        }

        int fakeHeaderCutoff = FindFakeHeaderCutoff(raw);
        if (fakeHeaderCutoff >= 0 && fakeHeaderCutoff < cutoff)
            cutoff = fakeHeaderCutoff;

        // Neither an "End" line nor a separator divider (below) is ever itself a
        // legitimate header - unlike FindFakeHeaderCutoff's "###" lines, there's no
        // "this IS one of our own headers" check possible for either. Instead, both are
        // guarded the same way: don't trust the FIRST occurrence blindly if real content
        // the parser still needs (an action/clue header) comes after it. Confirmed in an
        // actual playthrough for "End": the model wrote "### End" right after the
        // Result, before ever reaching "New Player Action:"/"New clue discovered:", then
        // kept going with a hallucinated restatement of its own instructions that
        // nonetheless contained the real action and clue afterward. Cutting at that
        // first, premature "End" discarded the real action and clue entirely - the "no
        // usable New Player Action" warning fired even though the raw response clearly
        // had one, just past where parsing had already stopped looking.
        int lastNeededHeaderIndex = LastLegitimateHeaderIndex(raw);
        foreach (Match endMatch in Regex.Matches(raw, @"^\s*#{0,3}\s*end\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            if (endMatch.Index < lastNeededHeaderIndex)
                continue;
            if (endMatch.Index < cutoff)
                cutoff = endMatch.Index;
            break;
        }

        // A standalone line made of nothing but repeated separator-like characters
        // (e.g. "---", "--/--"). Confirmed in an actual playthrough for the "tacked onto
        // the end of an otherwise-complete action, followed by a self-referential 'Your
        // response was successful!' status line" case this was originally added for -
        // but ALSO confirmed, separately, that the model sometimes inserts a bare "---"
        // as an innocuous stylistic paragraph break between the Result and its own
        // genuine "New Player Action:"/"New clue discovered:" sections, not as a sign of
        // hallucination at all. Blindly trusting the first one discarded that real
        // action and clue entirely, the same failure shape as the premature-"End" case
        // above - so this gets the identical guard: skip any divider before the last
        // real header still coming up, only trust one at or after that point.
        foreach (Match dividerMatch in Regex.Matches(raw, @"^\s*[-=~*/_]{2,}\s*$", RegexOptions.Multiline))
        {
            if (dividerMatch.Index < lastNeededHeaderIndex)
                continue;
            if (dividerMatch.Index < cutoff)
                cutoff = dividerMatch.Index;
            break;
        }

        return raw.Substring(0, cutoff).Trim();
    }

    // Confirmed in an actual playthrough: a per-turn response can hand back a
    // non-empty, non-blank NewAction that's still garbage - the model echoed a
    // paraphrase of its OWN prompt instructions instead of writing a real action, e.g.
    // '") and reveal a new clue about Mr. Vincent Reeves (if found) by writing "New clue
    // discovered:")."' - meta-commentary about the response format itself, landing in
    // the field a genuine detective action was supposed to occupy. Nothing in
    // CutHallucinatedContinuation catches this (it isn't a runaway continuation past a
    // real answer - the "answer" itself just IS this echo), and the empty-NewAction
    // check in LLMStoryClient can't catch it either, since the field isn't empty.
    // These specific phrases are drawn directly from our own prompt templates' meta-
    // instructions (the literal heading text, or the sentence that tells the model how
    // to use it) - genuine narrative action/result/clue content has no legitimate reason
    // to contain any of them, so a match here is a strong, checkable signal of this
    // exact failure mode. Diagnostic only (used by LLMStoryClient to decide whether to
    // log the raw response) - deliberately not used to silently discard/clamp the text,
    // since we don't yet have enough confirmed raw responses to know the right fallback.
    public static readonly Regex EchoedInstructionMarker = new Regex(
        @"new\s+(?:player'?s?\s+)?action\s*:|new\s+clue\s+discovered|decisive\s+proof|\bby\s+writing\b",
        RegexOptions.IgnoreCase);

    public static ActionResponse ParseActionResponse(string raw)
    {
        var result = new ActionResponse();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        raw = CutHallucinatedContinuation(raw.Replace("\r\n", "\n").Trim());

        // Confirmed in an actual playthrough: two of three branches fell back to the
        // generic "Investigate further." placeholder text (GameManager's FallbackNewAction)
        // after their first turn - the only way that happens is NewAction coming back
        // null, i.e. this heading not being found at all. The model varies this wording
        // ("New Action:", "Next Action:", "New Player's Action:") the same way it already
        // varies the "New clue discovered:" heading - match any of those instead of the
        // single rigid phrase.
        //
        // Both header searches require the phrase to start its own line (HeaderLinePrefix
        // allows optional leading "#"/"*" markdown, same as elsewhere in this file, but
        // nothing else before it). Confirmed in an actual playthrough this matters: the
        // model can hallucinate a restated, paraphrased copy of its own instructions
        // BEFORE the real header ("Now, provide a new clue about Ethan Vale, only if you
        // have one...") which itself contains the literal phrase "new clue" mid-sentence -
        // without line-anchoring, Regex.Match's first-occurrence behavior locked onto that
        // hallucinated mention instead of the real "### New clue discovered:" header
        // further down, and everything from the fake match onward (including the genuine
        // header and its real content) got folded into a garbled NewClue.
        Match actionMatch = Regex.Match(raw, HeaderLinePrefix + ActionHeaderPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // The model sometimes abandons the "New Player Action:" header entirely and
        // instead asks the player directly what to do next - "What would you like the
        // detective to do now?"/"What would you like to do now?" - followed by its own
        // numbered/bulleted menu of options. Confirmed in an actual playthrough: this used
        // to leave NewAction completely empty (no header for actionMatch to find at all,
        // so GameManager fell back to its generic "Investigate further." placeholder)
        // despite the raw response clearly offering a perfectly usable menu of real
        // actions - and separately, the question itself used to leak verbatim into the
        // displayed Result text, since it doesn't contain "players" (the only direct-
        // address signal HallucinatedMenuMarker checks for) when phrased as "the
        // detective" or with no addressee at all. Only used as a fallback when the real
        // header wasn't found - if a genuine "New Player Action:" header exists, trust it
        // over this, since a real action is always a stronger signal than this fallback.
        Match menuPromptMatch = actionMatch.Success
            ? Match.Empty
            : Regex.Match(raw, MenuPromptPattern, RegexOptions.IgnoreCase);
        Match effectiveActionMatch = menuPromptMatch.Success ? menuPromptMatch : actionMatch;

        // The model's phrasing for this header varies ("New clue discovered:", "New Clue
        // Found:", "New Clue Discovered?", bare "New Clue:", etc.) - match "new clue"
        // followed by anything up to whichever terminator it used (colon, period, or
        // question mark) rather than chasing each exact wording.
        Match clueMatch = Regex.Match(raw, HeaderLinePrefix + ClueHeaderPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

        int resultEnd = raw.Length;
        if (effectiveActionMatch.Success) resultEnd = Math.Min(resultEnd, effectiveActionMatch.Index);
        if (clueMatch.Success) resultEnd = Math.Min(resultEnd, clueMatch.Index);
        result.Result = TruncateResult(StripMarkers(TrimHallucinatedMenu(raw.Substring(0, resultEnd))));

        if (effectiveActionMatch.Success)
        {
            int start = effectiveActionMatch.Index + effectiveActionMatch.Length;
            int end = clueMatch.Success && clueMatch.Index > effectiveActionMatch.Index ? clueMatch.Index : raw.Length;
            string actionBlock = StripMarkers(raw.Substring(start, end - start));

            // The model was asked for exactly one action but sometimes replies with a
            // numbered/bulleted list anyway - take just the first item when that happens
            // (this is also exactly how a "what would you like to do?"-style menu gets
            // reduced to a single real action above). It also sometimes writes multiple
            // plain sentences with no bullets at all (e.g. "Investigate X... Continue the
            // investigation.") - cutting at the first genuine sentence end catches that
            // case too.
            List<string> items = ExtractListItems(actionBlock);
            result.NewAction = TruncateToFirstSentence(items.Count > 0 ? items[0] : actionBlock);
        }
        else
        {
            // Last resort: the model sometimes invents its own ad-hoc heading before the
            // options menu instead of either "New Player Action:" or "What would you like
            // to do" phrasing - e.g. "Investigative Option(s):". Confirmed in an actual
            // playthrough. Since the exact wording keeps varying every time, don't try to
            // recognize it at all - just look for "a list of options sitting at the end
            // of the response," which is the one part of the shape that actually has
            // been consistent across every confirmed variant so far. Only reached when
            // neither a real header nor the menu-prompt phrasing matched anything.
            int trailingListStart = FindTrailingListBlockStart(raw);
            if (trailingListStart >= 0)
            {
                List<string> items = ExtractListItems(StripMarkers(raw.Substring(trailingListStart)));
                if (items.Count > 0)
                    result.NewAction = TruncateToFirstSentence(items[0]);
            }
        }

        if (clueMatch.Success)
        {
            int start = clueMatch.Index + clueMatch.Length;
            int end = effectiveActionMatch.Success && effectiveActionMatch.Index > clueMatch.Index ? effectiveActionMatch.Index : raw.Length;
            string clueBlock = CleanCluePreamble(StripMarkers(raw.Substring(start, end - start)));

            // The model was asked for a single clue but sometimes lists two or three under
            // the same "New clue discovered:" heading (numbered/bulleted) - a numbered list
            // also defeats GameManager's sentence-boundary cut (a digit like "2." isn't a
            // capital letter, so it doesn't look like a sentence start), letting every item
            // through as one crammed clue. Take just the first item here, same as NewAction.
            List<string> clueItems = ExtractListItems(clueBlock);
            string clueText = clueItems.Count > 0 ? clueItems[0] : clueBlock;

            // Check IsNoClue against the FIRST SENTENCE specifically, not the whole
            // (possibly multi-sentence) clueText - confirmed in an actual playthrough: the
            // model sometimes answers "None." as a complete first sentence, then keeps
            // rambling with unrelated commentary afterward ("None. The result stands as
            // described above."). That full blob doesn't equal "None" exactly, so it used
            // to pass this check - but GameManager.TruncateClue downstream still cuts to
            // just the first sentence for display, isolating "None." and showing it as if
            // it were a real clue. Checking the first sentence here catches it up front.
            if (!IsNoClue(TruncateToFirstSentence(clueText)))
                result.NewClue = clueText;
        }

        // Sometimes the model doesn't clearly separate "what happened" from "the new
        // clue" and effectively only writes the latter - if the result ended up empty,
        // show the clue text there too rather than leaving the output blank.
        if (string.IsNullOrWhiteSpace(result.Result) && !string.IsNullOrEmpty(result.NewClue))
            result.Result = result.NewClue;

        return result;
    }

    public static List<string> ExtractClueList(string cluesText)
    {
        // The model is asked for "at least 3 clues" but sometimes runs out of genuine
        // ideas and fills a slot with a "None" placeholder instead (e.g. "2. : None."
        // when it left the item's label blank too) - filter those out here the same way
        // a per-turn "no new clue" answer already is, rather than displaying a bare
        // "- None." bullet as if it were a real clue.
        List<string> items = ExtractListItems(cluesText);
        items.RemoveAll(IsNoClue);
        if (items.Count > 0)
            return items;

        // Some responses list each clue under its own "Clue #1:" sub-header instead of
        // a single "Clues:" heading with a bulleted list underneath - fall back to that.
        items = ExtractSubHeaderItems(cluesText, "clue");
        items.RemoveAll(IsNoClue);
        if (items.Count > 0)
            return items;

        // No bullets, numbers, or sub-headers at all - if the model just wrote one plain
        // sentence instead of a list, treat that whole block as a single clue rather
        // than silently dropping it.
        if (!string.IsNullOrWhiteSpace(cluesText) && Regex.IsMatch(cluesText, @"[A-Za-z0-9]") && !IsNoClue(cluesText))
            items.Add(cluesText.Trim());

        return items;
    }

    private static List<string> ExtractSubHeaderItems(string content, string headerWord)
    {
        var items = new List<string>();
        if (string.IsNullOrEmpty(content))
            return items;

        MatchCollection matches = Regex.Matches(content, headerWord + @"\s*#?\s*\d+\s*:", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
            return items;

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index + matches[i].Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            string item = StripMarkers(content.Substring(start, end - start));
            if (Regex.IsMatch(item, @"[A-Za-z0-9]"))
                items.Add(item);
        }
        return items;
    }

    public static List<string> ExtractSuspectNames(string suspectsText)
    {
        List<string> lines = ExtractSuspectEntries(suspectsText);
        if (lines.Count == 0)
            // Some responses list each suspect under its own "Suspect #1:" sub-header
            // instead of a bulleted list - fall back to that.
            lines = ExtractSubHeaderItems(suspectsText, "suspect");

        var names = new List<string>();
        foreach (string item in lines)
        {
            string name = ExtractName(item);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }
        return names;
    }

    private static List<string> ExtractSuspectEntries(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new List<string>();

        // Standard case: suspects themselves are bulleted/numbered.
        List<string> bulletItems = ExtractListItems(content).FindAll(item => !LooksLikeAlibiOrContradiction(item));
        if (bulletItems.Count > 0)
            return bulletItems;

        // Some responses instead write each suspect's name+description as a plain
        // paragraph, only bulleting "- Alibi:"/"- Contradiction:" sub-details
        // underneath rather than the suspects themselves. Treat each paragraph
        // (blank-line-separated block) that isn't itself one of those sub-details as
        // one suspect entry.
        var entries = new List<string>();
        foreach (string paragraph in Regex.Split(content.Trim(), @"\n\s*\n"))
        {
            string firstLine = Regex.Replace(paragraph.Trim().Split('\n')[0], @"^[-*•]\s*", "").Trim();
            if (!string.IsNullOrEmpty(firstLine)
                && !LooksLikeAlibiOrContradiction(firstLine)
                && Regex.IsMatch(firstLine, @"[A-Za-z]"))
                entries.Add(firstLine);
        }
        return entries;
    }

    private static bool LooksLikeAlibiOrContradiction(string line)
    {
        string trimmed = Regex.Replace(line, @"^[-*•]\s*", "");
        return Regex.IsMatch(trimmed, @"^(alibi|contradiction)\s*:", RegexOptions.IgnoreCase);
    }

    // Matches the leading run of Capitalized words at the start of a suspect line with
    // no bold markup and no comma (e.g. "Charlie claimed he heard footsteps..." instead
    // of "Charlie, a café owner who...") - without this, ExtractName's comma fallback
    // used to return the entire sentence as the "name," which would show up as garbled
    // text on the guess-screen buttons.
    private static readonly Regex CapitalizedNameRun = new Regex(@"^([A-Z][a-zA-Z'\-]*(?:\s+[A-Z][a-zA-Z'\-]*)*)");

    // A title followed by a capitalized word/two, anywhere in the line - e.g. "Officer
    // Marcus", "Mr. Traven". Deliberately searched across the whole line, not just the
    // start, since the model sometimes leads a suspect line with an evidentiary clause
    // ("The coin was found near Brennan's hand, suggesting Officer Marcus was
    // involved...") rather than the suspect's name.
    private static readonly Regex TitledNameAnywhere = new Regex(
        @"\b(?:Mr|Mrs|Ms|Miss|Dr|Officer|Detective|Sergeant|Sgt|Captain|Capt|Professor|Prof)\.?\s+[A-Z][a-zA-Z'\-]*(?:\s+[A-Z][a-zA-Z'\-]*)?");

    // Ordinary sentence-initial capitalized words ("The", "When", "After", etc.) that
    // look like a name to a naive capitalized-word check but never actually are one.
    private static readonly HashSet<string> NonNameWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "The", "A", "An", "This", "That", "These", "Those", "When", "After", "Before",
        "During", "While", "Since", "If", "However", "Meanwhile", "Later", "Then",
    };

    // A bare title abbreviation with nothing else - what CapitalizedNameRun wrongly
    // returns on its own when a suspect line leads with an abbreviated title, e.g. "Ms.
    // Ruth Pearson is a regular customer..." - the period right after "Ms" isn't in
    // CapitalizedNameRun's allowed character class, so its capitalized-word-run stops
    // dead at "Ms" instead of continuing on to "Ruth Pearson" just past the period.
    // Confirmed via direct testing against real suspect text from an actual playthrough:
    // this silently produced "Ms"/"Mr" alone as a suspect's "name" (shown as a bare,
    // meaningless guess-screen button) whenever the line had no usable comma-led name
    // either (the paragraph-fallback path in ExtractSuspectEntries, which leads with
    // plain prose rather than "Name, description"). Titles with no trailing period
    // ("Miss", "Officer") aren't affected - only abbreviated ones are.
    private static readonly HashSet<string> TitleAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Mr", "Mrs", "Ms", "Miss", "Dr", "Prof", "Sgt", "Capt", "Lt", "Col", "Gen", "Rev", "Fr", "St", "Jr", "Sr",
    };

    // A real name is a short run of capitalized words with no lowercase filler words
    // mixed in - used to reject a pre-comma segment that's actually a full descriptive
    // clause rather than a name (see ExtractName).
    private static bool LooksLikeName(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 40)
            return false;

        string[] words = candidate.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        foreach (string word in words)
            if (!char.IsUpper(word[0]))
                return false;

        return true;
    }

    private static string ExtractName(string suspectLine)
    {
        Match bold = Regex.Match(suspectLine, @"\*\*(.+?)\*\*");
        if (bold.Success)
            return bold.Groups[1].Value.Trim();

        // The standard "Name, description" case - but only trust the pre-comma segment
        // if it actually looks like a name. Confirmed in an actual playthrough: the model
        // sometimes leads with an evidentiary clause instead ("The coin was found near
        // Brennan's hand, suggesting Officer Marcus..."), and blindly taking everything
        // before the first comma used to return that whole clause as the "name," which
        // then displayed as a garbled sentence on the guess-screen buttons.
        int commaIndex = suspectLine.IndexOf(',');
        if (commaIndex >= 0)
        {
            string beforeComma = suspectLine.Substring(0, commaIndex).Trim();
            if (LooksLikeName(beforeComma))
                return beforeComma;
        }

        Match capsRun = CapitalizedNameRun.Match(suspectLine);
        if (capsRun.Success)
        {
            string candidate = capsRun.Groups[1].Value.Trim();
            bool isSingleWord = candidate.IndexOf(' ') < 0;
            // A single common sentence-starter ("The coin...") isn't a name, and neither
            // is a bare title abbreviation truncated at its own period ("Ms", "Mr") -
            // fall through to the next tier (TitledNameAnywhere) instead of returning
            // either, since that tier can actually see past the period to the real name.
            if (!(isSingleWord && (NonNameWords.Contains(candidate) || TitleAbbreviations.Contains(candidate))))
                return candidate;
        }

        Match titled = TitledNameAnywhere.Match(suspectLine);
        if (titled.Success)
            return titled.Value.Trim();

        // Last resort: the first standalone capitalized word past the very start of the
        // line that isn't a common sentence-starter - a weaker signal than a title, but
        // still better than showing the entire garbled sentence as a "name."
        foreach (Match word in Regex.Matches(suspectLine, @"\b[A-Z][a-z']+\b"))
        {
            if (word.Index == 0 || NonNameWords.Contains(word.Value))
                continue;
            return word.Value;
        }

        return suspectLine.Trim();
    }

    // Titles vary between where a suspect is first listed and where the murderer is
    // later revealed (e.g. "Miss Rissa" in the Suspects list vs "Ms. Rissa" in the
    // Real Murderer reveal - same person, confirmed in a real raw response) - strip
    // them before comparing so this difference alone doesn't break the match.
    private static readonly Regex TitlePrefix = new Regex(@"^(?:Mr|Mrs|Ms|Miss|Dr|Prof)\.?\s+", RegexOptions.IgnoreCase);

    // Fuzzy-matches each suspect name against the Real Murderer reveal text and returns
    // the index of whichever suspect scores highest (most of their name-words found in
    // the reveal). Returns -1 - rather than a guess - when nobody scores above zero, so
    // a garbled or missing Real Murderer field can never silently decide a win or loss.
    public static int DetermineMurdererIndex(List<string> suspectNames, string realMurdererText)
    {
        if (suspectNames == null || suspectNames.Count == 0 || string.IsNullOrWhiteSpace(realMurdererText))
            return -1;

        int bestIndex = -1;
        int bestScore = 0;

        for (int i = 0; i < suspectNames.Count; i++)
        {
            int score = ScoreSuspectAgainstMurdererText(suspectNames[i], realMurdererText);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestScore > 0 ? bestIndex : -1;
    }

    private static int ScoreSuspectAgainstMurdererText(string suspectName, string realMurdererText)
    {
        if (string.IsNullOrWhiteSpace(suspectName))
            return 0;

        string bareName = TitlePrefix.Replace(suspectName.Trim(), "");
        string[] words = bareName.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        int score = 0;
        foreach (string word in words)
        {
            if (word.Length < 2)
                continue;
            if (Regex.IsMatch(realMurdererText, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase))
                score++;
        }
        return score;
    }

    // The model sometimes answers its own "New clue discovered:" heading as if it were
    // a yes/no question - e.g. "New clue discovered: (Yes/No) Yes New Clue: The shop
    // door had been unlocked..." - stacking a restated "(Yes/No)" marker, an affirmation,
    // and a second "New Clue:" label in front of the actual clue text, none of which a
    // single strip pass catches (StripLeadingAffirmation alone leaves the "(Yes/No)"
    // untouched since it doesn't start with "yes"). Loop stripping each known preamble
    // piece until nothing more matches, in whatever order the model happened to stack
    // them in - bounded so a pathological input can't spin forever.
    private static string CleanCluePreamble(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            string before = text;

            // A restated yes/no marker, with or without parens - e.g. "(Yes/No)".
            text = Regex.Replace(text, @"^\s*\(?\s*yes\s*/\s*no\s*\)?\s*[:.]?\s*", "", RegexOptions.IgnoreCase);
            // E.g. "New Clue Discovered? Yes - Lila may have..." - the actual clue starts
            // after the "Yes" confirmation, not with it.
            text = Regex.Replace(text, @"^\s*yes\s*[,\-–—]?\s*", "", RegexOptions.IgnoreCase);
            // A restated "New clue discovered:"-style header, in case the model
            // re-announces it a second time after answering its own yes/no preamble.
            text = Regex.Replace(text, @"^\s*new\s+clue[^:.?\n]*[:.?]\s*", "", RegexOptions.IgnoreCase);

            if (text == before)
                break;
        }
        return text;
    }

    private static bool IsNoClue(string clueText)
    {
        if (string.IsNullOrWhiteSpace(clueText))
            return true;

        // The model's "no clue" answer varies ("None", "None.", "No new clue", etc.) -
        // normalize away trailing punctuation before comparing instead of requiring an
        // exact match.
        string normalized = clueText.Trim().TrimEnd('.', '!').Trim();
        // A numbered clue item can leave a bare leading colon/dash behind when the
        // model writes an empty sub-label before "None" (e.g. "2. : None." for a clue
        // it couldn't come up with) - strip that before comparing too.
        normalized = Regex.Replace(normalized, @"^[:\-–—]\s*", "").Trim();
        return normalized.Equals("None", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("No new clue", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    }

    // Raised 350 -> 450 -> 1200 across two playthroughs. First raise: a legitimate
    // multi-sentence result ("...The turn-down had a post-it note attached that said:
    // 'See Miss Janine tomorrow.'") got clamped off before that last sentence, silently
    // dropping information the player needed. Second raise: confirmed the model is
    // consistently capable of much richer, multi-paragraph results than either limit
    // allowed, and the Game scene's output area is no longer a fixed-height single-line
    // display (see StoryDisplay-style ScrollRect added to GameManager) - so this no
    // longer needs to double as a UI-space constraint, only a sanity backstop against
    // truly runaway generation. The real guard against hallucinated tails (a fabricated
    // "Based on these findings:" bullet list, or the model offering the player its own
    // "What would you like to do now?" menu) is TrimHallucinatedMenu above, applied
    // before this length clamp ever gets a chance to run.
    private const int MaxResultLength = 1200;

    private static string TruncateResult(string result)
    {
        // Cut at the LAST complete sentence within the length limit, not just when the
        // text exceeds it - using the same abbreviation-aware boundary check so a title
        // like "Mr."/"Mrs." isn't mistaken for the sentence end. A genuine short result
        // already ends on a real sentence, so this changes nothing for the normal case -
        // but it also cleans up the dangling non-sentence fragment TrimHallucinatedMenu
        // can leave behind right where it cut (e.g. a heading like "Based on these
        // findings:" with nothing after it, once the bulleted list following it is gone).
        string truncated = result.Length <= MaxResultLength ? result : result.Substring(0, MaxResultLength);

        MatchCollection sentenceEnds = SentenceEndRegex.Matches(truncated);
        if (sentenceEnds.Count > 0)
        {
            Match last = sentenceEnds[sentenceEnds.Count - 1];
            return truncated.Substring(0, last.Index + last.Length).Trim();
        }

        // No sentence-ending punctuation found at all - if it's still within budget,
        // there's nothing to gain by chopping mid-word, so return it as-is. Only past
        // the length cap do we force a hard, ellipsis-marked cut.
        if (result.Length <= MaxResultLength)
            return truncated;

        int lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 0)
            truncated = truncated.Substring(0, lastSpace);
        return truncated.TrimEnd(',', ';', ':', ' ') + "...";
    }

    private static string StripMarkers(string content)
    {
        // Strip stray "#"/"##"/"###"/"####" style section-marker lines the model sometimes
        // injects between paragraphs - whether bare or with leftover echoed prompt text
        // trailing after the hashes (e.g. "### At least 5").
        content = Regex.Replace(content, @"^\s*#+.*$", "", RegexOptions.Multiline);

        // The model occasionally wraps part of its answer in a markdown code fence
        // (```) - confirmed in an actual playthrough, sitting on its own line right
        // after a hallucinated question and before a hallucinated multiple-choice list.
        // Never legitimate content in any field here, same treatment as the "#" lines
        // just above.
        content = Regex.Replace(content, @"^\s*```\s*$", "", RegexOptions.Multiline);

        // It also sometimes closes a section with a bare "End" line (no "#" prefix) -
        // strip that too, but only as a standalone line so "end" appearing naturally
        // inside a sentence isn't affected.
        content = Regex.Replace(content, @"^\s*end\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        // Echoed fragment from our own prompt's "5. At least 5 Clues" list item,
        // occasionally leaking into the output on its own line.
        content = Regex.Replace(content, @"^\s*at\s+least\s+\d+\s*$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        // The model occasionally leaks literal internal-template-looking placeholder
        // tokens like "{LIST_RESULT}: {GENERATE_ACTION}" - strip any line built out of
        // one or more "{ALL_CAPS}" style tokens.
        content = Regex.Replace(content, @"^\s*(\{[A-Z_]+\}\s*:?\s*)+$", "", RegexOptions.Multiline);

        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        // TMP doesn't render markdown, so raw "**bold**" syntax just shows as literal
        // asterisks - strip it everywhere rather than only trimming the ends.
        content = content.Replace("**", "");
        content = content.Trim();

        // The model sometimes wraps its entire answer in literal double quotes (e.g. an
        // action rendered as `"Review closed-circuit feeds..."`) - a closing quote right
        // after the final period also breaks TruncateToFirstSentence's sentence-boundary
        // lookahead (a quote isn't whitespace), so the quotes themselves used to survive
        // into the displayed button/clue text. Only strip a genuine leading+trailing
        // pair - an embedded quotation that doesn't wrap the whole block is left alone.
        if (content.Length >= 2 && content[0] == '"' && content[content.Length - 1] == '"')
            content = content.Substring(1, content.Length - 2).Trim();

        return content;
    }

    private static List<string> ExtractListItems(string content)
    {
        var items = new List<string>();
        if (string.IsNullOrEmpty(content))
            return items;

        MatchCollection matches = Regex.Matches(content, @"^\s*(?:\d+[\.\)]|[-*•])\s*(.+)$", RegexOptions.Multiline);
        foreach (Match m in matches)
        {
            string item = m.Groups[1].Value.Trim();
            // Skip degenerate matches like a lone "*" or "-" left over from a malformed
            // bullet with no real content after it.
            if (Regex.IsMatch(item, @"[A-Za-z0-9]"))
                items.Add(item);
        }
        return items;
    }

    // Finds the start of the LAST run of consecutive numbered/bulleted list-item lines
    // in the raw text - used by ParseActionResponse as a final fallback for recovering
    // NewAction when neither a real action header nor the "what would you like to do"
    // phrasing matched anything. Walks backward from the final list-item line, only
    // extending the block backward while consecutive items are separated by nothing but
    // blank lines - a paragraph of unrelated prose in between means an earlier match
    // belongs to a different, earlier list, not this trailing one.
    private static int FindTrailingListBlockStart(string raw)
    {
        MatchCollection matches = Regex.Matches(raw, @"^[ \t]*(?:\d+[.\)]|[-*•])[ \t]*\S", RegexOptions.Multiline);
        if (matches.Count == 0)
            return -1;

        int blockStart = matches[matches.Count - 1].Index;
        for (int i = matches.Count - 2; i >= 0; i--)
        {
            int itemLineEnd = raw.IndexOf('\n', matches[i].Index);
            if (itemLineEnd < 0) itemLineEnd = raw.Length;
            string gap = raw.Substring(itemLineEnd, matches[i + 1].Index - itemLineEnd);
            if (string.IsNullOrWhiteSpace(gap))
                blockStart = matches[i].Index;
            else
                break;
        }
        return blockStart;
    }

    private static string[] ExtractNumberedLines(string content)
    {
        var actions = new string[3];
        List<string> items = ExtractListItems(content);
        for (int i = 0; i < items.Count && i < 3; i++)
            actions[i] = TruncateToFirstSentence(items[i]);
        return actions;
    }

    // Matches a sentence-ending ./!/?, optionally followed by a single closing quote
    // character (the model sometimes puts the punctuation *inside* a quoted phrase that
    // ends the sentence, e.g. "...read 'This will tell the truth.'" - without allowing
    // for that trailing quote, this found no sentence end at all for the whole clue,
    // since a quote character isn't whitespace and isn't "end of string" either. Confirmed
    // in an actual playthrough: this silently dropped an entire clue via TruncateClue
    // returning null). Only matches when followed by whitespace+capital letter or the end
    // of the string, and NOT immediately preceded by a common title abbreviation (whose
    // following word is capitalized too, e.g. "Mrs. Marlowe" - the naive
    // followed-by-capital check alone would wrongly treat "Mrs." itself as the end).
    // Public: shared with GameManager.cs's clue display truncation rather than duplicated,
    // so the two can no longer drift out of sync with each other.
    public static readonly Regex SentenceEndRegex = new Regex(
        @"(?<!\b(?:Mr|Mrs|Ms|Dr|Prof|St|Jr|Sr|Capt|Lt|Col|Gen|Rev|Sgt|Fr|Hon))[.!?][""'’”]?(?=\s+[A-Z]|\s*$)",
        RegexOptions.IgnoreCase);

    private static string TruncateToFirstSentence(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        Match m = SentenceEndRegex.Match(text);
        return m.Success ? text.Substring(0, m.Index + m.Length).Trim() : text.Trim();
    }

    // Matches one or more trailing lines that are nothing but a bare list marker - e.g.
    // the model abandons a numbered list after only 2 of the requested 3 suspects and
    // leaves a dangling "3." with no name/description before the next heading starts.
    // A marker's period is otherwise indistinguishable from a real sentence end when
    // it's the last thing in the block (SentenceEndRegex's end-of-string branch would
    // happily treat "...\n3." as ending there), so this has to run first.
    private static readonly Regex TrailingEmptyListMarkers = new Regex(
        @"(\n[ \t]*(?:\d+[\.\)]|[-*•])[ \t]*)+$");

    private static string TrimTrailingJunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        // Trim first so "$" reliably anchors to the true end - the header-boundary cut
        // in Parse() often leaves one or more blank lines after the last real content.
        content = TrailingEmptyListMarkers.Replace(content.TrimEnd(), "");
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        // Keep everything through the LAST genuine sentence in the block (not just the
        // first, since these sections are meant to be full paragraphs) and discard
        // anything after it - whatever that trailing fragment says.
        MatchCollection matches = SentenceEndRegex.Matches(content);
        if (matches.Count == 0)
            return content.Trim();

        Match last = matches[matches.Count - 1];
        return content.Substring(0, last.Index + last.Length).Trim();
    }
}
