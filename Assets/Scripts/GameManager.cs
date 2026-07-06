using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public Button[] actionButtons; // 3 buttons
    public TMP_Text[] buttonTexts;
    public TMP_Text outputText;

    [SerializeField] private LLMStoryClient client;
    [SerializeField] private RectTransform cluesGroupParent;
    [SerializeField] private GameObject clueTemplate;

    private const string FallbackNewAction = "Investigate further.";

    private class ActionBranch
    {
        public int StepsTaken;
        public bool IsComplete => StepsTaken >= 3;
    }

    private readonly ActionBranch[] branches = { new ActionBranch(), new ActionBranch(), new ActionBranch() };
    private readonly List<(string Action, string Result)> allHistory = new List<(string, string)>();
    private List<string> suspectNames;
    private bool guessPhase;

    void Start()
    {
        ParsedStory story = GameSession.Story;
        if (story == null)
        {
            outputText.text = "No case has been generated yet. Start from the Main Menu.";
            SetButtonsInteractable(false);
            return;
        }

        string[] initialActions = GameSession.InitialActions;
        outputText.text = "What will you do?";
        for (int i = 0; i < actionButtons.Length; i++)
        {
            int index = i;
            buttonTexts[i].text = initialActions != null && i < initialActions.Length && !string.IsNullOrEmpty(initialActions[i])
                ? initialActions[i]
                : FallbackNewAction;
            actionButtons[i].onClick.AddListener(() => OnActionClicked(index));
        }

        if (clueTemplate != null)
            clueTemplate.SetActive(false);

        PopulateClues(story);
    }

    private void OnActionClicked(int index)
    {
        if (guessPhase)
        {
            HandleGuess(index);
            return;
        }

        ActionBranch branch = branches[index];
        if (branch.IsComplete)
            return;

        string currentAction = buttonTexts[index].text;
        bool isFinalStep = branch.StepsTaken == 2;
        string previousActionsText = BuildHistoryText(allHistory);

        SetButtonsInteractable(false);
        outputText.text = "...";

        StartCoroutine(client.RequestActionResult(
            GameSession.RawStory,
            previousActionsText,
            currentAction,
            isFinalStep,
            response => OnActionResultSuccess(index, currentAction, response),
            OnActionResultError));
    }

    private void OnActionResultSuccess(int index, string actionTaken, ActionResponse response)
    {
        ActionBranch branch = branches[index];
        string result = string.IsNullOrEmpty(response.Result) ? "..." : response.Result;
        allHistory.Add((actionTaken, result));
        branch.StepsTaken++;

        outputText.text = result + (response.NewClue != null ? "\n\nNew clue found." : "");

        if (response.NewClue != null)
            AddClueEntry(response.NewClue);

        if (branch.IsComplete)
            actionButtons[index].interactable = false;
        else
            buttonTexts[index].text = string.IsNullOrEmpty(response.NewAction) ? FallbackNewAction : response.NewAction;

        RefreshButtonInteractability();
        CheckForGuessPhase();
    }

    private void OnActionResultError(string message)
    {
        Debug.LogError("Action request failed: " + message);
        outputText.text = "Something went wrong processing that action. Try again.";
        RefreshButtonInteractability();
    }

    private void CheckForGuessPhase()
    {
        if (guessPhase)
            return;

        foreach (ActionBranch branch in branches)
            if (!branch.IsComplete)
                return;

        guessPhase = true;
        StartCoroutine(EnterGuessPhase());
    }

    private IEnumerator EnterGuessPhase()
    {
        outputText.text = "Guess the murderer.";
        SetButtonsInteractable(false);

        // Suspects are normally ready long before this point (a background follow-up
        // fetch, if one was even needed, has the entire rest of the playthrough to
        // finish) - but give it a short grace period on the rare chance it's still
        // in flight rather than immediately settling for generic labels.
        float waited = 0f;
        const float maxWait = 15f;
        while (!GameSession.HasSuspects && waited < maxWait)
        {
            yield return new WaitForSeconds(1f);
            waited += 1f;
        }

        suspectNames = GameSession.HasSuspects ? GameSession.SuspectNames : new List<string>();

        for (int i = 0; i < actionButtons.Length; i++)
        {
            buttonTexts[i].text = i < suspectNames.Count ? suspectNames[i] : $"Suspect {i + 1}";
            actionButtons[i].interactable = true;
        }
    }

    private void HandleGuess(int index)
    {
        string suspect = buttonTexts[index].text;
        outputText.text = $"You accused {suspect}. (Resolution logic coming soon.)";
    }

    private void RefreshButtonInteractability()
    {
        for (int i = 0; i < actionButtons.Length; i++)
            actionButtons[i].interactable = !branches[i].IsComplete;
    }

    private void SetButtonsInteractable(bool interactable)
    {
        foreach (Button b in actionButtons)
            b.interactable = interactable;
    }

    private static string BuildHistoryText(List<(string Action, string Result)> history)
    {
        if (history.Count == 0)
            return "None yet.";

        var sb = new StringBuilder();
        for (int i = 0; i < history.Count; i++)
        {
            sb.Append("Action: ").Append(history[i].Action).Append('\n');
            sb.Append("Result: ").Append(history[i].Result);
            if (i < history.Count - 1)
                sb.Append("\n\n");
        }
        return sb.ToString();
    }

    private void PopulateClues(ParsedStory story)
    {
        List<string> clueList = StoryParser.ExtractClueList(story.Clues);

        if (clueList.Count == 0)
            Debug.LogWarning("No clues could be parsed from the story's Clues section. Raw text:\n" + story.Clues);

        foreach (string clue in clueList)
            AddClueEntry(clue);
    }

    private void AddClueEntry(string clueText)
    {
        if (clueTemplate == null || cluesGroupParent == null)
            return;

        GameObject clone = Instantiate(clueTemplate, cluesGroupParent);
        clone.SetActive(true);
        TMP_Text text = clone.GetComponentInChildren<TMP_Text>();
        if (text != null)
            text.text = FormatClue(clueText);

        RebuildCluesLayout();
    }

    private const int MaxClueLength = 300;

    private static string FormatClue(string clueText)
    {
        string trimmed = TruncateClue(clueText.Trim());
        bool alreadyBulleted = trimmed.StartsWith("-") || trimmed.StartsWith("•") || trimmed.StartsWith("*");
        return alreadyBulleted ? trimmed : "- " + trimmed;
    }

    private static string TruncateClue(string clue)
    {
        if (clue.Length <= MaxClueLength)
            return clue;

        string truncated = clue.Substring(0, MaxClueLength);

        // Prefer cutting at the last complete sentence within the limit so it doesn't
        // trail off mid-thought.
        int lastSentenceEnd = -1;
        foreach (char punct in new[] { '.', '!', '?' })
        {
            int idx = truncated.LastIndexOf(punct);
            if (idx > lastSentenceEnd)
                lastSentenceEnd = idx;
        }

        if (lastSentenceEnd > 0)
            return truncated.Substring(0, lastSentenceEnd + 1).Trim();

        // No sentence boundary found - fall back to a clean word cut with an ellipsis.
        int lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 0)
            truncated = truncated.Substring(0, lastSpace);
        return truncated.TrimEnd(',', ';', ':', ' ') + "...";
    }

    private void RebuildCluesLayout()
    {
        if (cluesGroupParent != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(cluesGroupParent);
    }
}
