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
        "Important: Under the \"Clues:\" heading, make sure AT LEAST ONE clue relates to EACH of the three " +
        "suspects specifically - every suspect must have something for the detective to investigate, not just " +
        "the real murderer.\n" +
        "Important: Each of the 3 initial actions must be a single short sentence, no more than 100 characters. " +
        "This limit applies ONLY to those 3 actions - the Crime Summary, Victim, Crime Scene, Suspects, and Clues " +
        "sections must still be written in full as descriptive paragraphs, not shortened.\n" +
        "Important: The three \"Initial Player Actions\" MUST correspond one-to-one, IN ORDER, with the three " +
        "suspects listed under \"Suspects:\" above - the first action investigates the first suspect, the second " +
        "action investigates the second suspect, and the third action investigates the third suspect.\n" +
        "Important: You MUST end your response with \"Initial Player Actions:\" followed by exactly 3 numbered actions.\n" +
        "Important: Never invent a personal name for the detective/player character anywhere in this story - " +
        "always refer to them as \"you\".\n" +
        "Important: The \"Real Murderer:\" section MUST include a one-sentence motive (why they did it) and one " +
        "specific piece of decisive proof - never just a name alone. The decisive proof MUST correspond to one of " +
        "the clues you already listed under \"Clues:\" above - do not invent a new piece of evidence the player " +
        "never had access to.\n" +
        "Important: Never address the player directly or ask a question like \"What would you like me to say?\" " +
        "anywhere in this response - go straight from the Clues section to the Real Murderer section and then " +
        "the Initial Player Actions, with no question or dialogue options in between.\n\n" +
        "### Response:";

    private const string FollowUpPromptTemplate =
        "<s>### Prompt:\n" +
        "You are a detective game designer. Based on this murder mystery story:\n\n" +
        "{0}\n\n" +
        "Provide exactly three logical initial actions that a detective player might take at the start of the investigation. Format them clearly under the heading \"Initial Player Actions:\" as a numbered list.\n" +
        "Important: The three actions MUST correspond one-to-one, IN ORDER, with the three suspects already " +
        "listed in the story above - the first action investigates the first suspect, the second investigates " +
        "the second suspect, and the third investigates the third suspect.\n" +
        "Important: Each action must be a single short sentence, no more than 100 characters.\n" +
        "Important: Refer to the detective/player as \"you\" - never invent a personal name for them.\n\n" +
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
            text => onSuccess?.Invoke(StoryParser.ParseSuspectsFollowUp(text)),
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
            text => onSuccess?.Invoke(StoryParser.ParseCluesFollowUp(text)),
            onError);
    }

    private const string RealMurdererFollowUpPromptTemplate =
        "<s>### Prompt:\n" +
        "You are a detective game designer. Based on this murder mystery story:\n\n" +
        "{0}\n\n" +
        "Name the real murderer for this case. Format your answer clearly under the heading \"Real Murderer:\".\n" +
        "Important: You MUST include a one-sentence motive (why they did it) and one specific piece of decisive " +
        "proof - never write just a name alone.\n" +
        "Important: The decisive proof MUST correspond to one of the clues already listed in the story above - " +
        "do not invent a new piece of evidence the player never had access to.\n\n" +
        "Now, name the murderer with a motive and decisive proof that matches one of the clues already listed " +
        "above:\n\n" +
        "### Response:";

    public IEnumerator RequestRealMurderer(string fullStory, Action<string> onSuccess, Action<string> onError)
    {
        if (config == null || string.IsNullOrEmpty(config.endpointUrl))
        {
            onError?.Invoke("LLM endpoint config is not assigned.");
            yield break;
        }

        var samplingParams = new SamplingParams
        {
            max_tokens = 200,
            temperature = 0.8f,
            top_p = 0.95f,
            repetition_penalty = 1.1f,
            seed = UnityEngine.Random.Range(1, 999999)
        };

        string prompt = string.Format(RealMurdererFollowUpPromptTemplate, fullStory);

        yield return SendPrompt(prompt, samplingParams,
            text => onSuccess?.Invoke(StoryParser.ParseRealMurderer(text)),
            onError);
    }

    private const string ActionPromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Continue the following murder mystery:{0}\n" +
        "SECRET (do NOT reveal to the player): the real murderer - including their identity, motive, and " +
        "decisive proof - is: {4}. Stay consistent with ALL of this always.\n\n" +
        "THIS INVESTIGATION THREAD IS FOCUSED ONLY ON SUSPECT: {5}. The result, any new clue, and the " +
        "next action must all relate specifically to {5} - do not shift focus to a different suspect. If {5} " +
        "is NOT the real murderer described above, keep the result and clue neutral or only mildly suspicious " +
        "- do not make {5} look definitively guilty. Only write strongly incriminating content if {5} truly " +
        "is the real murderer.\n\n" +
        "Known Clues So Far (for this thread):\n{3}\n\n" +
        "Previous Actions and Results (for THIS thread only):\n{1}\n\n" +
        "THE DETECTIVE'S CURRENT ACTION (about {5}): \"{2}\"\n\n" +
        "Describe the result of ONLY that action, briefly in no more than 2 sentences. Do not describe a " +
        "different action or continue a previous one.\n" +
        "After generating the result, provide exactly one logical action that a detective player might take next to keep investigating {5}. Format the action clearly after the heading \"New Player Action:\".\n" +
        "Then, clearly reveal if a new clue about {5} is found by writing: \"New clue discovered:\" followed by AT MOST ONE " +
        "single sentence (write 'None' if no new clue). Never list more than one clue here.\n" +
        "Do NOT reveal the murderer to the player.\n" +
        "Important: Your result MUST be no more than 300 characters.\n" +
        "Important: If a new clue is found, it must be exactly ONE single complete sentence, no more than 200 characters.\n" +
        "Important: The new action must be a single short sentence, no more than 100 characters.\n" +
        "Important: Everything you write here MUST be about {5} specifically - never a different suspect.\n" +
        "Important: Refer to the detective performing this investigation as \"you\" - never invent a personal " +
        "name for them (e.g. never \"Detective Mark\" or similar).\n" +
        "Important: Never ask the player a question or offer them multiple-choice options (e.g. \"What clue " +
        "does this reveal? A. ... B. ...\") - state the result and the new clue directly, as narration, never " +
        "as a question with answer choices.\n" +
        "Important: You MUST end your response with the line:\n### End\n\n" +
        "Reminder (do NOT reveal to the player): the real murderer is {4}.\n" +
        "Reminder: refer to the detective as \"you\", never by an invented name.\n" +
        "Reminder: never ask a question or offer multiple-choice options - narrate the result and clue directly.\n" +
        "Now, write the result of this exact action about {5}: \"{2}\"\n\n" +
        "### Response:";

    private const string FinalActionPromptTemplate =
        "<s>### Prompt:\n" +
        "You are an intelligent mystery writer. Continue the following murder mystery:{0}\n" +
        "SECRET (do NOT reveal to the player): the real murderer - including their identity, motive, and " +
        "decisive proof - is: {4}. Stay consistent with ALL of this always.\n\n" +
        "THIS INVESTIGATION THREAD IS FOCUSED ONLY ON SUSPECT: {5}. The result and any new clue must " +
        "relate specifically to {5} - do not shift focus to a different suspect. If {5} is NOT the real " +
        "murderer described above, keep the result and clue neutral or only mildly suspicious - do not make " +
        "{5} look definitively guilty. Only write strongly incriminating content if {5} truly is the real " +
        "murderer.\n\n" +
        "Known Clues So Far (for this thread):\n{3}\n\n" +
        "Previous Actions and Results (for THIS thread only):\n{1}\n\n" +
        "THE DETECTIVE'S CURRENT ACTION (about {5}): \"{2}\"\n\n" +
        "Describe the result of ONLY that action, briefly in no more than 2 sentences. Do not describe a " +
        "different action or continue a previous one.\n" +
        "Then, clearly reveal if a new clue about {5} is found by writing: \"New clue discovered:\" followed by AT MOST ONE " +
        "single sentence (write 'None' if no new clue). Never list more than one clue here.\n" +
        "Do NOT reveal the murderer to the player.\n" +
        "Important: Your result MUST be no more than 300 characters.\n" +
        "Important: If a new clue is found, it must be exactly ONE single complete sentence, no more than 200 characters.\n" +
        "Important: Everything you write here MUST be about {5} specifically - never a different suspect.\n" +
        "Important: Refer to the detective performing this investigation as \"you\" - never invent a personal " +
        "name for them (e.g. never \"Detective Mark\" or similar).\n" +
        "Important: Never ask the player a question or offer them multiple-choice options (e.g. \"What clue " +
        "does this reveal? A. ... B. ...\") - state the result and the new clue directly, as narration, never " +
        "as a question with answer choices.\n" +
        "Important: You MUST end your response with the line:\n### End\n\n" +
        "Reminder (do NOT reveal to the player): the real murderer is {4}.\n" +
        "Reminder: refer to the detective as \"you\", never by an invented name.\n" +
        "Reminder: never ask a question or offer multiple-choice options - narrate the result and clue directly.\n" +
        "Now, write the result of this exact action about {5}: \"{2}\"\n\n" +
        "### Response:";

    public IEnumerator RequestActionResult(string storyContext, string previousActionsAndResults, string currentAction,
        bool isFinalStep, string knownClues, string realMurderer, string suspectName, Action<ActionResponse> onSuccess, Action<string> onError)
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

        // The initial story doesn't always yield a clean "Real Murderer:" section (Rule #1
        // in CLAUDE.md - this model drifts from format constantly) - fall back to a neutral
        // instruction rather than formatting an empty string into the prompt.
        string murderer = string.IsNullOrWhiteSpace(realMurderer)
            ? "not clearly specified - stay consistent with the story and clues as already written"
            : realMurderer;

        // Likewise for the suspect this branch is bound to, in the rare case fewer than
        // 3 real suspect names came through at all (GameManager already falls back to
        // "Suspect N" per-branch, so this is only hit if even that's somehow empty).
        string suspect = string.IsNullOrWhiteSpace(suspectName) ? "the suspect being investigated" : suspectName;

        string template = isFinalStep ? FinalActionPromptTemplate : ActionPromptTemplate;
        string prompt = string.Format(template, storyContext, previousActionsAndResults, currentAction, knownClues, murderer, suspect);

        yield return SendPrompt(prompt, samplingParams,
            text =>
            {
                ActionResponse parsed = StoryParser.ParseActionResponse(text);

                // Confirmed in an actual playthrough: GameManager's generic "Investigate
                // further." fallback showed up in place of a real next action - the only
                // way that happens is NewAction coming back null, i.e. this response never
                // matched a recognizable action heading at all (see the permissive-regex
                // fix in ParseActionResponse). Not every non-final step's response needs to
                // repro this again, but if it does, log the raw text the same way
                // GameSession.SetRawStory already does for the main story - otherwise this
                // class of bug can only ever be diagnosed by catching it live.
                if (!isFinalStep && string.IsNullOrEmpty(parsed.NewAction))
                    Debug.LogWarning("Action response had no usable 'New Player Action'. Raw LLM response:\n" + text);

                // Confirmed in an actual playthrough: a NON-empty NewAction can still be
                // garbage - the model echoed a paraphrase of its own prompt instructions
                // instead of a real action (the empty-check above can't catch this, since
                // the field wasn't empty). Check Result and NewClue too, since the same
                // echoing could in principle land in either of those fields instead.
                if (StoryParser.EchoedInstructionMarker.IsMatch(parsed.NewAction ?? "")
                    || StoryParser.EchoedInstructionMarker.IsMatch(parsed.Result ?? "")
                    || StoryParser.EchoedInstructionMarker.IsMatch(parsed.NewClue ?? ""))
                    Debug.LogWarning("Action response looks like it echoed prompt instructions instead of real content. Raw LLM response:\n" + text);

                onSuccess?.Invoke(parsed);
            },
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
