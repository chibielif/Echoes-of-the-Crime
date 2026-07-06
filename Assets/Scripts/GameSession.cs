using System.Collections.Generic;
using UnityEngine;

public static class GameSession
{
    public static string RawStory { get; private set; }
    public static ParsedStory Story { get; private set; }
    public static List<string> SuspectNames { get; private set; } = new List<string>();

    public static void SetRawStory(string raw)
    {
        RawStory = raw;
        Story = StoryParser.Parse(raw);
        SuspectNames = StoryParser.ExtractSuspectNames(Story.Suspects);

        if (IsMissingExpectedFields(Story))
            Debug.LogWarning("Story parsing produced one or more empty fields. Raw LLM response:\n" + raw);
    }

    public static void SetInitialActions(string[] actions)
    {
        if (Story != null)
            Story.InitialActions = actions;
    }

    public static void SetSuspectNames(List<string> names)
    {
        SuspectNames = names ?? new List<string>();
    }

    public static bool HasInitialActions =>
        Story?.InitialActions != null
        && Story.InitialActions.Length >= 3
        && !string.IsNullOrEmpty(Story.InitialActions[0])
        && !string.IsNullOrEmpty(Story.InitialActions[1])
        && !string.IsNullOrEmpty(Story.InitialActions[2]);

    public static bool HasSuspects => SuspectNames != null && SuspectNames.Count >= 3;

    private static bool IsMissingExpectedFields(ParsedStory story)
    {
        if (string.IsNullOrEmpty(story.CrimeSummary)) return true;
        if (string.IsNullOrEmpty(story.Victim)) return true;
        if (string.IsNullOrEmpty(story.CrimeScene)) return true;
        if (string.IsNullOrEmpty(story.Suspects)) return true;
        return !HasInitialActions;
    }

    public static string[] InitialActions => Story?.InitialActions;
}
