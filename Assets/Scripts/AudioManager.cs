using UnityEngine;
using UnityEngine.Audio;

[RequireComponent(typeof(AudioSource))]
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [SerializeField] private AudioClip themeSong;
    [SerializeField] private AudioMixerGroup musicMixerGroup;

    private AudioSource audioSource;

    void Awake()
    {
        // Standard singleton guard: if one already exists (carried over via
        // DontDestroyOnLoad from an earlier scene), this instance is a duplicate from
        // re-entering a scene that also has its own AudioManager in it - destroy it
        // instead of starting a second, overlapping copy of the theme song.
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        audioSource = GetComponent<AudioSource>();
        audioSource.outputAudioMixerGroup = musicMixerGroup;
        audioSource.loop = true;
        audioSource.playOnAwake = false;
        audioSource.clip = themeSong;

        if (themeSong != null)
            audioSource.Play();
    }
}
