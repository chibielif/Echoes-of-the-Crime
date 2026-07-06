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

        if (needsActions || needsSuspects)
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

        SceneManager.LoadScene("Story");
    }

    private void OnActionsSuccess(string[] actions)
    {
        GameSession.SetInitialActions(actions);
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

    private void FinishBackgroundFetch()
    {
        pendingBackgroundFetches--;
        if (pendingBackgroundFetches <= 0)
            Destroy(gameObject);
    }

    private void OnError(string message)
    {
        statusText.text = "Something went wrong generating your case:\n" + message;
        Debug.LogError(message);
    }
}
