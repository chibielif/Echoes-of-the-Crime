using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Gameplay : MonoBehaviour
{
    [SerializeField]TextMeshProUGUI outputText;
    [SerializeField]OutputSO output;
    [SerializeField]StorySO story;
    [SerializeField] GameObject[] actionButtons;

    void Start()
    {
        outputText.text = output.GetOutput(0);
        for (int i=0; i<3; i++)
        {
            TextMeshProUGUI buttonText = actionButtons[i].GetComponentInChildren<TextMeshProUGUI>();
            buttonText.text = story.GetAction(i);
        }
        
    }

    public void OnActionSelected(int index)
    {
        outputText.text = output.GetOutput(index + 1);
    }

}
