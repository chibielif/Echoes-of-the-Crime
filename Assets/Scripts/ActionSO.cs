using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Action", fileName = "New Action")]
public class ActionSO : ScriptableObject
{
    [TextArea(2, 10)]
    [SerializeField] string action = "enter action";

    public string GetAction()
    {
        return action;
    }
}

public class Actions
{
    ActionSO actionSO;

    void ActionList(){
        string actionText = actionSO.GetAction();
    }
}
