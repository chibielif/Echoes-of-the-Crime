using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly List<string> allClues = new List<string>();
    private List<string> suspectNames;
    private bool awaitingGuessPrompt;
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

        StartCoroutine(PopulateCluesWhenReady());
    }

    private void OnActionClicked(int index)
    {
        if (guessPhase)
        {
            HandleGuess(index);
            return;
        }

        if (awaitingGuessPrompt)
        {
            if (index == 0)
                StartCoroutine(EnterGuessPhase());
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
            BuildCluesText(),
            response => OnActionResultSuccess(index, currentAction, response),
            OnActionResultError));
    }

    private void OnActionResultSuccess(int index, string actionTaken, ActionResponse response)
    {
        ActionBranch branch = branches[index];
        string result = string.IsNullOrEmpty(response.Result) ? "..." : response.Result;
        allHistory.Add((actionTaken, result));
        branch.StepsTaken++;

        // Only claim a new clue was found if it actually made it into the list -
        // AddClueEntry can silently discard an incomplete/truncated fragment, and the
        // message shouldn't promise something that isn't there.
        bool clueAdded = response.NewClue != null && AddClueEntry(response.NewClue);
        outputText.text = result + (clueAdded ? "\n\nNew clue found." : "");

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
        if (awaitingGuessPrompt || guessPhase)
            return;

        foreach (ActionBranch branch in branches)
            if (!branch.IsComplete)
                return;

        // Don't touch outputText here - the final action's result is still showing and
        // should stay visible until the player is ready to move on. Only button 1 turns
        // into a "Guess the murderer" prompt; the suspects don't appear until it's clicked.
        awaitingGuessPrompt = true;
        buttonTexts[0].text = "Guess the murderer.";
        buttonTexts[1].text = "";
        buttonTexts[2].text = "";
        actionButtons[0].interactable = true;
        actionButtons[1].interactable = false;
        actionButtons[2].interactable = false;
    }

    private IEnumerator EnterGuessPhase()
    {
        SetButtonsInteractable(false);
        outputText.text = "Guess the murderer.";

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

        guessPhase = true;
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

    private string BuildCluesText()
    {
        return allClues.Count == 0 ? "None yet." : string.Join("\n", allClues);
    }

    private IEnumerator PopulateCluesWhenReady()
    {
        // Clues are normally ready immediately from the initial story parse. If they
        // weren't (e.g. the model skipped the Clues section entirely), a background
        // follow-up fetch was kicked off back in the Loading scene - give it a short
        // grace period rather than showing an empty panel right away.
        float waited = 0f;
        const float maxWait = 20f;
        while (!GameSession.HasClues && waited < maxWait)
        {
            yield return new WaitForSeconds(1f);
            waited += 1f;
        }

        if (!GameSession.HasClues)
            Debug.LogWarning("No clues were available for this story, even after the follow-up fetch.");

        foreach (string clue in GameSession.Clues)
            AddClueEntry(clue);
    }

    private bool AddClueEntry(string clueText)
    {
        string formatted = FormatClue(clueText);
        if (formatted == null)
            return false; // Incomplete/truncated fragment with no real sentence end -
                           // skip it rather than show the player a confusing half-sentence.

        allClues.Add(formatted);

        if (clueTemplate == null || cluesGroupParent == null)
            return true;

        GameObject clone = Instantiate(clueTemplate, cluesGroupParent);
        clone.SetActive(true);
        TMP_Text text = clone.GetComponentInChildren<TMP_Text>();
        if (text != null)
            text.text = formatted;

        RebuildCluesLayout();
        return true;
    }

    private const int MaxClueLength = 300;

    private static string FormatClue(string clueText)
    {
        string trimmed = TruncateClue(clueText.Trim());
        if (trimmed == null)
            return null;

        bool alreadyBulleted = trimmed.StartsWith("-") || trimmed.StartsWith("•") || trimmed.StartsWith("*");
        return alreadyBulleted ? trimmed : "- " + trimmed;
    }

    // Matches a sentence-ending ./!/? only when followed by whitespace+capital letter or
    // the end of the string, and NOT immediately preceded by a common title abbreviation
    // (whose following word is capitalized too, e.g. "Mrs. Marlowe" - the naive
    // followed-by-capital check alone would wrongly treat "Mrs." itself as the end).
    private static readonly Regex SentenceEndRegex = new Regex(
        @"(?<!\b(?:Mr|Mrs|Ms|Dr|Prof|St|Jr|Sr|Capt|Lt|Col|Gen|Rev|Sgt|Fr|Hon))[.!?](?=\s+[A-Z]|\s*$)",
        RegexOptions.IgnoreCase);

    private static string TruncateClue(string clue)
    {
        // Clues are meant to be a single sentence - cut at the first genuine sentence
        // end so any unrelated content the model tacks on afterward (a stray question,
        // meta-commentary, a second unrelated clue, etc.) never makes it into the
        // display. If there's no real sentence end at all, the model's generation was
        // most likely cut off mid-thought - discard it rather than show a fragment.
        Match sentenceEnd = SentenceEndRegex.Match(clue);
        if (!sentenceEnd.Success)
            return null;

        string sentence = clue.Substring(0, sentenceEnd.Index + 1).Trim();

        if (sentence.Length <= MaxClueLength)
            return sentence;

        // Extremely long single "sentence" (e.g. missing punctuation) - fall back to a
        // clean word cut with an ellipsis so it doesn't overflow the UI.
        string truncated = sentence.Substring(0, MaxClueLength);
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
