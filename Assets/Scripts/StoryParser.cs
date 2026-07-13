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
    // ("What really happened?"), also required to stand alone. See Parse() for why an
    // unmarked/unrecognized heading additionally needs to be standalone before it's
    // trusted as a real boundary - this regex only finds candidates.
    private static readonly Regex CandidateHeadingLine = new Regex(
        @"^[ \t]*(?:#{1,6}[ \t]*\**|\*\*)([A-Za-z][A-Za-z0-9 '\-#]{0,40}?)\**[ \t]*:" +
        @"|^[ \t]*([A-Za-z][A-Za-z0-9 '\-#]{0,40}?)[ \t]*:[ \t]*$" +
        @"|^[ \t]*(what\s+really\s+happened)\s*\??[ \t]*$",
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

    // CandidateHeadingLine has three alternatives (marked-up, bare-standalone, and the
    // "what really happened" paraphrase), which land in different capture groups.
    private static string GetHeadingText(Match headingLine)
    {
        if (headingLine.Groups[1].Success) return headingLine.Groups[1].Value;
        if (headingLine.Groups[2].Success) return headingLine.Groups[2].Value;
        return headingLine.Groups[3].Value;
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
            bool isKnown = ClassifyHeading(GetHeadingText(candidate)) != null;
            bool standalone = RestOfLineIsBlank(raw, candidate.Index + candidate.Length);
            if (isKnown || standalone)
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
            string content = StripMarkers(raw.Substring(start, end - start));

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
        result.RealMurderer = TrimMurdererHallucination(TrimTrailingJunk(CombineParts(sectionParts, "RealMurderer")));
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

        return TrimMurdererHallucination(result);
    }

    // A different, subtler hallucination than CutHallucinatedContinuation catches: after
    // giving the real motive/proof, the model sometimes keeps going by addressing the
    // player directly - e.g. "What would you like the player to do next?" followed by a
    // fabricated numbered list of new actions. Confirmed in an actual playthrough; none
    // of CutHallucinatedContinuation's markers (###, ---, ##) appear in this pattern, so
    // it survived all the way into the reveal shown to the player. Neither a numbered
    // list nor a direct address to "the player"/"players" should ever legitimately
    // appear in this field, so either is treated as a hard stop - cut at the start of
    // whichever offending line comes first, not just the trigger word, so the fragment
    // "What would you like the" doesn't survive as trailing junk.
    private static readonly Regex MurdererHallucinationMarker = new Regex(
        @"^\s*\d+[.\)]\s|^.*\bplayers?\b.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string TrimMurdererHallucination(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        Match m = MurdererHallucinationMarker.Match(text);
        return m.Success ? text.Substring(0, m.Index).Trim() : text;
    }

    // The model sometimes keeps going past the intended answer, hallucinating further
    // content instead of stopping - a "---" divider, restated conversation markers
    // ("### Response:"/"### Prompt:"), a "##"-style heading, or a bare "End" line (not
    // always the literal "### End"). None of these should ever legitimately appear
    // inside a short, single-purpose answer, so treat the earliest one found as a hard
    // stop. Deliberately NOT used by the general-purpose Parse() - a full story response
    // legitimately uses "##"/"###" as real section headers, so this blanket cutoff would
    // wrongly truncate the story instead of just removing hallucinated filler; it's only
    // safe for narrow, single-purpose responses (a per-turn action result, or the Real
    // Murderer follow-up) that have no legitimate reason to contain any of these markers.
    private static string CutHallucinatedContinuation(string raw)
    {
        int cutoff = raw.Length;
        foreach (string marker in new[] { "### Response:", "### Prompt:", "\n---", "\n##" })
        {
            int idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cutoff)
                cutoff = idx;
        }

        // Only match a *standalone* "End" line (not "end" appearing mid-sentence in
        // normal prose) to avoid false positives.
        Match endMatch = Regex.Match(raw, @"^\s*#{0,3}\s*end\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (endMatch.Success && endMatch.Index < cutoff)
            cutoff = endMatch.Index;

        // A standalone line made of nothing but repeated separator-like characters
        // (e.g. "--/--") - confirmed in an actual playthrough: the model tacked one of
        // these onto the end of an otherwise-complete action, followed by a
        // self-referential "Your response was successful!" status line. Neither is a
        // fixed enough string to add to the marker list above, but this shape never
        // legitimately appears in a real action/clue/result either way. This also
        // mattered beyond just trailing junk: the divider defeated the sentence-boundary
        // regex used elsewhere (a "." followed by "\n--/--" isn't followed by
        // whitespace+capital or end-of-string), so without this cutoff the *entire*
        // hallucinated tail used to survive as if it were part of the real action.
        Match dividerMatch = Regex.Match(raw, @"^\s*[-=~*/_]{2,}\s*$", RegexOptions.Multiline);
        if (dividerMatch.Success && dividerMatch.Index < cutoff)
            cutoff = dividerMatch.Index;

        return raw.Substring(0, cutoff).Trim();
    }

    public static ActionResponse ParseActionResponse(string raw)
    {
        var result = new ActionResponse();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        raw = CutHallucinatedContinuation(raw.Replace("\r\n", "\n").Trim());

        Match actionMatch = Regex.Match(raw, @"new\s+player\s+action\s*:", RegexOptions.IgnoreCase);

        // The model's phrasing for this header varies ("New clue discovered:", "New Clue
        // Found:", "New Clue Discovered?", bare "New Clue:", etc.) - match "new clue"
        // followed by anything up to whichever terminator it used (colon, period, or
        // question mark) rather than chasing each exact wording.
        Match clueMatch = Regex.Match(raw, @"new\s+clue[^:.?\n]*[:.?]", RegexOptions.IgnoreCase);

        int resultEnd = raw.Length;
        if (actionMatch.Success) resultEnd = Math.Min(resultEnd, actionMatch.Index);
        if (clueMatch.Success) resultEnd = Math.Min(resultEnd, clueMatch.Index);
        result.Result = TruncateResult(StripMarkers(raw.Substring(0, resultEnd)));

        if (actionMatch.Success)
        {
            int start = actionMatch.Index + actionMatch.Length;
            int end = clueMatch.Success && clueMatch.Index > actionMatch.Index ? clueMatch.Index : raw.Length;
            string actionBlock = StripMarkers(raw.Substring(start, end - start));

            // The model was asked for exactly one action but sometimes replies with a
            // numbered/bulleted list anyway - take just the first item when that happens.
            // It also sometimes writes multiple plain sentences with no bullets at all
            // (e.g. "Investigate X... Continue the investigation.") - cutting at the
            // first genuine sentence end catches that case too.
            List<string> items = ExtractListItems(actionBlock);
            result.NewAction = TruncateToFirstSentence(items.Count > 0 ? items[0] : actionBlock);
        }

        if (clueMatch.Success)
        {
            int start = clueMatch.Index + clueMatch.Length;
            int end = actionMatch.Success && actionMatch.Index > clueMatch.Index ? actionMatch.Index : raw.Length;
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
            // A single common sentence-starter ("The coin...") isn't a name - fall
            // through to the next tier instead of returning it.
            if (!(candidate.IndexOf(' ') < 0 && NonNameWords.Contains(candidate)))
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

    private const int MaxResultLength = 350;

    private static string TruncateResult(string result)
    {
        if (result.Length <= MaxResultLength)
            return result;

        // The model is asked to stay under 300 characters, but that's a request, not
        // a guarantee - clamp hard so a rambling response can't break the UI. Prefer
        // cutting at the last complete sentence within the limit (Result is allowed up
        // to 2 sentences, unlike actions/clues) so it doesn't trail off mid-word - using
        // the same abbreviation-aware boundary check so a title like "Mr."/"Mrs." isn't
        // mistaken for the sentence end.
        string truncated = result.Substring(0, MaxResultLength);

        MatchCollection sentenceEnds = SentenceEndRegex.Matches(truncated);
        if (sentenceEnds.Count > 0)
        {
            Match last = sentenceEnds[sentenceEnds.Count - 1];
            return truncated.Substring(0, last.Index + last.Length).Trim();
        }

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
