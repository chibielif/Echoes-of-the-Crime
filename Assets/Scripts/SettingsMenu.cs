using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

public class SettingsMenu : MonoBehaviour
{
    public AudioMixer audioMixer;
    public TMP_Dropdown resolutionDropdown;

    // The slider's own authored value (whatever was left in the Editor) doesn't
    // necessarily match the mixer's actual current volume - e.g. AudioManager set it via
    // script, or a previous Settings visit changed it - so on entering this scene the
    // slider used to always show its stale authored position instead of reality.
    [SerializeField] private Slider musicSlider;

    Resolution[] resolutions;
    void Start(){
        resolutions = Screen.resolutions;
        resolutionDropdown.ClearOptions();

        List<string> options = new List<string>();

        int currentResolutionIndex = 0;
        for (int i = 0; i < resolutions.Length; i++)
        {
            string option = resolutions[i].width + " x " + resolutions[i].height;
            options.Add(option);

            if (resolutions[i].width == Screen.currentResolution.width &&
                resolutions[i].height == Screen.currentResolution.height)
            {
                currentResolutionIndex = i;
            }
        }
        resolutionDropdown.AddOptions(options);
        resolutionDropdown.value = currentResolutionIndex;
        resolutionDropdown.RefreshShownValue();

        // Read the mixer's actual current volume and position the slider to match -
        // SetValueWithoutNotify so this sync doesn't itself fire SetMusicVolume and
        // re-set the mixer to the value it already had.
        if (musicSlider != null && audioMixer.GetFloat("MusicVolume", out float currentMusicVolume))
            musicSlider.SetValueWithoutNotify(currentMusicVolume);
    }

    public void SetResolution(int resolutionIndex)
    {
        Resolution resolution = resolutions[resolutionIndex];
        Screen.SetResolution(resolution.width, resolution.height, Screen.fullScreen);
    }

    public void SetSfxVolume(float volume)
    {
        audioMixer.SetFloat("SFXVolume", volume);
    }
    
    public void SetMusicVolume(float volume)
    {
        audioMixer.SetFloat("MusicVolume", volume);
    }

    public void SetFullscreen(bool isFullscreen)
    {
        Screen.fullScreen = isFullscreen;
    }
}
