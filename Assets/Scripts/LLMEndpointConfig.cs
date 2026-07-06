using UnityEngine;

[CreateAssetMenu(menuName = "LLM/Endpoint Config", fileName = "LLMEndpointConfig")]
public class LLMEndpointConfig : ScriptableObject
{
    [Tooltip("RunPod sync URL, e.g. https://api.runpod.ai/v2/<id>/runsync")]
    public string endpointUrl;

    [Tooltip("RunPod API key, sent as 'Authorization: Bearer <key>'.")]
    public string apiKey;

    public int maxTokens = 700;
    public float repetitionPenalty = 1.1f;

    [Tooltip("Seconds to wait for the sync endpoint before giving up.")]
    public int timeoutSeconds = 120;
}
