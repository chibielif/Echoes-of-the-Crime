using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class LLMStoryClient : MonoBehaviour
{
    [SerializeField] private LLMEndpointConfig config;

    private const string PromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Generate a fictional murder mystery game story including the following details clearly structured in paragraphs:\n\n" +
        "1. Crime Summary (briefly summarize the murder case)\n" +
        "2. Victim (brief description of the victim)\n" +
        "3. Crime Scene (detailed description of how and where the victim was found)\n" +
        "4. Three Suspects (brief descriptions, alibis, and contradictions)\n" +
        "5. At least 5 Clues (some should be misleading)\n" +
        "6. Real Murderer (with clear motive and decisive proof)\n\n" +
        "After generating the story and actions, provide exactly three logical initial actions that a detective player might take at the start of the investigation. Format these actions clearly under the heading \"Initial Player Actions:\".\n\n" +
        "Now, generate the story and actions below:\n\n" +
        "Important: Under the \"Suspects:\" heading, you MUST list exactly 3 suspects, each as its own numbered " +
        "line starting with the suspect's name.\n" +
        "Important: Under the \"Clues:\" heading, you MUST list at least 3 clues, each as its own numbered line, " +
        "each written as a single complete sentence no more than 200 characters.\n" +
        "Important: Each of the 3 initial actions must be a single short sentence, no more than 100 characters. " +
        "This limit applies ONLY to those 3 actions - the Crime Summary, Victim, Crime Scene, Suspects, and Clues " +
        "sections must still be written in full as descriptive paragraphs, not shortened.\n" +
        "Important: You MUST end your response with \"Initial Player Actions:\" followed by exactly 3 numbered actions.\n\n" +
        "### Response:";

    private const string FollowUpPromptTemplate =
        "<s>### Prompt:\n" +
        "You are a detective game designer. Based on this murder mystery story:\n\n" +
        "{0}\n\n" +
        "Provide exactly three logical initial actions that a detective player might take at the start of the investigation. Format them clearly under the heading \"Initial Player Actions:\" as a numbered list.\n" +
        "Important: Each action must be a single short sentence, no more than 100 characters.\n\n" +
        "### Response:";

    public IEnumerator RequestStory(Action<string> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = config.maxTokens,
            temperature = UnityEngine.Random.Range(0.7f, 1.0f),
            top_p = UnityEngine.Random.Range(0.85f, 0.98f),
            repetition_penalty = config.repetitionPenalty,
            seed = UnityEngine.Random.Range(1, 999999)
        };

        yield return SendPrompt(PromptTemplate, samplingParams, onSuccess, onError);
    }

    public IEnumerator RequestInitialActions(string fullStory, Action<string[]> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = 150,
            temperature = 0.8f,
            top_p = 0.95f,
            repetition_penalty = 1.1f
        };

        string prompt = string.Format(FollowUpPromptTemplate, fullStory);

        yield return SendPrompt(prompt, samplingParams,
            text => onSuccess?.Invoke(StoryParser.ParseInitialActions(text)),
            onError);
    }

    private const string SuspectsFollowUpPromptTemplate =
        "<s>### Prompt:\n" +
        "You are a detective game designer. Based on this murder mystery story:\n\n" +
        "{0}\n\n" +
        "List exactly 3 suspects for this case. Format them clearly under the heading \"Suspects:\" as a numbered list, each starting with the suspect's name.\n\n" +
        "### Response:";

    public IEnumerator RequestSuspects(string fullStory, Action<List<string>> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = 150,
            temperature = 0.8f,
            top_p = 0.95f,
            repetition_penalty = 1.1f
        };

        string prompt = string.Format(SuspectsFollowUpPromptTemplate, fullStory);

        yield return SendPrompt(prompt, samplingParams,
            text => onSuccess?.Invoke(StoryParser.ExtractSuspectNames(text)),
            onError);
    }

    private const string CluesFollowUpPromptTemplate =
        "<s>### Prompt:\n" +
        "You are a detective game designer. Based on this murder mystery story:\n\n" +
        "{0}\n\n" +
        "List at least 3 clues for this case (some may be misleading). Format them clearly under the heading \"Clues:\" as a numbered list, each written as a single complete sentence no more than 200 characters.\n\n" +
        "### Response:";

    public IEnumerator RequestClues(string fullStory, Action<List<string>> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = 300,
            temperature = 0.8f,
            top_p = 0.95f,
            repetition_penalty = 1.1f
        };

        string prompt = string.Format(CluesFollowUpPromptTemplate, fullStory);

        yield return SendPrompt(prompt, samplingParams,
            text => onSuccess?.Invoke(StoryParser.ExtractClueList(text)),
            onError);
    }

    private const string ActionPromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Continue the following murder mystery:{0}\n" +
        "Real murderer is not yet known by the player.\n\n" +
        "Known Clues So Far:\n{3}\n\n" +
        "Previous Player Actions and Results:\n{1}\n\n" +
        "Current Player Action: {2}\n\n" +
        "Describe the result of the player's action briefly in no more than 2 sentences.\n" +
        "After generating the result, provide exactly one logical action that a detective player might take after seeing the result. Format the action clearly after the heading \"New Player Action:\".\n" +
        "Then, clearly reveal if a new clue is found by writing: \"New clue discovered:\" (write 'None' if no new clue).\n" +
        "Do NOT reveal the murderer to the player.\n" +
        "Important: Your result MUST be no more than 300 characters.\n" +
        "Important: If a new clue is found, it must be a single complete sentence no more than 200 characters.\n" +
        "Important: The new action must be a single short sentence, no more than 100 characters.\n" +
        "Important: You MUST end your response with the line:\n### End\n\n" +
        "Now, generate the result and action below:\n\n" +
        "### Response:";

    private const string FinalActionPromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Continue the following murder mystery:{0}\n" +
        "Real murderer is not yet known by the player.\n\n" +
        "Known Clues So Far:\n{3}\n\n" +
        "Previous Player Actions and Results:\n{1}\n\n" +
        "Current Player Action: {2}\n\n" +
        "Describe the result of the player's action briefly in no more than 2 sentences.\n" +
        "Then, clearly reveal if a new clue is found by writing: \"New clue discovered:\" (write 'None' if no new clue).\n" +
        "Do NOT reveal the murderer to the player.\n" +
        "Important: Your result MUST be no more than 300 characters.\n" +
        "Important: If a new clue is found, it must be a single complete sentence no more than 200 characters.\n" +
        "Important: You MUST end your response with the line:\n### End\n\n" +
        "Now, generate the result and action below:\n\n" +
        "### Response:";

    public IEnumerator RequestActionResult(string storyContext, string previousActionsAndResults, string currentAction,
        bool isFinalStep, string knownClues, Action<ActionResponse> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = 400,
            temperature = UnityEngine.Random.Range(0.7f, 1.0f),
            top_p = UnityEngine.Random.Range(0.85f, 0.98f),
            repetition_penalty = 1.1f,
            seed = UnityEngine.Random.Range(1, 999999)
        };

        string template = isFinalStep ? FinalActionPromptTemplate : ActionPromptTemplate;
        string prompt = string.Format(template, storyContext, previousActionsAndResults, currentAction, knownClues);

        yield return SendPrompt(prompt, samplingParams,
            text => onSuccess?.Invoke(StoryParser.ParseActionResponse(text)),
            onError);
    }

    private IEnumerator SendPrompt(string prompt, SamplingParams samplingParams, Action<string> onSuccess, Action<string> onError)
    {
        var requestBody = new RunPodRequest
        {
            input = new RunPodInput
            {
                prompt = prompt,
                sampling_params = samplingParams
            }
        };

        string json = JsonUtility.ToJson(requestBody);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        using (var request = new UnityWebRequest(config.endpointUrl, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + config.apiKey);
            request.timeout = config.timeoutSeconds;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"{request.result}: {request.error}");
                yield break;
            }

            RunPodResponse response;
            try
            {
                response = JsonUtility.FromJson<RunPodResponse>(request.downloadHandler.text);
            }
            catch (Exception e)
            {
                onError?.Invoke("Failed to parse response: " + e.Message);
                yield break;
            }

            string text = ExtractText(response);

            if (string.IsNullOrEmpty(text))
            {
                // RunPod's /runsync has its own internal wait limit (~90s) separate from
                // our client-side timeout. If the queue+execution time runs long, it can
                // return early with a "still processing" status instead of the actual
                // output, even though the job keeps running and finishes moments later.
                // Fall back to polling the job's own status endpoint in that case.
                if (response != null && !string.IsNullOrEmpty(response.id) && IsPendingStatus(response.status))
                {
                    yield return PollForCompletion(response.id, onSuccess, onError);
                    yield break;
                }

                onError?.Invoke("Response contained no text.");
                yield break;
            }

            onSuccess?.Invoke(text);
        }
    }

    private IEnumerator PollForCompletion(string jobId, Action<string> onSuccess, Action<string> onError)
    {
        string statusUrl = BuildStatusUrl(jobId);
        if (statusUrl == null)
        {
            onError?.Invoke("Job is still processing but no status URL could be determined.");
            yield break;
        }

        const float pollInterval = 3f;
        const float maxWait = 120f;
        float elapsed = 0f;

        while (elapsed < maxWait)
        {
            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;

            using (var statusRequest = UnityWebRequest.Get(statusUrl))
            {
                statusRequest.SetRequestHeader("Authorization", "Bearer " + config.apiKey);
                yield return statusRequest.SendWebRequest();

                if (statusRequest.result != UnityWebRequest.Result.Success)
                    continue;

                RunPodResponse response;
                try
                {
                    response = JsonUtility.FromJson<RunPodResponse>(statusRequest.downloadHandler.text);
                }
                catch (Exception)
                {
                    continue;
                }

                string text = ExtractText(response);
                if (!string.IsNullOrEmpty(text))
                {
                    onSuccess?.Invoke(text);
                    yield break;
                }

                if (response != null && IsFailedStatus(response.status))
                {
                    onError?.Invoke("Job failed with status: " + response.status);
                    yield break;
                }
            }
        }

        onError?.Invoke("Timed out waiting for a delayed response.");
    }

    private string BuildStatusUrl(string jobId)
    {
        const string suffix = "/runsync";
        if (config.endpointUrl == null || !config.endpointUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        string basePart = config.endpointUrl.Substring(0, config.endpointUrl.Length - suffix.Length);
        return $"{basePart}/status/{jobId}";
    }

    private static string ExtractText(RunPodResponse response)
    {
        return response?.output != null && response.output.Length > 0
            && response.output[0].choices != null && response.output[0].choices.Length > 0
            && response.output[0].choices[0].tokens != null && response.output[0].choices[0].tokens.Length > 0
            ? response.output[0].choices[0].tokens[0]
            : null;
    }

    private static bool IsPendingStatus(string status) =>
        string.Equals(status, "IN_QUEUE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedStatus(string status) =>
        string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
}
