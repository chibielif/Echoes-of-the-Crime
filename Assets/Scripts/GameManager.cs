using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public TMP_Text[] clueTexts;
    public Button[] actionButtons; // 3 buttons
    public TMP_Text[] buttonTexts;
    public TMP_Text outputText;

    [SerializeField] private LLMStoryClient client;
    [SerializeField] private Transform cluesGroupParent;
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
        suspectNames = StoryParser.ExtractSuspectNames(GameSession.Story.Suspects);
        outputText.text = "Guess the murderer.";

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

        for (int i = 0; i < clueTexts.Length; i++)
            clueTexts[i].text = i < clueList.Count ? clueList[i] : "";

        for (int i = clueTexts.Length; i < clueList.Count; i++)
            AddClueEntry(clueList[i]);
    }

    private void AddClueEntry(string clueText)
    {
        if (clueTemplate == null || cluesGroupParent == null)
            return;

        GameObject clone = Instantiate(clueTemplate, cluesGroupParent);
        clone.SetActive(true);
        TMP_Text text = clone.GetComponentInChildren<TMP_Text>();
        if (text != null)
            text.text = clueText;
    }
}
