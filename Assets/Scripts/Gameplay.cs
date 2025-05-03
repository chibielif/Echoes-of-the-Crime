using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Gameplay : MonoBehaviour
{
    [SerializeField] TextMeshProUGUI actionText;

    [SerializeField] ActionSO action1;
    [SerializeField] ActionSO action2;

    void Start()
    {
        actionText.text = action1.GetAction();

    }


}
