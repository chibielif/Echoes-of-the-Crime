using UnityEngine;
using UnityEngine.SceneManagement;

public class GoToGameplay : MonoBehaviour
{
    public void GoToGameplayScene()
    {
        SceneManager.LoadScene("Game");
    }
}
