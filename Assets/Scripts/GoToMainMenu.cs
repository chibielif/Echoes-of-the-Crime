using UnityEngine;
using UnityEngine.SceneManagement;

public class GoToMainMenu : MonoBehaviour
{
    public void GoToMainMenuScene()
    {
        SceneManager.LoadScene("MainMenu");
    }
}
