using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Story", fileName ="New Story" )]
public class StorySO : ScriptableObject
{
    [SerializeField] int storyNumber = 0;
    [SerializeField]string[] actions = new string[9];

    public string GetAction(int index)
    {
        return actions[index];
    }
    public int GetStoryNumber()
    {
        return storyNumber;
    }
}
