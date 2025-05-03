using UnityEngine;
using UnityEngine.SceneManagement;

public class GoToSettings : MonoBehaviour
{
    public void GoToSettingsScene()
    {
        SceneManager.LoadScene("Settings");
    }
}
