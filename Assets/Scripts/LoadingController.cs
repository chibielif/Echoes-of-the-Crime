using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadingController : MonoBehaviour
{
    [SerializeField] private LLMStoryClient client;
    [SerializeField] private TMP_Text statusText;

    private int followUpAttempts;

    void Start()
    {
        statusText.text = "Generating your case...";
        StartCoroutine(client.RequestStory(OnStorySuccess, OnError));
    }

    private void OnStorySuccess(string rawStory)
    {
        GameSession.SetRawStory(rawStory);

        if (!GameSession.HasInitialActions)
        {
            // Keep this object (and its LLMStoryClient) alive across the scene
            // change so the follow-up request can finish in the background
            // while the player is already reading the story, instead of
            // making them wait here for a second network round-trip.
            DontDestroyOnLoad(gameObject);
            StartCoroutine(client.RequestInitialActions(GameSession.RawStory, OnActionsSuccess, OnActionsError));
        }

        SceneManager.LoadScene("Story");
    }

    private void OnActionsSuccess(string[] actions)
    {
        GameSession.SetInitialActions(actions);
        Destroy(gameObject);
    }

    private void OnActionsError(string message)
    {
        Debug.LogWarning("Follow-up initial actions request failed: " + message);

        followUpAttempts++;
        if (followUpAttempts < 2)
        {
            StartCoroutine(client.RequestInitialActions(GameSession.RawStory, OnActionsSuccess, OnActionsError));
            return;
        }

        Destroy(gameObject);
        SceneManager.LoadScene("MainMenu");
    }

    private void OnError(string message)
    {
        statusText.text = "Something went wrong generating your case:\n" + message;
        Debug.LogError(message);
    }
}
