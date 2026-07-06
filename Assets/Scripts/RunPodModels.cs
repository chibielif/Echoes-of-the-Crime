using System;

[Serializable]
public class RunPodRequest
{
    public RunPodInput input;
}

[Serializable]
public class RunPodInput
{
    public string prompt;
    public SamplingParams sampling_params;
}

[Serializable]
public class SamplingParams
{
    public int max_tokens;
    public float temperature;
    public float top_p;
    public float repetition_penalty;
    public int seed;
}

[Serializable]
public class RunPodResponse
{
    public string id;
    public string status;
    public RunPodOutput[] output;
}

[Serializable]
public class RunPodOutput
{
    public RunPodChoice[] choices;
}

[Serializable]
public class RunPodChoice
{
    public string[] tokens;
}
