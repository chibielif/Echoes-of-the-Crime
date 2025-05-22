using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Output", fileName ="New Output" )]
public class OutputSO : ScriptableObject
{
    [SerializeField]int outputNumber;
    [TextArea(2,15)]
    [SerializeField] string[] outputs = new string[10];

    public string GetOutput(int index)
    {
        return outputs[index];
    }
}
