using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class GoToGameplay : MonoBehaviour
{
    [SerializeField] private Button continueButton;

    void Update()
    {
        if (continueButton != null)
            continueButton.interactable = GameSession.HasInitialActions;
    }

    public void GoToGameplayScene()
    {
        if (!GameSession.HasInitialActions)
            return;

        SceneManager.LoadScene("Game");
    }
}
