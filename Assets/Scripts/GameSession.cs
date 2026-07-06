using UnityEngine;

public static class GameSession
{
    public static string RawStory { get; private set; }
    public static ParsedStory Story { get; private set; }

    public static void SetRawStory(string raw)
    {
        RawStory = raw;
        Story = StoryParser.Parse(raw);

        if (IsMissingExpectedFields(Story))
            Debug.LogWarning("Story parsing produced one or more empty fields. Raw LLM response:\n" + raw);
    }

    public static void SetInitialActions(string[] actions)
    {
        if (Story != null)
            Story.InitialActions = actions;
    }

    public static bool HasInitialActions =>
        Story?.InitialActions != null
        && Story.InitialActions.Length >= 3
        && !string.IsNullOrEmpty(Story.InitialActions[0])
        && !string.IsNullOrEmpty(Story.InitialActions[1])
        && !string.IsNullOrEmpty(Story.InitialActions[2]);

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
