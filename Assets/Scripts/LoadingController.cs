using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadingController : MonoBehaviour
{
    [SerializeField] private LLMStoryClient client;
    [SerializeField] private TMP_Text statusText;

    private int actionsAttempts;
    private int suspectsAttempts;
    private int cluesAttempts;
    private int murdererAttempts;
    private int pendingBackgroundFetches;

    void Start()
    {
        statusText.text = "Generating your case...";
        StartCoroutine(client.RequestStory(OnStorySuccess, OnError));
    }

    private void OnStorySuccess(string rawStory)
    {
        GameSession.SetRawStory(rawStory);

        bool needsActions = !GameSession.HasInitialActions;
        bool needsSuspects = !GameSession.HasSuspects;
        bool needsClues = !GameSession.HasClues;
        bool needsMurderer = !GameSession.HasRealMurderer;

        if (needsActions || needsSuspects || needsClues || needsMurderer)
        {
            // Keep this object (and its LLMStoryClient) alive across the scene change so
            // any follow-up requests can finish in the background while the player is
            // already reading the story, instead of making them wait here for a second
            // network round-trip.
            DontDestroyOnLoad(gameObject);
        }

        if (needsActions)
        {
            pendingBackgroundFetches++;
            StartCoroutine(client.RequestInitialActions(GameSession.RawStory, OnActionsSuccess, OnActionsError));
        }

        if (needsSuspects)
        {
            pendingBackgroundFetches++;
            StartCoroutine(client.RequestSuspects(GameSession.RawStory, OnSuspectsSuccess, OnSuspectsError));
        }

        if (needsClues)
        {
            pendingBackgroundFetches++;
            StartCoroutine(client.RequestClues(GameSession.RawStory, OnCluesSuccess, OnCluesError));
        }

        if (needsMurderer)
        {
            pendingBackgroundFetches++;
            StartCoroutine(client.RequestRealMurderer(GameSession.RawStory, OnMurdererSuccess, OnMurdererError));
        }

        SceneManager.LoadScene("Story");
    }

    private void OnActionsSuccess(string[] actions)
    {
        GameSession.SetInitialActions(actions);

        // A successful HTTP response doesn't guarantee the model actually produced 3
        // usable actions (Rule #1: never trust it to follow instructions) - confirmed in
        // an actual playthrough: the Continue button stayed permanently disabled with no
        // error and no further retry, since the retry path below only ever triggered on
        // an actual network/RunPod failure, never on a "successful" response that still
        // didn't parse into 3 valid actions. Treat that case the same as a transport
        // error instead of silently calling the fetch finished.
        if (!GameSession.HasInitialActions)
        {
            OnActionsError("Response did not contain 3 usable actions.");
            return;
        }

        FinishBackgroundFetch();
    }

    private void OnActionsError(string message)
    {
        Debug.LogWarning("Follow-up initial actions request failed: " + message);

        actionsAttempts++;
        if (actionsAttempts < 2)
        {
            StartCoroutine(client.RequestInitialActions(GameSession.RawStory, OnActionsSuccess, OnActionsError));
            return;
        }

        // Actions are required for the Game scene to function at all, and this failure
        // happens right at the very start (low sunk cost) - bail back to the Main Menu
        // rather than leave the player stuck with a permanently disabled Continue button.
        Destroy(gameObject);
        SceneManager.LoadScene("MainMenu");
    }

    private void OnSuspectsSuccess(List<string> names)
    {
        GameSession.SetSuspectNames(names);
        FinishBackgroundFetch();
    }

    private void OnSuspectsError(string message)
    {
        Debug.LogWarning("Follow-up suspects request failed: " + message);

        suspectsAttempts++;
        if (suspectsAttempts < 2)
        {
            StartCoroutine(client.RequestSuspects(GameSession.RawStory, OnSuspectsSuccess, OnSuspectsError));
            return;
        }

        // Unlike actions, suspects aren't needed until the very end of a full
        // playthrough - by then the player has invested real time, so fall back to the
        // generic "Suspect 1/2/3" labels (already handled in GameManager) rather than
        // throwing away their progress.
        FinishBackgroundFetch();
    }

    private void OnCluesSuccess(List<string> clues)
    {
        GameSession.SetClues(clues);
        FinishBackgroundFetch();
    }

    private void OnCluesError(string message)
    {
        Debug.LogWarning("Follow-up clues request failed: " + message);

        cluesAttempts++;
        if (cluesAttempts < 2)
        {
            StartCoroutine(client.RequestClues(GameSession.RawStory, OnCluesSuccess, OnCluesError));
            return;
        }

        // Clues are flavor/deduction aids, not a hard requirement for the game to
        // function - if they never come through, just leave the panel empty rather
        // than blocking or discarding the player's progress.
        FinishBackgroundFetch();
    }

    private void OnMurdererSuccess(string murderer)
    {
        GameSession.SetRealMurderer(murderer);
        FinishBackgroundFetch();
    }

    private void OnMurdererError(string message)
    {
        Debug.LogWarning("Follow-up real murderer request failed: " + message);

        murdererAttempts++;
        if (murdererAttempts < 2)
        {
            StartCoroutine(client.RequestRealMurderer(GameSession.RawStory, OnMurdererSuccess, OnMurdererError));
            return;
        }

        // Not required for the Game scene to function (unlike actions) and, unlike
        // suspects/clues, never shown directly to the player - GameManager's
        // guess-resolution and LLMStoryClient's per-turn prompts already treat a
        // missing Real Murderer as indeterminate/"not clearly specified" rather than
        // crashing or blocking progress, so just leave it unset if it never comes through.
        FinishBackgroundFetch();
    }

    private void FinishBackgroundFetch()
    {
        pendingBackgroundFetches--;
        if (pendingBackgroundFetches <= 0)
            Destroy(gameObject);
    }

    private const float MainMenuRedirectDelay = 10f;

    // The main story request has no retry (unlike the four follow-ups above) - it's the
    // very first thing that happens on entering the game, so there's nothing to fall
    // back to if it fails. Previously this just left statusText showing the error with
    // no path forward at all - a permanent dead end on the Loading screen, whether the
    // failure was a timeout ("delayed", from LLMStoryClient.PollForCompletion giving up)
    // or RunPod itself reporting the job CANCELLED. Now it tells the player where
    // they're headed and actually takes them there instead of stranding them.
    private void OnError(string message)
    {
        Debug.LogError(message);
        statusText.text = "Something went wrong generating your case:\n" + message +
            "\n\nYou're being directed to Main Menu...";
        StartCoroutine(ReturnToMainMenuAfterDelay());
    }

    private IEnumerator ReturnToMainMenuAfterDelay()
    {
        yield return new WaitForSeconds(MainMenuRedirectDelay);
        SceneManager.LoadScene("MainMenu");
    }
}
