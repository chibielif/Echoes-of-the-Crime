using System;
using System.Collections.Generic;
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
    private static readonly (string Key, string Pattern)[] Headers =
    {
        ("CrimeSummary", @"crime\s+summary\s*:"),
        ("Victim", @"victim\s*:"),
        ("CrimeScene", @"crime\s+scene\s*:"),
        ("Suspects", @"(three\s+)?suspects\s*:"),
        ("Clues", @"clues?(\s*#?\s*\d+)?\s*:"),
        ("RealMurderer", @"real\s+murderer\s*:"),
        ("InitialActions", @"initial\s+player\s+actions\s*:"),
    };

    public static ParsedStory Parse(string raw)
    {
        var result = new ParsedStory();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        raw = raw.Replace("\r\n", "\n").Trim();

        var matches = new List<(string Key, Match Match)>();
        foreach (var (key, pattern) in Headers)
        {
            Match m = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
                matches.Add((key, m));
        }
        matches.Sort((a, b) => a.Match.Index.CompareTo(b.Match.Index));

        if (matches.Count == 0)
            return result;

        result.Title = StripMarkers(raw.Substring(0, matches[0].Match.Index)).Trim('*', '#', ' ', '\n');

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Match.Index + matches[i].Match.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Match.Index : raw.Length;
            string content = StripMarkers(raw.Substring(start, end - start));

            switch (matches[i].Key)
            {
                case "CrimeSummary": result.CrimeSummary = content; break;
                case "Victim": result.Victim = content; break;
                case "CrimeScene": result.CrimeScene = content; break;
                case "Suspects": result.Suspects = content; break;
                case "Clues": result.Clues = content; break;
                case "RealMurderer": result.RealMurderer = content; break;
                case "InitialActions": result.InitialActions = ExtractNumberedLines(content); break;
            }
        }

        // The model doesn't always emit the Crime Summary/Victim/Crime Scene headers - when it
        // skips them, the intro prose ends up in Title instead of being shown anywhere.
        if (!string.IsNullOrEmpty(result.Title)
            && string.IsNullOrEmpty(result.CrimeSummary)
            && string.IsNullOrEmpty(result.Victim)
            && string.IsNullOrEmpty(result.CrimeScene))
        {
            result.CrimeSummary = result.Title;
        }

        return result;
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

    public static ActionResponse ParseActionResponse(string raw)
    {
        var result = new ActionResponse();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        raw = raw.Replace("\r\n", "\n").Trim();

        // The model sometimes keeps going past the current turn, hallucinating further
        // rounds (or even a fake follow-up conversation) instead of stopping at "### End".
        // None of these markers should ever legitimately appear inside its actual answer,
        // so treat the earliest one found as a hard stop.
        int cutoff = raw.Length;
        foreach (string marker in new[] { "### Response:", "### Prompt:", "\n---", "\n##" })
        {
            int idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < cutoff)
                cutoff = idx;
        }

        // The model doesn't always write the literal "### End" - sometimes it's just a
        // bare "End" on its own line. Only match a *standalone* line (not "end" appearing
        // mid-sentence in normal prose) to avoid false positives.
        Match endMatch = Regex.Match(raw, @"^\s*#{0,3}\s*end\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (endMatch.Success && endMatch.Index < cutoff)
            cutoff = endMatch.Index;

        raw = raw.Substring(0, cutoff).Trim();

        Match actionMatch = Regex.Match(raw, @"new\s+player\s+action\s*:", RegexOptions.IgnoreCase);
        Match clueMatch = Regex.Match(raw, @"new\s+clue\s+(discovered|found)\s*:", RegexOptions.IgnoreCase);

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
            List<string> items = ExtractListItems(actionBlock);
            result.NewAction = items.Count > 0 ? items[0] : actionBlock;
        }

        if (clueMatch.Success)
        {
            int start = clueMatch.Index + clueMatch.Length;
            int end = actionMatch.Success && actionMatch.Index > clueMatch.Index ? actionMatch.Index : raw.Length;
            string clueText = StripMarkers(raw.Substring(start, end - start));
            if (!IsNoClue(clueText))
                result.NewClue = clueText;
        }

        return result;
    }

    public static List<string> ExtractClueList(string cluesText)
    {
        List<string> items = ExtractListItems(cluesText);
        if (items.Count > 0)
            return items;

        // Some responses list each clue under its own "Clue #1:" sub-header instead of
        // a single "Clues:" heading with a bulleted list underneath - fall back to that.
        items = ExtractSubHeaderItems(cluesText, "clue");
        if (items.Count > 0)
            return items;

        // No bullets, numbers, or sub-headers at all - if the model just wrote one plain
        // sentence instead of a list, treat that whole block as a single clue rather
        // than silently dropping it.
        if (!string.IsNullOrWhiteSpace(cluesText) && Regex.IsMatch(cluesText, @"[A-Za-z0-9]"))
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
        List<string> lines = ExtractListItems(suspectsText);
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

    private static string ExtractName(string suspectLine)
    {
        Match bold = Regex.Match(suspectLine, @"\*\*(.+?)\*\*");
        if (bold.Success)
            return bold.Groups[1].Value.Trim();

        int commaIndex = suspectLine.IndexOf(',');
        return (commaIndex >= 0 ? suspectLine.Substring(0, commaIndex) : suspectLine).Trim();
    }

    private static bool IsNoClue(string clueText)
    {
        if (string.IsNullOrWhiteSpace(clueText))
            return true;

        // The model's "no clue" answer varies ("None", "None.", "No new clue", etc.) -
        // normalize away trailing punctuation before comparing instead of requiring an
        // exact match.
        string normalized = clueText.Trim().TrimEnd('.', '!').Trim();
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
        // cutting at the last complete sentence within the limit so it doesn't trail
        // off mid-word.
        string truncated = result.Substring(0, MaxResultLength);

        int lastSentenceEnd = -1;
        foreach (char punct in new[] { '.', '!', '?' })
        {
            int idx = truncated.LastIndexOf(punct);
            if (idx > lastSentenceEnd)
                lastSentenceEnd = idx;
        }
        if (lastSentenceEnd > 0)
            return truncated.Substring(0, lastSentenceEnd + 1).Trim();

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
        content = Regex.Replace(content, @"\n{3,}", "\n\n");

        // TMP doesn't render markdown, so raw "**bold**" syntax just shows as literal
        // asterisks - strip it everywhere rather than only trimming the ends.
        content = content.Replace("**", "");
        return content.Trim();
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
            actions[i] = items[i];
        return actions;
    }
}
