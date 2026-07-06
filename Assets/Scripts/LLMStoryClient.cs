using System;
using System.Collections;
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
        "Important: You MUST end your response with \"Initial Player Actions:\" followed by exactly 3 numbered actions.\n\n" +
        "### Response:";

    private const string FollowUpPromptTemplate =
        "<s>### Prompt:\n" +
        "You are a detective game designer. Based on this murder mystery story:\n\n" +
        "{0}\n\n" +
        "Provide exactly three logical initial actions that a detective player might take at the start of the investigation. Format them clearly under the heading \"Initial Player Actions:\" as a numbered list.\n\n" +
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

    private const string ActionPromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Continue the following murder mystery:{0}\n" +
        "Real murderer is not yet known by the player.\n\n" +
        "Previous Player Actions and Results:\n{1}\n\n" +
        "Current Player Action: {2}\n\n" +
        "Describe the result of the player's action briefly in no more than 2 sentences.\n" +
        "After generating the result, provide exactly one logical action that a detective player might take after seeing the result. Format the action clearly after the heading \"New Player Action:\".\n" +
        "Then, clearly reveal if a new clue is found by writing: \"New clue discovered:\" (write 'None' if no new clue).\n" +
        "Do NOT reveal the murderer to the player.\n" +
        "Important: You MUST end your response with the line:\n### End\n\n" +
        "Now, generate the result and action below:\n\n" +
        "### Response:";

    private const string FinalActionPromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Continue the following murder mystery:{0}\n" +
        "Real murderer is not yet known by the player.\n\n" +
        "Previous Player Actions and Results:\n{1}\n\n" +
        "Current Player Action: {2}\n\n" +
        "Describe the result of the player's action briefly in no more than 2 sentences.\n" +
        "Then, clearly reveal if a new clue is found by writing: \"New clue discovered:\" (write 'None' if no new clue).\n" +
        "Do NOT reveal the murderer to the player.\n" +
        "Important: You MUST end your response with the line:\n### End\n\n" +
        "Now, generate the result and action below:\n\n" +
        "### Response:";

    public IEnumerator RequestActionResult(string storyContext, string previousActionsAndResults, string currentAction,
        bool isFinalStep, Action<ActionResponse> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = 300,
            temperature = UnityEngine.Random.Range(0.7f, 1.0f),
            top_p = UnityEngine.Random.Range(0.85f, 0.98f),
            repetition_penalty = 1.1f,
            seed = UnityEngine.Random.Range(1, 999999)
        };

        string template = isFinalStep ? FinalActionPromptTemplate : ActionPromptTemplate;
        string prompt = string.Format(template, storyContext, previousActionsAndResults, currentAction);

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

            string text = response?.output != null && response.output.Length > 0
                && response.output[0].choices != null && response.output[0].choices.Length > 0
                && response.output[0].choices[0].tokens != null && response.output[0].choices[0].tokens.Length > 0
                ? response.output[0].choices[0].tokens[0]
                : null;

            if (string.IsNullOrEmpty(text))
            {
                onError?.Invoke("Response contained no text.");
                yield break;
            }

            onSuccess?.Invoke(text);
        }
    }
}
