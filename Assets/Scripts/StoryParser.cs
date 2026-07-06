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

        int endIndex = raw.IndexOf("### End", StringComparison.OrdinalIgnoreCase);
        if (endIndex >= 0)
            raw = raw.Substring(0, endIndex).Trim();

        Match actionMatch = Regex.Match(raw, @"new\s+player\s+action\s*:", RegexOptions.IgnoreCase);
        Match clueMatch = Regex.Match(raw, @"new\s+clue\s+discovered\s*:", RegexOptions.IgnoreCase);

        int resultEnd = raw.Length;
        if (actionMatch.Success) resultEnd = Math.Min(resultEnd, actionMatch.Index);
        if (clueMatch.Success) resultEnd = Math.Min(resultEnd, clueMatch.Index);
        result.Result = StripMarkers(raw.Substring(0, resultEnd));

        if (actionMatch.Success)
        {
            int start = actionMatch.Index + actionMatch.Length;
            int end = clueMatch.Success && clueMatch.Index > actionMatch.Index ? clueMatch.Index : raw.Length;
            result.NewAction = StripMarkers(raw.Substring(start, end - start));
        }

        if (clueMatch.Success)
        {
            int start = clueMatch.Index + clueMatch.Length;
            int end = actionMatch.Success && actionMatch.Index > clueMatch.Index ? actionMatch.Index : raw.Length;
            string clueText = StripMarkers(raw.Substring(start, end - start));
            if (!string.IsNullOrEmpty(clueText) && !clueText.Equals("None", StringComparison.OrdinalIgnoreCase))
                result.NewClue = clueText;
        }

        return result;
    }

    public static List<string> ExtractClueList(string cluesText)
    {
        return ExtractListItems(cluesText);
    }

    public static List<string> ExtractSuspectNames(string suspectsText)
    {
        var names = new List<string>();
        foreach (string item in ExtractListItems(suspectsText))
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

    private static string StripMarkers(string content)
    {
        int markerIndex = content.IndexOf("####");
        if (markerIndex >= 0)
            content = content.Substring(0, markerIndex);
        return content.Trim();
    }

    private static List<string> ExtractListItems(string content)
    {
        var items = new List<string>();
        if (string.IsNullOrEmpty(content))
            return items;

        MatchCollection matches = Regex.Matches(content, @"^\s*(?:\d+[\.\)]|[-*•])\s*(.+)$", RegexOptions.Multiline);
        foreach (Match m in matches)
            items.Add(m.Groups[1].Value.Trim());
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
