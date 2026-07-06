# LLM Training

`train_lora.ipynb` is the Colab notebook used to LoRA fine-tune the language
model that generates each playthrough's case file (suspects, clues, and
branching action outcomes) for Echoes of the Crime.

At runtime the game calls a hosted endpoint running the fine-tuned model
(see [`Assets/Scripts/LLMStoryClient.cs`](../../Assets/Scripts/LLMStoryClient.cs)
and [`Assets/Scripts/RunPodModels.cs`](../../Assets/Scripts/RunPodModels.cs)),
and [`Assets/Scripts/StoryParser.cs`](../../Assets/Scripts/StoryParser.cs)
turns the raw response into the structured data the game uses.
