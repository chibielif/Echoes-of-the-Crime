using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
    public Button[] actionButtons; // 3 buttons
    public TMP_Text[] buttonTexts;
    public TMP_Text outputText;

    [SerializeField] private LLMStoryClient client;
    [SerializeField] private RectTransform cluesGroupParent;
    [SerializeField] private GameObject clueTemplate;

    // "CluesTag" header above the panel, and the two bottom-right buttons (renamed from
    // their old Story/MainMenu-navigation roles - CluesButton/SuspectsButton) that
    // toggle the SAME scroll view between showing clues and showing the suspects list,
    // rather than each having its own separate view.
    [SerializeField] private TMP_Text cluesTagText;
    private const string CluesTagLabel = "CLUES";
    private const string SuspectsTagLabel = "SUSPECTS";

    // Every instantiated clue clone is tracked here (in addition to living under
    // cluesGroupParent) so ShowSuspects()/ShowClues() can toggle them all inactive/active
    // as a group without touching the suspects entry sharing the same parent.
    private readonly List<GameObject> clueEntryObjects = new List<GameObject>();
    private GameObject suspectsEntryObject;
    private bool showingSuspects;

    // Wraps outputText in a scroll view (Viewport/Content, same pattern as
    // StoryDisplay's Scroll View) so a long, richly-detailed result can be read in full
    // by scrolling instead of being hard-truncated to fit a fixed-height box. Both are
    // optional (null-checked in SetOutputText) so this degrades gracefully if the scene
    // wiring is ever missing.
    [SerializeField] private ScrollRect outputScrollRect;
    [SerializeField] private RectTransform outputContentRect;

    private const string FallbackNewAction = "Investigate further.";

    private class ActionBranch
    {
        public int StepsTaken;
        public bool IsComplete => StepsTaken >= 3;
    }

    private readonly ActionBranch[] branches = { new ActionBranch(), new ActionBranch(), new ActionBranch() };

    // Each action branch is bound to one specific suspect (by index, matching
    // GameSession.SuspectNames/the guess-phase button order) and keeps its own history
    // and discovered clues, entirely separate from the other two branches - see the
    // "Suspect-bound action branches" note in CLAUDE.md for why.
    private readonly List<(string Action, string Result)>[] branchHistories =
        { new List<(string, string)>(), new List<(string, string)>(), new List<(string, string)>() };
    private readonly List<string>[] branchClues = { new List<string>(), new List<string>(), new List<string>() };
    private string[] branchSuspectNames = new string[3];

    // The initial story's own clue list is shared baseline knowledge for every branch
    // (like reading the case file), unlike clues discovered mid-branch which are scoped
    // to whichever branch found them.
    private readonly List<string> initialClues = new List<string>();

    // Flat, combined view of every clue found so far (initial + all branches) - used for
    // the Clues panel display and for cross-branch duplicate detection, independent of
    // the per-branch lists used to build each branch's own prompt context.
    private readonly List<string> allClues = new List<string>();
    private List<string> suspectNames;
    private bool awaitingGuessPrompt;
    private bool guessPhase;
    private bool gameEnded;

    private const int MaxGuesses = 2;
    private int guessesRemaining = MaxGuesses;
    private int murdererIndex = -1;

    void Start()
    {
        ParsedStory story = GameSession.Story;
        if (story == null)
        {
            SetOutputText("No case has been generated yet. Start from the Main Menu.");
            SetButtonsInteractable(false);
            return;
        }

        // Bind each branch to a specific suspect up front, by index - matching
        // GameSession.SuspectNames' order (and therefore the guess-phase button order
        // too, for free). Suspects are normally ready well before this point (the same
        // reasoning EnterGuessPhase already relies on - Loading/Story gave any needed
        // follow-up fetch plenty of time), so this is read synchronously rather than
        // waited on, same as GameSession.InitialActions just below. Falls back to
        // generic labels if fewer than 3 real names are available, same fallback text
        // already used for the guess-phase buttons.
        List<string> namesForBranches = GameSession.HasSuspects ? GameSession.SuspectNames : new List<string>();
        for (int i = 0; i < branchSuspectNames.Length; i++)
            branchSuspectNames[i] = i < namesForBranches.Count ? namesForBranches[i] : $"Suspect {i + 1}";

        string[] initialActions = GameSession.InitialActions;
        SetOutputText("What will you do?");
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

        PopulateSuspectsEntry();
        StartCoroutine(PopulateCluesWhenReady());
    }

    // Builds the single suspects list-entry, sharing the same clueTemplate/
    // cluesGroupParent the clue bullets already use, so it visually matches and slots
    // into the same scroll view instead of needing a separate one. The text itself is
    // filled in later, in ShowSuspects() (refreshed on every click, not just here) -
    // see that method for why.
    private void PopulateSuspectsEntry()
    {
        if (clueTemplate == null || cluesGroupParent == null)
            return;

        suspectsEntryObject = Instantiate(clueTemplate, cluesGroupParent);
        suspectsEntryObject.SetActive(false);
    }

    // Prefers the full Suspects text block (names + descriptions/alibis) - those bare
    // names are already visible on the guess buttons, so the reason to look here is the
    // detail the guess buttons don't show. But the initial story parse can come back
    // with an empty Suspects block, which kicks off a background RequestSuspects
    // follow-up (LoadingController) that only ever fills in GameSession.SuspectNames
    // (bare names), never this richer text block - if this were computed once at Start()
    // (before that follow-up has necessarily finished) and never revisited, the panel
    // could show "no suspects" forever even after the follow-up succeeded moments later.
    // Recomputing on every ShowSuspects() call means the next click after the follow-up
    // lands shows the correct thing instead.
    private static string BuildSuspectsDisplayText()
    {
        ParsedStory story = GameSession.Story;
        if (story != null && !string.IsNullOrWhiteSpace(story.Suspects))
            return story.Suspects;

        if (GameSession.HasSuspects)
            return string.Join("\n", GameSession.SuspectNames);

        return "No suspect details available.";
    }

    // Both are no-ops if the requested view is already showing, so repeatedly clicking
    // the same button doesn't churn the layout for nothing.
    public void ShowClues()
    {
        if (!showingSuspects)
            return;

        showingSuspects = false;
        if (cluesTagText != null)
            cluesTagText.text = CluesTagLabel;
        if (suspectsEntryObject != null)
            suspectsEntryObject.SetActive(false);
        foreach (GameObject clueObj in clueEntryObjects)
            clueObj.SetActive(true);
        RebuildCluesLayout();
    }

    public void ShowSuspects()
    {
        if (showingSuspects)
            return;

        showingSuspects = true;
        if (cluesTagText != null)
            cluesTagText.text = SuspectsTagLabel;
        foreach (GameObject clueObj in clueEntryObjects)
            clueObj.SetActive(false);
        if (suspectsEntryObject != null)
        {
            TMP_Text text = suspectsEntryObject.GetComponentInChildren<TMP_Text>(includeInactive: true);
            if (text != null)
                text.text = BuildSuspectsDisplayText();
            suspectsEntryObject.SetActive(true);
        }
        RebuildCluesLayout();
    }

    private void OnActionClicked(int index)
    {
        if (gameEnded)
        {
            if (index == actionButtons.Length - 1)
                SceneManager.LoadScene("MainMenu");
            return;
        }

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
        // Each branch only ever sees its own history, never the other two branches' -
        // this is what stops the model from treating a different branch's action as a
        // continuation of this one's last result (confirmed in an actual playthrough).
        string previousActionsText = BuildHistoryText(branchHistories[index]);

        SetButtonsInteractable(false);
        SetOutputText("...");

        StartCoroutine(client.RequestActionResult(
            GameSession.RawStory,
            previousActionsText,
            currentAction,
            isFinalStep,
            BuildCluesText(index),
            GameSession.RealMurderer,
            branchSuspectNames[index],
            response => OnActionResultSuccess(index, currentAction, response),
            OnActionResultError));
    }

    private void OnActionResultSuccess(int index, string actionTaken, ActionResponse response)
    {
        ActionBranch branch = branches[index];
        string result = string.IsNullOrEmpty(response.Result) ? "..." : response.Result;
        branchHistories[index].Add((actionTaken, result));
        branch.StepsTaken++;

        // Only claim a new clue was found if it actually made it into the list -
        // AddClueEntry can silently discard an incomplete/truncated fragment, and the
        // message shouldn't promise something that isn't there.
        bool clueAdded = response.NewClue != null && AddClueEntry(response.NewClue, index);
        SetOutputText(result + (clueAdded ? "\n\nNew clue found." : ""));

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
        SetOutputText("Something went wrong processing that action. Try again.");
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
        SetOutputText("Guess the murderer.");

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

        // Fewer than 3 real suspects means every button below falls back to a generic
        // "Suspect N" label with no real mapping to check a guess against - force
        // indeterminate rather than resolving an index against those fallback labels.
        murdererIndex = GameSession.HasSuspects ? GameSession.DetermineMurdererIndex() : -1;
        // GameSession.SuspectNames could in principle hold more entries than the 3
        // buttons shown (e.g. an over-long Suspects list) - clamp defensively so a
        // later buttonTexts[murdererIndex] lookup can never throw.
        if (murdererIndex >= actionButtons.Length)
            murdererIndex = -1;

        for (int i = 0; i < actionButtons.Length; i++)
        {
            buttonTexts[i].text = i < suspectNames.Count ? suspectNames[i] : $"Suspect {i + 1}";
            actionButtons[i].interactable = true;
        }
    }

    private void HandleGuess(int index)
    {
        string suspect = buttonTexts[index].text;

        if (murdererIndex < 0)
        {
            // Deliberately doesn't say "there's no way to call this one right or wrong"
            // here - immediately following that with the full motive+proof reveal below
            // directly contradicts it (confirmed in an actual playthrough: the message
            // told the player the case couldn't be resolved, then named the murderer one
            // sentence later). Just present the reveal on its own instead.
            EndGame(GameSession.HasRealMurderer
                ? $"Here's what really happened:\n\n{GameSession.RealMurderer}"
                : "The case is closed, but no clear culprit could be determined.");
            return;
        }

        if (index == murdererIndex)
        {
            EndGame($"You were right. {suspect} did it.{BuildRevealSuffix()}");
            return;
        }

        guessesRemaining--;
        actionButtons[index].interactable = false;

        if (guessesRemaining <= 0)
        {
            string actualName = buttonTexts[murdererIndex].text;
            EndGame($"Wrong again. It wasn't {suspect} - it was {actualName}.{BuildRevealSuffix()}");
            return;
        }

        SetOutputText($"That's not them. {guessesRemaining} guess{(guessesRemaining == 1 ? "" : "es")} left.");
    }

    private static string BuildRevealSuffix() =>
        GameSession.HasRealMurderer ? $"\n\nHere's what really happened:\n\n{GameSession.RealMurderer}" : "";

    private void EndGame(string message)
    {
        gameEnded = true;
        SetOutputText(message);

        for (int i = 0; i < actionButtons.Length; i++)
            actionButtons[i].interactable = false;

        // The reveal message (motive + decisive proof, sometimes lengthy) can run long
        // enough to visually overlap a button in the first slot - putting "Return to
        // Main Menu" in the last slot instead gives it the most clearance.
        int lastIndex = actionButtons.Length - 1;
        for (int i = 0; i < lastIndex; i++)
            buttonTexts[i].text = "";
        buttonTexts[lastIndex].text = "Return to Main Menu";
        actionButtons[lastIndex].interactable = true;
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

    // Combines the shared initial clues with just this branch's own discoveries - never
    // another branch's - so a suspect-focused thread only ever sees evidence relevant to
    // (or at least previously surfaced by) its own investigation.
    private string BuildCluesText(int branchIndex)
    {
        var combined = new List<string>(initialClues);
        combined.AddRange(branchClues[branchIndex]);
        return combined.Count == 0 ? "None yet." : string.Join("\n", combined);
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

        // Null branchIndex: these are shared baseline clues, not tied to any one branch's
        // own investigation.
        foreach (string clue in GameSession.Clues)
            AddClueEntry(clue, branchIndex: null);
    }

    private bool AddClueEntry(string clueText, int? branchIndex)
    {
        string formatted = FormatClue(clueText);
        if (formatted == null)
            return false; // Incomplete/truncated fragment with no real sentence end -
                           // skip it rather than show the player a confusing half-sentence.

        // Despite "Known Clues So Far" being in every action prompt, the model
        // sometimes restates an already-discovered clue verbatim instead of finding
        // something new (weak instruction-following, same root cause as everywhere
        // else in this project) - skip an exact repeat (even one found by a different
        // branch) rather than padding the panel with the same fact twice, and don't let
        // the caller claim "New clue found." for it.
        if (allClues.Contains(formatted))
            return false;

        allClues.Add(formatted);
        if (branchIndex.HasValue)
            branchClues[branchIndex.Value].Add(formatted);
        else
            initialClues.Add(formatted);

        if (clueTemplate == null || cluesGroupParent == null)
            return true;

        GameObject clone = Instantiate(clueTemplate, cluesGroupParent);
        clone.SetActive(true);
        TMP_Text text = clone.GetComponentInChildren<TMP_Text>();
        if (text != null)
            text.text = formatted;

        // A clue can be discovered while the Suspects view is the one currently showing
        // (they share the same scroll view/parent) - without this, the new clue would
        // pop up active and visible right in the middle of the suspects entry instead of
        // staying hidden until the player switches back to Clues.
        clueEntryObjects.Add(clone);
        if (showingSuspects)
            clone.SetActive(false);

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

    private static string TruncateClue(string clue)
    {
        // Clues are meant to be a single sentence - cut at the first genuine sentence
        // end so any unrelated content the model tacks on afterward (a stray question,
        // meta-commentary, a second unrelated clue, etc.) never makes it into the
        // display. If there's no real sentence end at all, the model's generation was
        // most likely cut off mid-thought - discard it rather than show a fragment.
        // Uses StoryParser.SentenceEndRegex rather than a separate copy, so this and the
        // parser's own truncation logic can't drift out of sync with each other.
        Match sentenceEnd = StoryParser.SentenceEndRegex.Match(clue);
        if (!sentenceEnd.Success)
            return null;

        string sentence = clue.Substring(0, sentenceEnd.Index + sentenceEnd.Length).Trim();

        if (sentence.Length <= MaxClueLength)
            return CapitalizeFirstLetter(sentence);

        // Extremely long single "sentence" (e.g. missing punctuation) - fall back to a
        // clean word cut with an ellipsis so it doesn't overflow the UI.
        string truncated = sentence.Substring(0, MaxClueLength);
        int lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 0)
            truncated = truncated.Substring(0, lastSpace);
        return CapitalizeFirstLetter(truncated.TrimEnd(',', ';', ':', ' ') + "...");
    }

    // A clue is meant to read as its own standalone sentence, but sometimes survives
    // starting lowercase - e.g. stripping a leading "Yes, " affirmation off a
    // self-answered yes/no question can leave a naturally-lowercase continuation
    // behind ("Yes, according to her statement..." -> "according to her statement...").
    // Capitalizing the first letter doesn't recover a lost antecedent the model never
    // stated outside that stripped "Yes," context, but at least reads as a complete
    // sentence instead of a visibly truncated fragment.
    private static string CapitalizeFirstLetter(string text)
    {
        if (string.IsNullOrEmpty(text) || !char.IsLower(text[0]))
            return text;
        return char.ToUpper(text[0]) + text.Substring(1);
    }

    private void RebuildCluesLayout()
    {
        if (cluesGroupParent != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(cluesGroupParent);
    }

    // Sets outputText and resets the scroll position to the top - same
    // ForceRebuildLayoutImmediate + verticalNormalizedPosition pattern StoryDisplay.cs
    // already uses, so a long result from a previous turn doesn't leave the view
    // scrolled partway down when a new, shorter message replaces it.
    private void SetOutputText(string text)
    {
        outputText.text = text;

        if (outputContentRect != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(outputContentRect);
        if (outputScrollRect != null)
            outputScrollRect.verticalNormalizedPosition = 1f;
    }
}
