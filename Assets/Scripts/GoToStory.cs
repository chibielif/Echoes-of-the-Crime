using UnityEngine;
using UnityEngine.SceneManagement;

public class GoToStory : MonoBehaviour
{
    public void GoToStoryScene()
    {
        SceneManager.LoadScene("Story");
    }
}
