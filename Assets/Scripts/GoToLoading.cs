using UnityEngine;
using UnityEngine.SceneManagement;

public class GoToLoading : MonoBehaviour
{
    public void GoToLoadingScene()
    {
        SceneManager.LoadScene("Loading");
    }
}
