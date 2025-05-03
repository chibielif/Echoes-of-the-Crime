using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ButtonTextChanger : MonoBehaviour
{
    public Button targetButton;
    public string newText = "Investigate the smudge.";

    public TMP_Text targetTMPText;   // Reference to the TMP_Text component
    public string newTMPText = "You notice the shattered vase and a smudge on the desk.";         // The new text for the TMP_Text element

    private TMP_Text buttonText;

    void Start()
    {
        // Get the TextMeshPro component
        buttonText = targetButton.GetComponentInChildren<TMP_Text>();

        // Add a listener to the button's onClick event
        targetButton.onClick.AddListener(ChangeButtonText);
    }

    void ChangeButtonText()
    {
        if (buttonText != null)
        {
            buttonText.text = newText;
        }

        // Change the TMP_Text element's text
        if (targetTMPText != null)
        {
            targetTMPText.text = newTMPText;
        }
    }
}
