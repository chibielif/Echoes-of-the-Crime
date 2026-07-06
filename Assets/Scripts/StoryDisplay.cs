using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class StoryDisplay : MonoBehaviour
{
    [SerializeField] private TMP_Text storyText;
    [SerializeField] private RectTransform contentRect;
    [SerializeField] private ScrollRect scrollRect;

    void Start()
    {
        ParsedStory story = GameSession.Story;
        if (story == null)
        {
            storyText.text = "No case has been generated yet.";
            return;
        }

        storyText.text = BuildDisplayText(story);

        if (contentRect != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);

        if (scrollRect != null)
            scrollRect.verticalNormalizedPosition = 1f;
    }

    private static string BuildDisplayText(ParsedStory story)
    {
        var sb = new StringBuilder();
        AppendSection(sb, null, story.CrimeSummary);
        AppendSection(sb, "Victim:", story.Victim);
        AppendSection(sb, "Crime Scene:", story.CrimeScene);
        AppendSection(sb, "Suspects:", story.Suspects);
        AppendSection(sb, "Clues:", story.Clues);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string header, string content)
    {
        if (string.IsNullOrEmpty(content))
            return;

        if (header != null)
            sb.Append(header).Append('\n');
        sb.Append(content).Append("\n\n");
    }
}
