# Echoes of the Crime — Project Guide

Unity murder-mystery game. Cases are generated live by an LLM (a LoRA fine-tune on
base **Mistral-7B-v0.1**, served via a RunPod serverless `/runsync` endpoint) instead
of being hand-authored. This file captures the architecture and the hard-won rules
from building the LLM integration — read it before touching any of the LLM/parsing
scripts.

## Scene flow

```
MainMenu --[NewGame button]--> Loading --(fetch story)--> Story --[Continue]--> Game
```

- **MainMenu.unity**: `NewGame` button (on `SceneChanger`) calls `GoToLoading.GoToLoadingScene()`.
- **Loading.unity**: `LoadingController` fires the main story request, then any needed
  follow-up requests (see below), then loads Story immediately — it does not wait for
  follow-ups to finish.
- **Story.unity**: `StoryDisplay` renders Crime Summary/Victim/Crime Scene/Suspects/Clues
  in a scroll view. `GoToGameplay`'s Continue button is disabled until
  `GameSession.HasInitialActions` is true.
- **Game.unity**: `GameManager` drives the 3-branch action-button gameplay loop and the
  Clues panel; `LLMStoryClient` (own component on the Game scene) sends per-turn requests.

## Core scripts

| File | Responsibility |
|---|---|
| `LLMEndpointConfig.cs` | ScriptableObject schema (URL, API key, maxTokens, repetitionPenalty, timeout). The actual asset instance (`Assets/Config/LLMEndpointConfig.asset`) is **gitignored** — it holds the real API key. Never commit it. |
| `RunPodModels.cs` | JSON request/response DTOs for RunPod's `/runsync` + `/status/{id}` shape. |
| `LLMStoryClient.cs` | All prompt templates + `UnityWebRequest` calls. One method per request type (see table below). |
| `StoryParser.cs` | All text parsing/cleanup. This is where nearly every model-inconsistency fix lives. |
| `GameSession.cs` | Static holder for the current story/actions/suspects/clues. Survives scene loads for free (static fields persist for the app's lifetime; no `DontDestroyOnLoad` needed). |
| `LoadingController.cs` | Orchestrates the main + follow-up requests, using `DontDestroyOnLoad` to keep fetching in the background after the scene changes. |
| `GameManager.cs` | Game scene gameplay loop: 3 suspect-bound action branches (3 steps each), clue list, guess-the-murderer handoff. Also drives the Clues/Suspects panel toggle (see below). |
| `AudioManager.cs` | Singleton (`DontDestroyOnLoad`, self-destroys any duplicate) that plays a looping theme song across every scene. Lives on a plain `AudioManager` GameObject in MainMenu.unity (the entry scene) so it exists from app start. Routes through `MainMixer.mixer`'s existing `Music` group (`fileID: -7594497713094770852`), the same group `SettingsMenu.SetMusicVolume` already controls - so the existing volume slider works on it for free. `themeSong` is left unassigned in the scene; drop an `AudioClip` onto it in the Inspector to enable playback (it no-ops silently until then). |
| `SettingsMenu.cs` | Resolution/fullscreen/volume controls for the Settings scene. `musicSlider` is synced to the mixer's *actual* current `MusicVolume` on `Start()` (`AudioMixer.GetFloat` → `Slider.SetValueWithoutNotify`) rather than showing whatever value was last left on it in the Editor - otherwise the slider could silently disagree with the volume `AudioManager.cs`/a previous Settings visit actually set. `MusicVolume`/`SFXVolume` are raw dB values (mixer group volume range, -80 to 20) passed straight through with no linear conversion - confirmed against the `MusicSlider`'s own configured `m_MinValue: -80`/`m_MaxValue: 20` in Settings.unity, so the slider's value already *is* the dB value, no scaling needed. |

## LLM requests and sampling params

| Request | Purpose | temperature/top_p/seed | max_tokens |
|---|---|---|---|
| `RequestStory` | Full case generation | **randomized** each call (temp 0.7–1.0, top_p 0.85–0.98, seed random) | `config.maxTokens` (1000) |
| `RequestActionResult` | Per-turn result + next action + clue | **randomized** each call | 400 |
| `RequestInitialActions` | Follow-up if story came back with no usable actions | fixed (0.8 / 0.95), seed not set (defaults to 0) | 150 |
| `RequestSuspects` | Follow-up if story came back with no usable suspects | fixed (0.8 / 0.95), seed not set | 150 |
| `RequestClues` | Follow-up if story came back with no usable clues | fixed (0.8 / 0.95), seed not set | 300 |
| `RequestRealMurderer` | Follow-up if story came back with no usable Real Murderer | fixed (0.8 / 0.95), **seed randomized** | 200 |

Note: the first three follow-up requests never set `seed`, so it's sent as a literal
`0` every time. If RunPod treats that as a real fixed seed rather than "unset," these
follow-ups could produce near-identical output across different games. Not yet fixed —
worth doing if follow-up staleness becomes noticeable. `RequestRealMurderer` was written
later and does set a random seed - no reason to copy a known bug into new code, but this
does mean it's the odd one out among the four follow-ups until the other three are fixed.

`RequestStory`'s `max_tokens` was raised from 700 to 1000 after an actual playthrough hit
a hard cutoff mid-response: the randomized temperature (0.7–1.0)/top_p (0.85–0.98) rolled
toward unusually verbose prose that time (4 dense paragraphs each for Crime Summary/
Victim/Crime Scene, versus one paragraph each in typical runs), consuming the entire
budget before the model reached the back half of its own response - it cut off mid-name
in the Suspects list ("Chloe Everett, a freel—"), with Clues/Real Murderer/Initial Player
Actions never generated at all (not mis-parsed - genuinely absent from the raw text).
Deliberately fixed by raising the token budget rather than asking the model to be more
concise in those sections - the prompt already explicitly requires them to stay "full...
descriptive paragraphs, not shortened" (see the `ActionPromptTemplate`/`PromptTemplate`
"Important:" reinforcement for the analogous action-length constraint), so asking for
brevity there would fight that existing design intent. This class of failure can still
happen on an especially verbose roll even at 1000 - if it recurs, the same fix (raise the
budget further) is preferred over trimming section length requirements.

## Rule #1: never trust the model to follow instructions — always add both a prompt nudge AND a code-side safety net

This model (small LoRA on a **base, non-instruct** Mistral-7B) drifts from the
requested format constantly and unpredictably. Every fix in this project follows the
same two-layer pattern:
1. **Prompt-level nudge** — tell it explicitly what's wrong ("must be under 100
   characters", "list under a `Suspects:` heading", etc.). This helps but never
   guarantees compliance.
2. **Code-side hard clamp** — regex/parsing logic that enforces the constraint
   regardless of what the model actually did.

Never ship a fix that's prompt-only. The model *will* eventually violate it.

## Rule #2: for a small/base model, put critical instructions at the very end, right before the response starts

Prompts here have grown long (full story + known clues + full cross-branch action
history). A 7B base-model LoRA has weak long-context attention. The fix that reliably
helps: **repeat the single most important instruction as literally the last line
before `### Response:`**. See `ActionPromptTemplate`/`FinalActionPromptTemplate` in
`LLMStoryClient.cs` for the pattern ("THE DETECTIVE'S CURRENT ACTION" labeled
mid-prompt, then repeated verbatim right before generation).

Nothing in any prompt ever assigns the detective/player character a name - every template
just says "the detective"/"detective player" generically - so the model is free to (and
does) invent a different one on its own each time. Confirmed across separate playthroughs:
"Detective Matt" in one, "Detective Mark" in another. Not a parsing bug (nothing to fix in
`StoryParser.cs`), but a player-facing inconsistency by request the player should always be
addressed as "you" instead. `PromptTemplate`, `FollowUpPromptTemplate`,
`ActionPromptTemplate`, and `FinalActionPromptTemplate` all now include an "Important:"
line requiring this; the two per-turn action templates additionally repeat it as a
`"Reminder:"` in the final line before `### Response:` (Rule #2 above), since that's where
the confirmed instances actually happened (the Result narration, not the main story or the
initial-actions list). Prompt-only fix - no code-side clamp exists (or is planned) for
this, same reasoning as the motive/decisive-proof reinforcement: there's no reliable way to
detect and replace an arbitrary invented name in code without risking a false-positive
match against a real suspect's name elsewhere in the same text.

## Rule #3: a follow-up's transport success isn't content success - validate before treating it as done

`LoadingController`'s follow-up success handlers (`OnActionsSuccess`/`OnSuspectsSuccess`/
etc.) only ever retried on an actual network/RunPod *error* - a successful HTTP response
whose content still didn't parse into anything usable was accepted as-is and the fetch
marked finished, with no path back to a retry. Confirmed in an actual playthrough:
`OnActionsSuccess` accepted whatever `RequestInitialActions` returned with no check at
all, so when that follow-up's response didn't parse into 3 valid actions,
`GameSession.HasInitialActions` stayed false **permanently** - the Continue button
(`GoToGameplay.Update()`) has no other path to becoming interactable, so it just silently
never did, with no error message anywhere telling the player something had gone wrong.
Fixed by having `OnActionsSuccess` check `GameSession.HasInitialActions` after setting the
result, and if still false, calling `OnActionsError` directly - reusing the exact same
retry-then-bail-to-MainMenu path a transport failure already goes through, rather than
adding a second, parallel failure path. This is Rule #1 ("never trust the model to follow
instructions") applied one layer further out than usual - it's not enough to make parsing
defensive against a bad response, the *caller* also has to treat "parsed successfully but
got nothing usable" the same as "the request itself failed," or a bad-but-HTTP-200
response becomes a silent permanent dead end instead of a retry. `OnSuspectsSuccess`/
`OnCluesSuccess`/`OnMurdererSuccess` weren't changed to match - none of them gate scene
progression the way actions do (suspects/clues/murderer all have neutral fallback
behavior elsewhere - generic "Suspect N" labels, an empty Clues panel, an indeterminate
guess resolution), so a similarly invalid response there degrades gracefully rather than
getting the player stuck.

The mirror-image gap existed on the *transport failure* side too: `LoadingController.
OnError` (the main story request's error handler - the one request with no retry, since
it's the very first thing that happens and there's nothing to fall back to) used to just
set `statusText` to the error and stop, with no further action at all - by request, RunPod
returning the job as delayed/timed-out or `CANCELLED` left the player permanently stuck on
the Loading screen with an error message and nowhere to go. Fixed by showing "You're being
directed to Main Menu..." alongside the error and starting a 10-second countdown
(`ReturnToMainMenuAfterDelay`) that then calls `SceneManager.LoadScene("MainMenu")` -
giving the player a moment to read what went wrong before being moved on, rather than
either stranding them silently or yanking them away with zero warning.

## Catalog of model quirks `StoryParser.cs` defends against

Do not re-litigate these — they're all confirmed, reproduced issues, not
hypothetical:
- Skips headers entirely (Crime Summary/Victim/Crime Scene missing, or *all* headers
  missing and the whole thing is bare prose) → `Parse()` falls back to using the raw
  text / `Title` as Crime Summary so nothing is silently discarded.
- Formats every section as a bare `####`-prefixed markdown header with **no trailing
  colon at all** (e.g. `#### Crime Summary` alone on its own line, body text starting on
  the next line) instead of `**Crime Summary:**` or `Crime Summary:`. Confirmed in an
  actual playthrough: `CandidateHeadingLine` required a colon in *every* one of its
  alternatives, so none of these 6 headers were recognized - only `Initial Player
  Actions:` survived, purely because that one specific header happened to still have a
  colon. Every other section's content silently fell into `Title` (never displayed) and
  got promoted into `CrimeSummary` as one giant undifferentiated blob (Crime Summary +
  Victim + Crime Scene + the numbered Suspects list all run together with no separation),
  while Victim/CrimeScene/Suspects/RealMurderer all came back empty - exactly triggering
  `GameSession.IsMissingExpectedFields`'s warning. Fixed by adding a 4th alternative to
  `CandidateHeadingLine` for a standalone `#`-prefixed line with no colon - safe to treat
  as a boundary unconditionally (no need for the usual known/standalone check) since a
  `#`-prefixed line's content is always discarded by `StripMarkers` regardless of whether
  it's recognized.
- The per-turn action header (`"New Player Action:"`) is matched by a single rigid exact
  phrase, unlike the already-permissive "New clue discovered:" regex - confirmed in an
  actual playthrough: two of three action branches showed the generic `FallbackNewAction`
  ("Investigate further.") after their first turn, which only happens when `NewAction`
  comes back null, i.e. the model varied this heading's wording (as it already does for
  every other heading in this project) and the rigid match missed it entirely. Fixed by
  making the pattern permissive - `(?:new|next)\s+(?:player'?s?\s+)?action\s*:` - matching
  "New Action:", "Next Action:", "New Player's Action:", etc., mirroring the clue header's
  existing treatment. `LLMStoryClient.RequestActionResult` also now logs the raw response
  whenever a non-final step's `NewAction` still comes back empty despite this, so any
  further wording variant this doesn't catch gets captured automatically next time instead
  of only being diagnosable by catching it live.
- A *separate*, still not fully root-caused issue from the same playthrough (don't
  conflate the two): `NewAction` came back **non-empty** but was garbage - the model
  echoed a paraphrase of its own prompt instructions instead of writing a real action:
  `") and reveal a new clue about Mr. Vincent Reeves (if found) by writing "New clue
  discovered:")."` Confirmed to have landed specifically in the `NewAction` field (not
  `Result`). The empty-`NewAction` fix above can't catch this - the field wasn't empty.
  Working hypothesis, NOT yet confirmed without the full raw response: `ParseActionResponse`
  uses a single `Regex.Match` (first occurrence only) for both `actionMatch` and
  `clueMatch`, and assumes the model writes them in the prompt's requested order (Result →
  New Player Action → New clue discovered). If the model flips that order - writing the
  genuine "New clue discovered:" *before* "New Player Action:" this time - the boundary
  logic (`end = clueMatch.Index > actionMatch.Index ? clueMatch.Index : raw.Length`) falls
  through to `raw.Length`, so `actionBlock` spans everything after "New Player Action:" to
  the end of the response with nothing to bound it - which is exactly where a
  self-referential echo like this would end up if the model devolved into one instead of
  writing a real action at that point. Not fixed yet since this mechanism is inferred, not
  confirmed from an actual captured raw response - guessing at a fix here risks solving
  the wrong problem (per the standing rule: verify against real raw text before calling
  anything done). Instead, added a diagnostic-only safety net: `StoryParser.
  EchoedInstructionMarker` flags known meta-instruction phrases (`"new action:"`, `"new
  clue discovered"`, `"decisive proof"`, `"by writing"`) that should never legitimately
  appear in genuine narrative content - `LLMStoryClient.RequestActionResult` checks
  `NewAction`/`Result`/`NewClue` against it and logs the raw response when matched. Until
  this fires, per-turn raw responses aren't visible anywhere (unlike the main story, which
  already logs via `GameSession.SetRawStory`) - this is the first source of that visibility
  for per-turn action results. Revisit the boundary-logic hypothesis above once a real raw
  response confirms (or rules out) it.
- The Suspects/Clues follow-ups (`LLMStoryClient.RequestSuspects`/`RequestClues`) used to
  call `ExtractSuspectNames`/`ExtractClueList` directly on the **full raw response**,
  heading included - unlike `ParseInitialActions`, which already slices past its own
  `"Initial Player Actions:"` heading first. Confirmed in an actual playthrough: when the
  model answered in paragraph form (no numbered/bulleted list), `ExtractSuspectEntries`'s
  paragraph-fallback treated the response's own leading `"Suspects:"` line as if it were
  the first suspect's own paragraph, extracting the literal word `"Suspects"` as a "name" -
  which then displayed as a bare, meaningless guess-screen button in place of a real
  suspect (with the *actual* murderer dropped entirely, since only 3 slots exist). Fixed
  with new `StoryParser.ParseSuspectsFollowUp`/`ParseCluesFollowUp`, which locate and slice
  past the `"Suspects:"`/`"Clues:"` heading before extracting (falling back to the whole
  response if the model skipped the heading, same leniency as `ParseRealMurderer`) -
  `LLMStoryClient` now calls these instead of the raw extractors directly.
- Separately, `ExtractName`'s `CapitalizedNameRun` tier truncates at a title
  abbreviation's own period instead of continuing on to the actual name - e.g. "Ms. Ruth
  Pearson is a regular customer..." (no early comma to trigger the comma-tier) returned
  bare `"Ms"` as the "name," because the character class backing that capitalized-word-run
  doesn't include `.`, so it stops dead right after "Ms" instead of reaching "Ruth
  Pearson" just past the period - even though the next tier, `TitledNameAnywhere`, would
  have matched the whole thing correctly. Confirmed via direct testing against real
  suspect text from an actual playthrough (this is exactly the paragraph-fallback path
  above - plain prose with no comma-led "Name, description" format). Titles with no
  trailing period ("Miss", "Officer") aren't affected. Fixed by rejecting a bare title
  abbreviation (`TitleAbbreviations`) from `CapitalizedNameRun`'s tier the same way a bare
  `NonNameWords` sentence-starter already was, falling through to `TitledNameAnywhere`
  instead of returning the truncated fragment.
- Inconsistent list formatting: numbered, dashed, `Clue #1:`/`Suspect #1:` sub-headers,
  or a single unbulleted sentence for what should be a list → `ExtractListItems` +
  `ExtractSubHeaderItems` + single-item fallback chain in `ExtractClueList`.
- Suspects sometimes given as plain-paragraph name+description with only the
  Alibi/Contradiction *sub-details* bulleted, not the suspects themselves →
  `ExtractSuspectEntries`'s paragraph-block fallback, filtering out
  Alibi/Contradiction blocks.
- Stray artifacts leaking into output: `####`, `###`, `**bold**`, a bare `End` line,
  echoed prompt fragments (`### At least 5`), literal `{ALL_CAPS}` placeholder tokens
  → all stripped in `StripMarkers`.
- Runs on past where it should stop, hallucinating fake multi-turn conversations
  (`### Response:`, `### Prompt:`, `---`, `##` reappearing) → hard cutoff at the earliest
  of these markers, in shared helper `CutHallucinatedContinuation` (used by
  `ParseActionResponse` and `ParseRealMurderer`). Confirmed in an actual playthrough for
  the Real Murderer follow-up specifically: the model kept going well past "Decisive
  Proof: ..." into a fabricated "---\nReturn to Main Menu\nPlayers can now make new
  choices..." and even a fake "For debugging purposes, here is how the response was
  generated:" explanation of its own reasoning - all of which used to survive verbatim
  into the guess-the-murderer reveal shown to the player, since `Parse()` (used by
  `ParseRealMurderer`) had no hallucination guard of its own before this fix.
  Deliberately not added to `Parse()` itself, since a full story response legitimately
  uses `##`/`###` as real section headers - the cutoff is only safe for narrow,
  single-purpose responses with no legitimate reason to contain any of these markers.
  - A standalone line of nothing but repeated separator characters (e.g. `--/--`) is
    also treated as a hallucination boundary in `CutHallucinatedContinuation`, even
    though it's not one of the fixed marker strings. Confirmed in an actual playthrough:
    the model tacked one onto the end of an otherwise-complete action, followed by a
    self-referential "Your response was successful!" status line. This mattered beyond
    display noise - the divider also defeated `SentenceEndRegex` (a `.` followed by
    `\n--/--` isn't followed by whitespace+capital or end-of-string), so without this
    cutoff the entire hallucinated tail survived as if it were part of the real action.
  - The blind `"\n---"` marker (in the fixed-string list) turned out to have the exact
    same false-positive problem as the blind `"\n##"` marker below, just for a different
    separator. Confirmed in an actual playthrough: the model inserted a bare `"---"` as
    an innocuous stylistic paragraph break between the Result and its own genuine
    `"New Player Action:"`/`"New clue discovered:"` sections - not a sign of
    hallucination at all - and the blind cutoff discarded both real sections entirely,
    the same "no usable New Player Action despite the raw response clearly having one"
    symptom as the premature-`"End"` case below. Fixed by removing `"\n---"` from the
    unconditional marker list and folding it into the existing repeated-separator-divider
    check instead, which already runs *after* computing `LastLegitimateHeaderIndex` for
    the premature-`"End"` fix - so a `"---"`/`"--/--"`-style divider is now skipped (not
    trusted as a boundary) whenever a real action/clue header still follows it, and only
    treated as genuine hallucination when nothing legitimate comes after. `"### Response:"`/
    `"### Prompt:"` stay in the unconditional list - both are specific enough
    conversation-restart markers that no genuine single-purpose response would ever
    contain either, unlike a bare divider or premature `"End"` which the model's already
    confirmed to use innocuously.
  - The blind `"\n##"` marker itself turned out to be too aggressive for
    `ParseActionResponse`'s use case specifically. Confirmed in an actual playthrough: the
    model formats its own required `"New Player Action:"`/`"New Clue Discovered:"` headers
    with a `"###"` markdown prefix (the prompt only asks for the bare phrase, no
    particular styling - Rule #1, the model drifts on formatting regardless of what's
    asked), and `"###"` contains `"##"` as a substring - so the marker fired on the
    model's own genuine first header and discarded the real action, the real clue that
    followed it, and even the real `"### End"` marker, all of which were still perfectly
    legitimate. The response looked, from `GameManager`'s side, like the action heading
    was never found at all (the exact "Investigate further." / empty-`NewAction` symptom
    documented above) - except this time the raw response clearly *did* contain a usable
    action and clue, just past the point where the parser had already stopped looking.
    Fixed by replacing the blind string match with `FindFakeHeaderCutoff`, which only
    treats a `#`-prefixed line as a real hallucination boundary if its heading text isn't
    one of the headers this response is actually supposed to contain (the action header,
    the clue header, or `"End"`) - `ActionHeaderPattern`/`ClueHeaderPattern` are now shared
    constants between this check and `ParseActionResponse`'s own header matching, so the
    two definitions of "what counts as our own header" can't drift apart from each other.
  - The `"### End"` handling had the mirror-image problem: it trusted the *first*
    standalone `"End"` line unconditionally. Confirmed in an actual playthrough: the model
    wrote `"### End"` right after the Result - genuinely ending its response too early,
    before ever reaching `"New Player Action:"`/`"New clue discovered:"` - then kept
    generating anyway, producing a hallucinated restatement of its own task instructions
    ("Now, suggest a single action for Ethan Vale...") that nonetheless contained the real
    action and real clue further down, each followed by its own additional `"### End"`.
    Cutting at the first (premature) `"End"` discarded the real action and clue entirely -
    the "no usable New Player Action" warning fired even though the raw response clearly
    had one. Fixed with `LastLegitimateHeaderIndex`, which finds the last position of a
    real action/clue header anywhere in the raw text; the `"End"` scan now skips any
    occurrence before that point and only trusts the first one at or after it.
  - Fixing the premature-`"End"` truncation surfaced a related issue in the *header
    search itself*: with the full (correctly un-truncated) text now visible,
    `clueMatch`'s permissive `new\s+clue[^:.?\n]*[:.?]` pattern matched a hallucinated
    mid-sentence echo ("Now, provide **a new clue** about Ethan Vale, only if you have
    one...") instead of the real, later `"### New clue discovered:"` header, since
    `Regex.Match` takes the first occurrence and the fake one came first. Confirmed via
    direct testing against the same raw response. Fixed by requiring the header phrase to
    start its own line (`HeaderLinePrefix`, allowing the same optional `#`/`*` markdown
    noise as elsewhere in this file) - a hallucinated restatement embedded mid-sentence
    can't satisfy that, while every genuine header (styled or bare) always has stood alone
    on its own line in every confirmed case so far. Applied to both `actionMatch` and
    `clueMatch` for symmetry, even though only the clue side had a confirmed instance.
  - A subtler variant `CutHallucinatedContinuation`'s fixed markers don't catch: after
    giving the real motive/proof, the Real Murderer follow-up sometimes keeps going by
    addressing the player directly - e.g. "What would you like the player to do next?"
    followed by a fabricated numbered list of new actions. Confirmed in an actual
    playthrough; none of `###`/`---`/`##` appear in this pattern. Neither a numbered
    list nor a direct address to "the player"/"players" should ever legitimately appear
    in the Real Murderer field, so `TrimMurdererHallucination` treats either as a hard
    stop - applied both in `Parse()` (for the main story's Real Murderer section) and in
    `ParseRealMurderer` (for the follow-up), since either path could produce this.
- Varies the "New clue discovered:" header wording constantly (`Found`, `Discovered?`,
  bare `New Clue:`) → permissive regex `new\s+clue[^:.?\n]*[:.?]`.
- Lists two or three clues under a single "New clue discovered:" heading instead of
  one (numbered/bulleted) → `ParseActionResponse` runs `ExtractListItems` on the clue
  block and keeps only the first item. This mattered beyond just display noise: a
  numbered list (`2.`, `3.`) defeats `TruncateClue`'s sentence-boundary cut in
  `GameManager.cs`, since a digit isn't a capital letter, so *all* the extra clues used
  to survive into `allClues` and get re-sent as "Known Clues So Far" on every later
  turn — growing the prompt and diluting relevance each time.
- Prefixes a discovered clue with `"Yes - "`, or answers its own "New clue discovered:"
  heading as if it were a yes/no question and stacks a restated marker/label in front of
  the actual clue - e.g. "New clue discovered: (Yes/No) Yes New Clue: The shop door had
  been unlocked..." - which used to survive into the Clues panel verbatim, preamble and
  all, since a single strip pass only caught a bare leading "Yes" and left "(Yes/No)"
  (doesn't start with "yes") and the restated "New Clue:" label untouched. `CleanCluePreamble`
  loops stripping each known preamble piece (yes/no marker, affirmation, restated header)
  until nothing more matches, in whatever order/combination the model happened to stack.
- "No new clue" answered as `None.`/`No new clue`/`N/A` with varying punctuation →
  `IsNoClue` normalizes before comparing. This also applies to the *initial* "at least 3
  clues" list, not just per-turn answers: the model sometimes runs out of genuine ideas
  and fills a numbered slot with a bare "None" (occasionally with an empty sub-label
  left in front, e.g. `2. : None.`) instead of a real clue - confirmed in an actual
  playthrough, where it rendered as a useless `- : None.` bullet in the Clues panel
  (and read as if a clue had silently gone missing, since a real one had been displaced
  by this placeholder). `IsNoClue` now strips a leading colon/dash before comparing, and
  `ExtractClueList` runs it over every extracted item, not just per-turn clues.
  - Separately confirmed for the *per-turn* case: the model sometimes writes "None." as
    a complete first sentence, then keeps rambling with unrelated commentary afterward
    (e.g. "None. The result stands as described above."). The full multi-sentence blob
    doesn't equal "None" exactly, so `IsNoClue` used to let it through - but
    `GameManager.TruncateClue` downstream still cuts to just the first sentence for
    display, isolating "None." and showing it as if it were a real clue. Fixed by
    checking `IsNoClue` against `TruncateToFirstSentence(clueText)` in
    `ParseActionResponse`, not the raw multi-sentence `clueText`.
- Wraps an entire action or clue in literal double quotes (e.g. `"Review closed-circuit
  feeds..."`) - the closing quote right after the final period also defeats
  `TruncateToFirstSentence`'s/`TruncateClue`'s sentence-boundary lookahead (a quote isn't
  whitespace), so the quotes used to survive all the way into the displayed button/clue
  text. `StripMarkers` now strips a genuine leading+trailing quote pair that wraps the
  *entire* block - an embedded quotation that doesn't wrap the whole thing (e.g. a quoted
  note inside a longer clue sentence) is left alone.
- A clue sometimes displays starting lowercase mid-sentence with no clear antecedent
  (e.g. "- according to her statement, she did see someone enter..." - unclear who "her"
  is). Not fully root-caused without a captured raw response, but the likely mechanism:
  `CleanCluePreamble` stripping a leading "Yes, " off a self-answered yes/no question
  can leave behind a naturally-lowercase continuation whose implicit question (and its
  antecedent) was never shown. `GameManager.TruncateClue` now capitalizes the first
  letter regardless of cause - it can't recover a lost antecedent, but at least reads as
  a complete sentence instead of a visibly truncated fragment. Revisit with an actual
  raw response if this keeps happening, to confirm the mechanism and see if the
  antecedent is recoverable from elsewhere in the response.
- Restates an already-discovered clue verbatim for a later action instead of finding
  something new - confirmed in an actual playthrough (identical clue text appeared twice
  in the panel, with "New clue found." shown both times). Despite "Known Clues So Far"
  being in every action prompt, weak instruction-following means the model can just echo
  one back. `GameManager.AddClueEntry` now skips a clue whose formatted text exactly
  matches one already in `allClues` (which already covers both the initial clue list and
  every per-turn discovery, since both paths funnel through this same method) and
  correctly withholds "New clue found." for it.
- Multi-sentence answers where exactly one sentence was asked for (actions, clues) →
  `TruncateToFirstSentence`/`TruncateClue` cut at the first genuine sentence boundary.
- The per-turn Result is allowed up to 2 sentences (unlike actions/clues), clamped by
  `MaxResultLength` at the last complete sentence within the limit - raised 350 → 450 →
  1200 across two playthroughs. First raise: a genuine 2-sentence result ("...The
  turn-down had a post-it note attached that said: 'See Miss Janine tomorrow.'") got
  clamped off before reaching that second, plot-relevant sentence, silently dropping
  information the player needed. Second raise: confirmed the model is consistently
  capable of much richer, multi-paragraph results than either limit allowed - see "Game
  scene output is now scrollable" below for the UI change that made a much larger limit
  viable (it no longer has to double as a fixed-height-box constraint).
  `TruncateResult` was also changed to always cut at the last complete sentence, not just
  when over the length limit - a genuine short result already ends on a real sentence so
  this changes nothing for the normal case, but it cleans up a dangling non-sentence
  fragment left behind by the fix below (e.g. a heading like "Based on these findings:"
  with nothing after it, once its own bulleted list is gone).
- The model sometimes stops narrating a Result and starts either addressing the player
  directly ("What would you like to do now?") or listing its own speculative bulleted
  "Based on these findings:" reasoning - both are hallucinated meta-commentary, never
  legitimate Result content, and neither has a `###`/`---`/`##` marker for
  `CutHallucinatedContinuation` to catch. This is the exact same shape as an
  already-confirmed Real Murderer hallucination (a numbered list or direct
  "player(s)" address, previously guarded by `MurdererHallucinationMarker`/
  `TrimMurdererHallucination`) - generalized into `HallucinatedMenuMarker`/
  `TrimHallucinatedMenu` (now also catching bulleted lists, not just numbered ones) and
  applied to both fields, applied to the Result substring before `TruncateResult` runs.
  - Confirmed in a later playthrough that trimming this out of Result wasn't the whole
    story: the "What would you like the detective to do now?" question (and the "What
    would you like to do now?" variant from the case above) is immediately followed by a
    perfectly usable numbered menu of real actions - discarding it entirely just to keep
    Result clean threw away a real action every time this happened, falling back to
    `GameManager`'s generic "Investigate further." even though the raw response clearly
    offered a good one. Fixed by treating this phrase (`MenuPromptPattern`) as an
    *alternate* action-header trigger, used only when the real `"New Player Action:"`
    header wasn't found at all - `ParseActionResponse` now takes whichever of the two
    fired (`effectiveActionMatch`) and runs it through the exact same "take the first
    numbered/bulleted item" extraction a real header already gets, recovering a genuine
    `NewAction` instead of leaving the field empty. This doubles as the fix for Result
    leakage too: since `resultEnd` is computed from `effectiveActionMatch`, Result now
    correctly stops *before* the question rather than needing `TrimHallucinatedMenu` to
    catch it after the fact. Deliberately does NOT fire when a real action header is
    already present elsewhere in the response - a genuine header is always trusted over
    this fallback, since this pattern hasn't been confirmed to coexist with one.
  - Confirmed in yet another playthrough that the model doesn't only use "New Player
    Action:" or "What would you like to do" phrasing before its options menu - it can
    invent an entirely different, one-off heading too (e.g. "Investigative Option(s):").
    Chasing each new wording individually doesn't scale, so instead of recognizing any
    particular phrase, `FindTrailingListBlockStart` looks for the shape that actually has
    been consistent across every confirmed variant: a run of consecutive numbered/
    bulleted lines sitting at the end of the response. It walks backward from the last
    list-item line, extending the block only while consecutive items are separated by
    nothing but blank lines - a paragraph of unrelated prose in between would mean an
    earlier match belongs to a different, earlier list, not this trailing one. Used as
    the final fallback in `ParseActionResponse`, only reached when neither a real header
    nor `MenuPromptPattern` matched anything - the invented heading itself doesn't need
    separate handling, since `TruncateResult`'s "cut at the last genuine sentence"
    already discards a dangling colon-terminated heading with nothing after it, the same
    way it already does for "Based on these findings:" above.
  - Separately confirmed: the model sometimes writes the clue and action sections in the
    *reverse* of the order the prompt asks for ("A new clue discovered:" appearing before
    "New player action:", with an extra leading "A " on the clue header this time too).
    The leading "A " broke `HeaderLinePrefix`'s line-anchor requirement entirely (it
    only tolerated markdown noise, not an ordinary word), so `clueMatch` failed to match
    at all - and with no `clueMatch` to bound it, `resultEnd` ran all the way past the
    clue text, leaking the entire "A new clue discovered:" section straight into the
    displayed Result instead of the Clues panel. Fixed by adding `OptionalLeadWord` (one
    short run of letters, bounded so it can't swallow a whole clause) to the shared
    prefix both `HeaderLinePrefix` and `FindFakeHeaderCutoff`'s legitimacy check use - the
    real protection against the earlier-confirmed mid-sentence hallucination
    ("Now, provide a new clue about Ethan Vale...") is still the line-anchor itself, not
    the absence of any leading word, since that hallucinated phrase sits well into the
    middle of its own line either way.
  - A further variant confirmed in an actual playthrough: instead of a numbered/bulleted
    menu, the model can hallucinate a lettered multiple-choice quiz - "What clue does
    this envelope reveal?" followed by a stray markdown code fence (` ``` `) and then
    "A. ...", "B. ...", "C. ..." options - none of which the existing digit/bullet-only
    `HallucinatedMenuMarker` caught (`A.`/`B.` aren't `\d+[.\)]` or `[-*•]`), so the whole
    fabricated quiz survived straight into the displayed Result. Fixed with two additions
    to `HallucinatedMenuMarker`: a run of at least two *consecutive* lettered-marker
    lines (a single lettered line isn't enough of a signal on its own - confirmed via
    direct testing that trusting just one false-positives on a genuine name written with
    a spaced initial, e.g. "A. J. Reeves refused to comment.", which a real
    multiple-choice list's *sequence* of markers doesn't share), and the literal "what
    clue ... ?" question phrasing itself (matched unanchored, same treatment as the
    existing `players?` check in this same regex, since Result has no fixed line shape
    to anchor to) - without the second part, trimming just the lettered options still
    left the question dangling on its own, since removing its answer choices let the
    question mark satisfy the normal end-of-Result sentence boundary. The stray code
    fence is now also stripped generally in `StripMarkers`, since it's never legitimate
    content in any field here.
- Trailing junk after the real content in whole-block sections (Crime Summary/Victim/
  Crime Scene/Suspects/Clues) → `TrimTrailingJunk` cuts at the *last* genuine sentence,
  discarding whatever follows regardless of what it says.
- Abandons a numbered list partway through (e.g. only 2 of the requested 3 suspects,
  leaving a bare `3.` with no name before the next heading starts) → a dangling
  marker's period is otherwise indistinguishable from a real sentence end when it's the
  last thing in the block, so `TrimTrailingJunk` strips trailing empty
  numbered/bulleted marker lines (`TrailingEmptyListMarkers`) *before* running the
  sentence-boundary cut. (Separately, `ExtractListItems` already required content after
  the marker, so this never affected `GameSession.SuspectNames`/`Clues` — only the raw
  text shown on the Story screen.)
- Paraphrases the `Real Murderer:` heading instead of writing it verbatim - e.g. "What
  really happened?" - which used to fall through the header match entirely, so the
  murderer reveal silently became trailing content of whatever section came before it
  and got *displayed to the player* (a real spoiler, not a formatting nit - it showed up
  stitched onto the end of the Clues section on the Story screen). Fixed by recognizing
  the paraphrase as its own standalone-line alternative in `CandidateHeadingLine` (see
  below) so an innocent phrase buried in ordinary prose ("no one knows what really
  happened that night") can't be mistaken for the header and wrongly split the story
  there.
- Invents its own extra headings that were never asked for - e.g. `Missing Items:`,
  `Identified Visitors:` - which used to silently become trailing content of whatever
  wanted section came right before them (with no header of their own shown, since
  `StripMarkers` deletes any `#`-prefixed line entirely), making them look like part of
  the Crime Scene text in the Story scene. `Parse()` no longer searches for each known
  header independently; it first finds *every* standalone heading-style line via
  `CandidateHeadingLine` (markdown `#`s and/or `**bold**`, or a bare line, ending in a
  colon), classifies each one by keyword (`ClassifyHeading`/`KnownHeadingKeywords`), and
  simply discards the content of any heading that doesn't match one of our seven known
  fields - it still acts as a boundary (ending the previous section), its own body just
  never gets stored or displayed. A recognized heading can repeat (`Clue #1:`/`#2:`/`#3:`
  instead of one `Clues:` list) - `CombineParts` gathers every occurrence for a key and
  re-presents them as a bulleted list so `ExtractListItems` downstream still splits them
  into separate items, rather than only the first occurrence surviving.
  - A subtlety this introduced: a recognized heading is allowed to have real content
    trailing on the *same* line (`**Victim:** Ellyn Harnett...`), since that's a format
    the model actually uses - but an *unrecognized* heading candidate is only trusted as
    a real boundary when it's alone on its own line. Without that restriction, an
    incidental "**Name**: description" sentence used as body-text emphasis (e.g.
    `**Ashley Voss**: A local street musician...` inside the Victim section itself) would
    look exactly like a heading and wrongly swallow the rest of that section. See
    `RestOfLineIsBlank` in `StoryParser.cs`.
  - Known limitation: the heading-candidate pattern caps the heading phrase at 40
    characters specifically so that an ordinary short colon-terminated sentence can't be
    mistaken for a heading. A genuine model-invented *sub*-heading inside a wanted
    section (not yet observed, but plausible) that happens to be short enough could still
    be wrongly excluded - not a problem for anything confirmed so far.
  - `KnownHeadingKeywords` deliberately matches on the single most distinctive word for
    each field, not the full expected phrase - e.g. `CrimeScene` matches just `scene`,
    not `crime\s+scene`. Confirmed necessary in an actual playthrough: the model titled
    the crime scene section "At the scene:" instead of "Crime Scene:", which the old
    two-word pattern didn't match at all, silently dropping that entire section from the
    Story screen (with no warning, since `CrimeScene` still landed in `parsed.CrimeScene`
    via a *different* recognized heading's fallback elsewhere in that response). Same
    principle applied to `CrimeSummary` (→ `summary`), `RealMurderer` (→ `murderer`, the
    "what really happened" paraphrase kept as a separate alternative), and
    `InitialActions` (→ `actions`) - `Victim`/`Suspects`/`Clues` were already single-word.
- A *much worse* version of the same spoiler risk as the "What really happened?"
  paraphrase case above (see the `Paraphrases the Real Murderer: heading` bullet earlier
  in this catalog), confirmed in an actual playthrough: a bare, unmarked heading with
  real content trailing on the *same* line -
  e.g. `"Real Murderer: Renna Dill - using the missing key..."` and `"Clue: Renna Dill
  arrived thirty minutes late..."` written as their own lines with no `**`/`#` markup at
  all. `CandidateHeadingLine` didn't recognize either shape: the marked-up alternative
  requires markup, and the bare-standalone alternative requires *nothing* after the
  colon (`$`-anchored) - a bare label with content trailing satisfies neither. Both (and
  everything after, up to the next *recognized* heading) silently became trailing content
  of whatever section came right before them - in this case `Suspects:` - so the murderer
  reveal itself rendered verbatim on the Story screen, right below the suspect list. Not
  just a display bug: the Story scene is the one place this project explicitly never
  wants `RealMurderer` to appear (`StoryDisplay.BuildDisplayText` never reads that field
  at all - confirmed by inspection - so this was purely a parsing failure routing the
  content into the wrong field, not a display-layer leak). Fixed by adding a 5th
  alternative to `CandidateHeadingLine` for a bare heading with same-line content, but
  deliberately **not** trusting it for all seven known fields the way the other four
  alternatives are - confirmed via direct testing that doing so is too easy to trip on
  ordinary narrative prose that happens to start a line with a common word immediately
  followed by a colon (`"Suspects: three people had motive, but only one had the
  opportunity..."` as an ordinary Crime Scene sentence wrongly carved out a bogus
  Suspects heading right there, wiping out the real Crime Scene content that should have
  followed it). Scoped in `Parse()`'s heading-collection loop to trust this specific
  alternative only when the classified key is `Clues` or `RealMurderer` - the two fields
  actually confirmed to use this bare same-line style - rather than opening it up to all
  seven on pure speculation.
- A whole-story response can hallucinate a direct-address question - "What would you like
  me to say?" - plus its own self-answered numbered list, right after a genuine `"Clues:"`
  list and before recovering with the real `"Initial Player Actions:"` heading. Confirmed
  in an actual playthrough. Since the question doesn't end in a colon, `CandidateHeadingLine`
  never recognized it as a section boundary, so it (and a `"-----"` divider the model tacked
  on after it) silently became trailing content of the Clues section - visible verbatim on
  the Story screen, and far worse in the Game scene: this same response also had a
  `"Final Clue:"` heading (a 4th clue, correctly meant to merge into `Clues` via the
  repeated-heading mechanism above), which gave `Clues` two heading-occurrences and
  triggered `CombineParts`'s multi-part flattening branch - collapsing the *first* part's
  own 3 already-numbered clues into one run-on blob (since flattening indiscriminately
  collapsed every newline to a single space, destroying the numbered list's line
  structure). The blob's leftover leading `"1."` then satisfied `GameManager.TruncateClue`'s
  sentence-end check (a digit, a period, then a capital letter looks exactly like a
  complete one-token sentence), truncating the entire clue down to a bare `"- 1."` in the
  Clues panel. Two fixes, verified together against the actual raw response via the
  PowerShell/`Add-Type` technique (see the regex-verification memory) plus a regression
  suite covering the already-confirmed `Clue #1:`/`#2:`/`#3:` case and the per-turn menu
  hallucination this shares a pattern with:
  1. `Parse()`'s per-heading loop now runs each heading's content through
     `TrimHallucinatedQuestion` before it's ever added to `sectionParts` - cutting at the
     first line matching the shared `MenuPromptPattern` (broadened from "to do" only to
     "to do|say", since this confirmed instance says "say" not "do"). Deliberately
     anchored to a standalone line, unlike `ParseActionResponse`'s existing unanchored use
     of the same pattern - direct testing caught a real false positive from an *anchored*
     version's absence: a suspect's own quoted dialogue containing "what would you like me
     to do about the broken lock?" mid-sentence wrongly truncated the entire Suspects
     section when matched unanchored. The genuine hallucination always presents as its own
     interjected line (a full paragraph break before and after); a quoted rhetorical
     question buried mid-sentence can't satisfy a line-start anchor, so anchoring catches
     the real case while leaving legitimate narrative dialogue alone.
  2. `CombineParts` no longer blindly flattens every part to one line - it first checks
     whether a part already contains its own numbered/bulleted list
     (`ExtractListItems(part).Count > 1`) and, if so, re-emits each of that part's own
     items as its own bullet instead of flattening them together. A part that's genuinely
     just one clue (the normal `Clue #1:`/`Clue #2:`/`Clue #3:` case, or `Final Clue:`'s
     own single paragraph) still flattens exactly as before - only a part that has more
     than one item of its own changes behavior.
- Naive sentence-boundary detection breaks on title abbreviations (`Mr.`, `Mrs.`,
  `Dr.`, etc. followed by a capitalized name looks like a sentence end) →
  `StoryParser.SentenceEndRegex` uses a negative lookbehind for a known abbreviation
  list. Made `public` and shared with `GameManager.cs`'s clue display truncation rather
  than kept as two separate copies, so the two can no longer drift out of sync.
- A clue's sentence-ending punctuation sometimes falls *inside* a quoted phrase that
  closes the sentence - e.g. "A piece of chalk found outside the tent read 'This will
  tell the truth.'" - with the model's closing quote landing right after the period
  instead of before it. Confirmed in an actual playthrough: since a quote character is
  neither whitespace nor "end of string," `SentenceEndRegex` found no sentence end at
  all for the whole clue (not just a truncation point), so `TruncateClue` returned
  `null` and the entire clue silently vanished from the panel - it looked like a clue
  had gone missing, when the real cause was this one specific clue never being added in
  the first place. `SentenceEndRegex` now optionally allows a single closing quote
  character (straight or curly, `"'’”`) immediately after the `.`/`!`/`?` before
  checking for the usual whitespace+capital-letter/end-of-string boundary.
- Generation gets cut off mid-clue/mid-sentence by hitting `max_tokens` → detected via
  "no sentence-ending punctuation found at all"; the fragment is discarded rather than
  shown (see `TruncateClue` returning `null`, and `AddClueEntry`/`OnActionResultSuccess`
  only claiming "New clue found." if the clue actually survived).
- Result text sometimes IS the clue text with no separate description → if `Result`
  parses empty but `NewClue` doesn't, `Result` falls back to the clue text.
- The result doesn't always correspond to the actually-selected action (weak
  instruction-following under long context) → see Rule #2 above; still worth watching.
- Suspect lines with no `**bold**` name and no comma - e.g. "Charlie claimed he heard
  footsteps in the tent during her performance." instead of "Charlie, a café owner
  who..." - used to defeat `ExtractName`'s comma-based fallback entirely, returning the
  *entire sentence* as the "name," which would have rendered as a garbled sentence on a
  guess-screen button. Fixed by adding a third tier (`CapitalizedNameRun`) that takes
  the leading run of Capitalized words before falling back to the whole line.
  - A related but distinct case, confirmed in an actual playthrough: a suspect line
    *does* have a comma, but leads with an evidentiary clause instead of the name - e.g.
    "The coin was found near Brennan's hand, suggesting Officer Marcus was involved..."
    - so blindly trusting "everything before the first comma" returned the whole clause
    as the "name" (shown verbatim as a guess-screen button, since `EnterGuessPhase`
    displays `GameSession.SuspectNames` directly). Fixed with `LooksLikeName` - the
    pre-comma segment is only trusted if every word in it is capitalized (a real name
    has no lowercase filler words) - plus two more fallback tiers before giving up: a
    title+name found anywhere in the line (`TitledNameAnywhere`, e.g. "Officer Marcus"),
    then the first standalone capitalized word past the start of the line that isn't a
    common sentence-starter (`NonNameWords`) like "The"/"When"/"After". Each tier is a
    weaker signal than the last, but still better than displaying the entire sentence.

## Real Murderer is threaded through every per-turn prompt

The initial `RequestStory` prompt already asks for a `Real Murderer:` section (name,
motive, decisive proof), and `StoryParser.Parse` captures it into `ParsedStory.RealMurderer`.
For a while, nothing downstream ever *read* that field — `RequestActionResult`'s templates
had a static, unfilled-in line ("Real murderer is not yet known by the player.") that told
the model nothing. Fixed: `GameSession.RealMurderer`/`HasRealMurderer` expose it,
`GameManager.OnActionClicked` passes it into `RequestActionResult`, and both action
templates embed it twice — once near the top for context, and once again as the literal
last line before `### Response:` (Rule #2), each time framed as a "SECRET (do NOT reveal
to the player)" so the model has the answer without being told to say it out loud. If it's
missing from the parsed story (format drift, per Rule #1), `LLMStoryClient` substitutes a
neutral fallback string rather than formatting an empty value into the prompt, and
`GameSession.IsMissingExpectedFields` now flags a missing `RealMurderer` the same way it
already flags missing Crime Summary/Victim/etc.

Confirmed in an actual playthrough: the model sometimes skips the `Real Murderer:`
section from the main story response entirely, which used to mean the guess-the-murderer
resolution had nothing to work with and always ended indeterminate, with no reveal text
at all. Fixed the same way actions/suspects/clues already were - `LoadingController`
detects `!GameSession.HasRealMurderer` after the main story parse and kicks off
`LLMStoryClient.RequestRealMurderer` as a background follow-up (retried once on failure,
same as the others), landing in `GameSession.SetRealMurderer`. The follow-up's own
response is parsed by `StoryParser.ParseRealMurderer`, which reuses `Parse()` (so the
"What really happened?" paraphrase is still recognized here too) and falls back to
`CrimeSummary` if the model skips the heading even in this narrow, single-purpose
follow-up - `Parse()` already promotes headerless content there as a catch-all, and
since this follow-up never asks for anything else, that's almost certainly just the
answer.

Also confirmed: even when a `Real Murderer:` section *is* present, the model sometimes
gives just a bare name with no motive or proof at all ("Real Murderer: Ron Tennyson.") -
a real player-facing problem, since the whole point of the end-game reveal is to explain
*why*, and "it was Ron Tennyson" alone doesn't. The main `PromptTemplate` asked for
"clear motive and decisive proof" back in its numbered list of six sections, but unlike
Suspects/Clues/Actions had no dedicated `"Important:"` reinforcement - for a small model
with weak long-context attention (Rule #2), that's exactly the instruction most likely to
get dropped by the time generation starts. Fixed by adding a `Real Murderer:` line to the
`"Important:"` block (now the last one, right before `### Response:`), and by adding the
same requirement to `RealMurdererFollowUpPromptTemplate`, repeated as its own line right
before generation there too. This is a prompt-only fix (no code-side clamp exists for
"the model didn't invent a motive it never had") - deliberately chosen over adding a
*second*, later LLM request (with full clue/action history) to ask for the motive
after the fact: that would add a network round-trip at the exact moment the player wants
immediate payoff (RunPod's own wait limit is ~90s), and risks generating a motive that
disagrees with the murderer identity already threaded through every per-turn prompt as a
"SECRET" all game, on top of being the single largest, most context-heavy prompt in the
app - the reinforced upfront prompts keep the reveal instant and consistent instead.

Related player-facing problem, not yet confirmed as fixed: even with a motive and proof
present, both are generated independently of the "Clues:" list the player actually
investigates during gameplay, so the "decisive proof" in the reveal can reference
evidence the player never saw or had any way to find - it reads as new information
sprung on them at the end rather than something they could have deduced. Both
`PromptTemplate` and `RealMurdererFollowUpPromptTemplate` now explicitly require the
decisive proof to correspond to one of the clues already listed, rather than inventing a
new one - the follow-up already receives the full story (including the Clues section) as
`{0}`, so no new plumbing was needed. Same category of fix as the motive reinforcement
above (prompt-only, no code-side clamp is possible for verifying semantic correspondence
between two free-text passages) and same reasoning for not adding a second end-game LLM
request instead. If the model still invents disconnected evidence after this, the
fallback discussed but not implemented: stop presenting it as "decisive proof" the player
could have found, and reframe the reveal's evidence as pure narrative flavor instead.

Also confirmed: a short, bare, single-line follow-up reply with content trailing on the
*same* line - e.g. `"Real Murderer: Ron Tennyson."` with no markup at all - isn't
recognized as a heading by `CandidateHeadingLine` at all (it requires either markup, or
the colon to end the line with nothing after it - see the "Ashley Voss" note above for
why that restriction exists), so the label itself fell through into the `CrimeSummary`
fallback and leaked verbatim into the guess-the-murderer reveal text shown to the
player. `ParseRealMurderer` now strips a leading `Real Murderer:`/`What really
happened:`/`?` label defensively from whatever text it ends up returning - safe here
specifically because this follow-up's prompt only ever asks for one thing, unlike the
general-purpose `Parse()` this label-stripping isn't applied to.

## Suspect-bound action branches

Confirmed in an actual playthrough, two related problems with the original design (one
shared `allHistory`/`allClues` context sent identically to every branch):

1. **Every branch's results centered on the real murderer, regardless of which action
   was picked.** Since the real murderer is threaded into every per-turn prompt as a
   "SECRET" the model must stay consistent with (see above), and nothing else told the
   model which suspect a given action was even about, the model had no counterweight
   pulling it toward investigating anyone else - every branch converged on the same
   person's guilt instead of surfacing something about each of the 3 suspects.
2. **Cross-branch action confusion.** Because `allHistory` was one linear list shared
   across all 3 branches, if the player played branch 1 then branch 2 then came back to
   branch 1, branch 1's next prompt would show branch 2's action/result sitting right in
   the middle of its own history - the model would sometimes treat the *other* branch's
   result as what it's continuing from, rather than treating each branch as an
   independent thread.

Fixed by binding each of the 3 action branches to one specific suspect, by index, at
`GameManager.Start()` (`branchSuspectNames[i] = GameSession.SuspectNames[i]`, falling
back to `"Suspect N"` if fewer than 3 real names came through - same fallback text
`EnterGuessPhase` already uses). This has a nice side effect for free: since
`EnterGuessPhase` already shows `suspectNames[i]` on button `i` in the same order,
branch `i`'s investigation and guess-button `i`'s accusation now refer to the same
person - button 1 investigates *and* accuses the same suspect throughout.

Both `branchHistories[]` and `branchClues[]` are now per-branch arrays instead of one
shared `allHistory`/single flat clue list - `BuildHistoryText`/`BuildCluesText(index)`
only ever pull from branch `index`'s own arrays, so a branch's prompt never sees another
branch's actions or discoveries (fixing problem 2 directly). The initial story's own
clue list is still shared baseline knowledge for every branch (`initialClues`, like
everyone having read the same case file) - only *mid-branch discoveries* are scoped per
branch. `allClues` remains a flat combined list purely for the Clues panel display and
for cross-branch duplicate detection (`AddClueEntry` still skips an exact repeat even if
a *different* branch found it first).

`LLMStoryClient.RequestActionResult` gained a `suspectName` parameter, embedded in both
`ActionPromptTemplate` and `FinalActionPromptTemplate` as "THIS INVESTIGATION THREAD IS
FOCUSED ONLY ON SUSPECT: {5}" near the top, an `"Important:"` reinforcement, and folded
into the final repeated line right before `### Response:` (Rule #2) - addressing problem
1. This is a prompt-only fix for problem 1 (no code-side clamp can force the model's
*content* to be about a specific suspect, same limitation as the motive/decisive-proof
reinforcements above) - worth revisiting if the model still gravitates toward the
murderer regardless of branch after this.

Confirmed in a further playthrough that problem 1 wasn't fully resolved by the
per-turn prompt alone - two follow-up refinements:
- **Seed suspect coverage at the source.** The main `PromptTemplate` now also requires
  at least one clue per suspect under "Clues:", and requires the 3 "Initial Player
  Actions" to correspond one-to-one, in order, with the 3 suspects listed under
  "Suspects:" (same requirement added to `FollowUpPromptTemplate`, the
  `RequestInitialActions` follow-up, since it also has the suspect list available as
  context). Relying entirely on the per-turn prompt to retroactively steer an
  initially-generic action toward a suspect was weaker than having the model seed each
  suspect's own lead while it still has the suspects freshest in its own context.
- **Explicitly distinguish "focus suspect" from "actual murderer."** The SECRET line
  now spells out that if the focus suspect `{5}` is *not* the real murderer described in
  `{4}`, the result/clue for that thread should stay neutral or only mildly suspicious -
  strongly incriminating content is reserved for when `{5}` truly is the murderer. Before
  this, the model had no signal for *how guilty* to make a non-murderer suspect's thread
  look, which risked either every thread pointing at the murderer anyway, or - just as
  bad - a non-murderer thread reading as a second, contradictory confession.

## Guess-the-murderer resolution

`GameManager` gives the player 2 guesses (`MaxGuesses`) once all 9 actions are done. A
wrong guess disables that suspect's button (a visible elimination signal) and consumes
one guess; the game ends on a correct guess or when guesses run out. Deliberately does
*not* ask the player to also justify their guess with a supporting clue - tying a
specific clue to a specific suspect would need either a new LLM request (this model
already degrades under long/complex prompts, see Rule #1/#2) or fragile text-similarity
matching that could confidently tell a player their correct reasoning was wrong. Instead
`GameSession.RealMurderer`'s existing motive+proof text (already generated, already
display-ready) is shown as a narrative reveal after the game ends, win or lose.

Since `GameSession.SuspectNames` and `GameSession.RealMurderer` are extracted/generated
independently, there's no guaranteed way to know which suspect *is* the murderer just
from string equality - titles can differ (e.g. "Miss Rissa" in the Suspects list vs
"Ms. Rissa" in the Real Murderer reveal - confirmed same person, different title, in a
real raw response). `StoryParser.DetermineMurdererIndex` fuzzy-matches by stripping a
leading title from each suspect name, then scoring how many of the remaining name-words
appear (word-boundary, case-insensitive) anywhere in the Real Murderer text - highest
scorer wins. If every suspect scores zero (the data's too garbled to tell, or there
aren't 3 real suspects to begin with), the result is *indeterminate* (-1), never a
guess - `GameManager` shows a neutral "can't call this one" message rather than
resolving a win or loss off bad data. This is the same "never let a formatting hiccup
silently produce a wrong answer" principle as everywhere else in `StoryParser.cs`, just
applied to picking a winner instead of picking text to display.

The indeterminate message deliberately does *not* say "there's no way to call this one
right or wrong" before showing the reveal - it used to, and confirmed in an actual
playthrough, immediately following that line with the full motive+proof text directly
contradicted it (the game claims it can't tell, then names the murderer one sentence
later). `HandleGuess` now just presents the reveal on its own in the indeterminate case,
with no right/wrong framing either way, rather than trying to reconcile the two messages.

No new Unity scene was needed for this - the resolution reuses the same 3
`actionButtons`/`buttonTexts`/`outputText` fields already repurposed for the guess
phase itself, calling `SceneManager.LoadScene` directly rather than going through the
existing `GoToMainMenu` component. "Return to Main Menu" is placed on the *last*
button (`actionButtons.Length - 1`), not the first - confirmed in an actual playthrough
that a long reveal message (motive + decisive proof) can run down far enough to
visually overlap a button in the first slot, so the last slot gives it the most
clearance before that happens.

## Game scene output is now scrollable

Confirmed in an actual playthrough: the model is consistently capable of much richer,
multi-paragraph Results than either the UI or `MaxResultLength` allowed for - genuinely
good, plot-relevant detail was being thrown away just to fit a fixed-height text box (see
the "Chloe Everett, a freel—" mid-token-cutoff note above for an unrelated but similarly-
flavored case of good content getting lost to a hard limit). Rather than keep raising a
character clamp indefinitely, `GameManager`'s `outputText` is now wrapped in a proper
scroll view, nested under the existing `ActionOutput` GameObject:

```
ActionOutput (unchanged: Button + Image, now just a plain container)
└── Scroll View (NEW - ScrollRect, Image for raycasting)
    ├── Viewport (NEW - Mask + Image)
    │   └── Content (NEW - VerticalLayoutGroup + ContentSizeFitter)
    │       └── Text (TMP)  (existing outputText, re-parented, anchors fixed to a
    │                        single point per the Vertical Layout Group child rule below)
    ├── Scrollbar Vertical (NEW - Permanent visibility, so it's always visible even
    │   │                    when content fits, per explicit request: the player has no
    │   │                    other way to discover the box is scrollable)
    │   └── Sliding Area → Handle
    └── Scrollbar Horizontal (NEW - AutoHide visibility; present for structural
        │                      parity with Story.unity's own Scroll View, but never
        │                      actually shown since `m_Horizontal: 0` - horizontal
        │                      scrolling is disabled, so it would be a non-functional,
        │                      confusing bar if forced visible)
        └── Sliding Area → Handle
```

First attempt put the `ScrollRect` directly on `ActionOutput` and skipped scrollbars
entirely - confirmed in an actual playthrough that this technically worked (content did
scroll) but gave the player *no visual indication* it was scrollable at all, since nothing
else in the UI hints at it. Rebuilt to mirror Unity's standard Scroll View structure
exactly (verified field-for-field against the already-working Scrollbar Horizontal/
Vertical + Sliding Area + Handle hierarchy in the Clues panel's own scroll view in this
same file, colors and all) - a real `Scroll View` object holds the `ScrollRect`, with
`Viewport` and both scrollbars as its children, not a variant invented from scratch.

`GameManager.cs` gained `outputScrollRect`/`outputContentRect` serialized fields and a new
`SetOutputText` helper (replacing every direct `outputText.text = ...` assignment) that
calls `LayoutRebuilder.ForceRebuildLayoutImmediate` + resets `verticalNormalizedPosition`
to 1 after every change - same as `StoryDisplay.cs` already does - so a new, shorter
message never leaves the view scrolled partway down from a previous long one.

This freed `MaxResultLength` to stop doing double duty as a UI-space constraint (see
above) - it's now purely a sanity backstop against truly runaway generation, with the
real guard against hallucinated tails being `TrimHallucinatedMenu` (also documented
above). **Not yet verified in the Editor** - the `.unity` scene edits were made directly
(new GameObjects/components wired by hand, matching Story.unity's/the Clues panel's
already-proven structure field-for-field, with every fileID cross-reference checked for
internal consistency both times) rather than through the Unity Editor itself, since a
second Editor instance can't open the same project concurrently (see the batchmode
gotcha below). Confirm the ActionOutput box scrolls, shows a visible vertical scrollbar
the player can see and drag, and that the (always-empty) horizontal scrollbar never
actually appears.

## Clues/Suspects panel toggle

The two bottom-right buttons in Game.unity used to navigate away (Story, Main Menu) - by
request, they're repurposed into `CluesButton`/`SuspectsButton` that toggle what the
*same* scroll view (the existing Clues panel, `cluesGroupParent`) shows, rather than
adding a second view. Both are prefab instances of a shared button prefab
(`f51975003815b6140914541ab193a90c`, also used elsewhere e.g. Story.unity), so the
rename/re-target lives in each instance's own `PrefabInstance.m_Modification` overrides,
not the shared prefab asset itself - editing the prefab asset directly would have changed
every instance across every scene, including unrelated buttons.

`GameManager.ShowClues()`/`ShowSuspects()` toggle every tracked clue-entry GameObject's
active state as one group against a single `suspectsEntryObject` (one clone of the same
`clueTemplate` other clues use, so it matches visually) - both live under the same
`cluesGroupParent`, and `VerticalLayoutGroup` already skips inactive children from its
layout, so no separate container or second `ScrollRect` was needed. The `CluesTag` header
text swaps between `"CLUES"`/`"SUSPECTS"` to match.

Suspects content prefers the full `ParsedStory.Suspects` text block (names + descriptions/
alibis) over bare names, since bare names are already visible on the guess buttons - the
reason to open this panel is the detail those buttons don't show. But the initial parse
can come back with an empty Suspects block, which kicks off the `RequestSuspects`
follow-up in `LoadingController` - and that follow-up only ever fills in
`GameSession.SuspectNames` (bare names), never the richer text block. Recomputing the
display text fresh on every `ShowSuspects()` click (`BuildSuspectsDisplayText`), rather
than once at `Start()`, means a click after that follow-up lands still shows something
useful (falling back to the bare names) instead of "no suspects" for the rest of the
game. A clue discovered while the Suspects tab is the one currently showing is added
inactive (not popping up mid-suspects-list) and only becomes visible once the player
switches back to Clues.

## Action button hover/press/selected feedback

`ActionButton.prefab` (the 3 buttons in `ActionButtonGroup`, reused for both the 3
action branches and the guess-the-murderer phase - see `GameManager.actionButtons`)
already had its `Button` component wired with `m_Transition: 1` (Color Tint) and a
`ColorBlock`, but it was invisible in practice: the target graphic (the button's own
background `Image`) had `m_Color` alpha `0`. `Graphic.CrossFadeColor` (what Color Tint
transitions animate toward) overwrites the graphic's rendered color outright based on
the `ColorBlock`'s state colors - but the *base* `NormalColor` itself was opaque white
`{1,1,1,1}` with the alpha-0 baseline only ever showing pre-Play in the Editor, not
during actual gameplay - so in practice hover/press never read as intentional, cohesive
feedback, just an inconsistent white box. Fixed by giving the background `Image` (and
every `ColorBlock` state) a deliberate warm-amber palette matching the button text's own
existing font color (`{1, 0.851, 0.627}`, confirmed against `ActionButton.prefab`'s
`Text (TMP)` child) rather than Unity's default grayscale: a faint glow at rest (low
alpha, ~0.10), brighter on hover (~0.30), a deeper saturated gold flash on press
(~0.55), and a softer persistent glow for `Selected` (~0.22, EventSystem's own
`hasSelection` state - free once a real color shows, no script needed). No C# changes
were needed - this is pure reuse of Unity's built-in `Selectable` state machine, once it
had something visible to actually tint.

One nuance: `GameManager.OnActionClicked` calls `SetButtonsInteractable(false)` on all 3
buttons immediately after any click (while the LLM request is in flight), and
`Selectable`'s state priority checks `!IsInteractable()` before `hasSelection` - so
during the 3-action-branch phase, the clicked button's `Selected` glow is pre-empted by
`DisabledColor` almost immediately, and what actually reads to the player is the brief
`Pressed` flash rather than a lingering highlight. The guess phase doesn't have this
issue (a wrong guess only disables the guessed button, not all 3), so `Selected` persists
visibly there. Not treated as a bug - the Pressed flash alone already satisfies "something
happens when you pick one," and changing `SetButtonsInteractable` to spare the clicked
button's own interactability would be a gameplay-flow change outside this feature's scope.

## CartoonGUIPack button hover/press/selected feedback

Every button in the game *except* `ActionButton.prefab` (the action/guess buttons above)
shares one prefab, `NewButton.prefab` (guid `f51975003815b6140914541ab193a90c`) - a
CartoonGUIPack sprite named "Gray" (`fileID: 21300000, guid: b4b024c6ca6464483b99578de7edfd49`,
Sliced) - a neutral, tintable shape, not pre-colored art; the dark brown look comes
entirely from the Image's own `Color` value multiplying it down. Used ~200 times combined
across MainMenu.unity, Story.unity, and Game.unity (Quit, Continue, the Clues/Suspects
toggle, every MainMenu nav button, etc.) - confirmed via search that none of those
instances override the Button component's own fields, so a single prefab-asset edit
reaches every instance at once, same as `ActionButton.prefab`.

This button had `m_Transition: 0` (**None**) - not just an invisible target graphic like
ActionButton's was, but no transition system engaged at all, so literally nothing happened
on hover or click anywhere in the game except the action buttons. First attempt set
`m_Transition` to `1` (Color Tint) with every state computed as a *percentage* of the
existing (very dark, low-saturation) `m_Color` - `Highlighted` ~1.35x, `Pressed` ~0.65x,
`Selected` ~1.18x - on the theory that preserving the exact resting color was safest.
Confirmed via screenshot + Inspector that this was wrong on two counts: (1) a modest
*relative* multiplier of an already-dark color is a tiny *absolute* shift - even the raw
color swatches in the Inspector looked nearly identical to each other, let alone the
on-screen result once rendered through the sprite's own shading, so hover/press read as
doing nothing at all; (2) the user confirmed the dark resting color itself (never changed
by this fix, only preserved) genuinely reads as too dark/near-black against the warm sepia
MainMenu background, not the intended "rich leather" look. Fixed by lightening the resting
color itself (`{0.447, 0.196, 0.114}`, a genuine chocolate brown instead of near-black) and
replacing the percentage-based derivation with large, decisive absolute jumps instead:
`Highlighted` a vivid warm gold (`{0.753, 0.451, 0.196}`, echoing the same amber accent
already used for button text and `ActionButton`'s own hover glow, so it reads as the
button actively "lighting up" rather than a subtle shade shift), `Pressed` noticeably
darker than resting (`{0.282, 0.118, 0.067}`), `Selected` a moderate persistent glow
between the two (`{0.588, 0.322, 0.157}`), `Disabled` the same resting RGB at ~0.5 alpha.
Also confirmed via this same Inspector screenshot that a "stale cached prefab from the
Editor being open throughout the edit" was NOT the cause, despite that being a real risk
this project has hit before (see the Unity/Editor gotchas section) - `Transition`/colors
in the Inspector already showed the first (too-subtle) attempt's exact values, proving the
reload had worked correctly; the actual bug was the color design itself, not caching.

## Loading scene: spinner + rotating detective-fiction facts

The Loading scene used to be static text with no motion for the entire ~1-3 min. main
story request - by request, addressed with two independent, decorative-only additions,
neither of which touches `LoadingController`'s actual fetch logic:

- **`LoadingSpinner`** (new Loading.unity object): an `Image` set to `Filled` /
  `Radial360`, animated by `RadialLoadingIndicator.cs` (`RequireComponent(typeof(Image))`)
  - the graphic stays still and `image.fillAmount` sweeps from `1` down to `0` over
  `cycleSeconds` (`3.5`s) on a repeating sawtooth
  (`1f - (Time.time % cycleSeconds) / cycleSeconds`), snapping straight back to full each
  time it empties - a stationary "pie" wipe rather than a spin. Went through two wrong
  sprite choices before landing here, both confirmed live rather than assumed:
  1. First attempt used the built-in `Background`/`UISprite` sprite (`fileID: 10905,
     guid: 0000000000000000f000000000000000` - already used elsewhere in this project, so
     a known-valid reference) with the graphic itself continuously rotated
     (`UIRotator.cs`, `transform.Rotate` in `Update`). Confirmed in a screenshot: this
     reads as a rounded *square* missing a corner, spinning - `Background`/`UISprite` is a
     rounded rect, not a circle, so rotating it just made the non-circular silhouette
     obvious. Switched to the stationary fill-wipe approach instead (still using the same
     sprite) to sidestep needing a true circle, and separately confirmed that sprite is
     also low-resolution (~20-30px source, meant for small buttons) - visibly
     blurred/pixelated stretched to fill a 140px spinner, unlike the SDF-rendered TMP text
     everywhere else on screen.
  2. Second attempt cleared the sprite reference entirely (`m_Sprite: {fileID: 0}`),
     assuming `Image` would fall back to its internal flat white texture and keep
     filling correctly. Confirmed live: it did not - `Image.OnPopulateMesh` early-outs to
     `base.OnPopulateMesh` (a plain, un-filled rectangle) whenever `overrideSprite ==
     null`, so the entire `Type`/`FillMethod`/`FillAmount` code path is skipped without a
     sprite - the spinner went completely static, `fillAmount` no longer had any visible
     effect at all. A `Filled` `Image` requires a real, non-null sprite to animate.
  3. Settled on `Assets/GUIPackCartoon/Demo/Sprites/Shapes/Shapes/Circle/Circle -
     256px.png` (`fileID: 21300000, guid: db8ebd87bc8b2430a8e1c33257b464e6`) - an actual
     circle, already imported as part of the existing GUIPackCartoon asset pack (so no new
     art was needed), and high enough resolution (256px source, downscaled to the 140px
     UI element rather than upscaled) to stay crisp. Fixes the shape and the blur
     complaints at once, while keeping the working fill-wipe animation from step 1.
  Once working, the wipe direction itself went through two refinements by request:
  - Originally a sawtooth (`fillAmount` linearly `1→0` over `cycleSeconds`, then an
    instant snap back to `1`); changed to `Mathf.PingPong(Time.time, cycleSeconds) /
    cycleSeconds` so it smoothly fills `0→1` and empties `1→0` in turn with no snap in
    either direction.
  - Confirmed by request that ping-ponging `fillAmount` still reads wrong: Unity's Radial
    fill always pins one edge at the fixed `FillOrigin` and only moves the other edge as
    `fillAmount` changes, so decreasing `fillAmount` after an increase makes the visible
    moving edge retrace backward over the same arc it just swept - it looks like the
    animation "undoes itself" at each turn rather than continuing to progress. Fixed by
    tracking a continuous, ever-increasing lap counter (`Time.time / cycleSeconds`) and
    flipping `image.fillClockwise` every lap instead of reversing `fillAmount`'s
    direction of travel: on "filling" laps, `fillAmount` counts `0→1` in the lap's
    rotational direction (pie grows forward from the origin); on "clearing" laps,
    `fillClockwise` is flipped and `fillAmount` counts `1→0`, which - because the fill
    direction flipped too - makes the *gap* open up starting at that same fixed origin
    and grow forward in the exact same rotational sense the fill was just advancing in.
    The moving edge's on-screen direction is therefore identical across both modes; only
    whether it's revealing or hiding the pie changes at each lap boundary. `cycleSeconds`
    is the duration of one lap (one full fill or one full clear), not a round trip.
  Positioned centered below the main "Generating your case..." status text.
- **`LoadingFactText`**: a bottom-center `TMP_Text`, italicized and dimmed relative to
  the main status text, driven by new `LoadingFactsRotator.cs` - starts on a random
  index into a fixed array of 18 detective-fiction trivia facts, then walks the array in
  order (wrapping) every `intervalSeconds` via `InvokeRepeating`, so nothing repeats
  until the whole list has been shown once. Purely cosmetic/no gameplay coupling, same
  reasoning as the spinner. Confirmed too fast at the original 10s default - raised to
  16s so each fact has a comfortable read.

Both objects live directly under `Loading.unity`'s own `Canvas` (not on the
`LoadingManager` GameObject that survives `DontDestroyOnLoad`), so they're destroyed
normally when `OnStorySuccess` loads the Story scene - exactly like the existing
`statusText`, and correctly, since only the background HTTP follow-up requests need to
survive the scene change, not this decorative UI.

**Not yet verified in the Editor** - built directly in the `.unity`/`.prefab` YAML by
hand while the Editor was closed (same reasoning and same caveat as the earlier
scrollable-output work: a second Editor instance can't open the same project
concurrently), with every new fileID cross-reference checked for internal consistency
and no collisions against existing IDs in either file. Confirm on next Editor open: the
spinner's pie wipe reads clearly as a loading indicator (not just a shape quietly
resizing), the action/guess buttons show a visible warm glow on hover and a flash on
click, and the footer fact line changes every 10 seconds through all 18 facts without
repeating early.

## Unity/Editor gotchas hit while building this

- **RunPod `/runsync` has its own internal wait limit** (~90s), separate from our
  client-side `UnityWebRequest.timeout`. If queue+execution time runs long, it
  returns early with a "still processing" status instead of output over the *same*
  HTTP response, even though the job keeps running server-side. Handled by
  `LLMStoryClient.PollForCompletion` falling back to `/status/{id}` polling.
- **Can't run Unity in `-batchmode` while the Editor already has the project open** —
  two instances can't share a project. If you need a headless compile check, the
  Editor must be closed first.
- **`SerializedObjectNotCreatableException` / `NullReferenceException` in
  `GameObjectInspector.OnDisable`** after repeated Play-mode toggles in one Editor
  session is a known Unity Editor bug (stale Inspector cache), not a project bug.
  Fix: restart the Editor. Don't chase it as a code issue.
- **Children inside a `Vertical Layout Group` must NOT have stretched anchors.** A
  stretched child (`anchorMin≠anchorMax`) fights the layout group's attempt to set
  `sizeDelta` directly and collapses to a near-zero size. Anchors must be a single
  point (e.g. `(0,1)`/`(0,1)`).
- **A wrapper GameObject with no `ILayoutElement` component of its own reports no
  size to its parent's Layout Group.** If a scroll-view item is a plain empty
  wrapper around a `TextMeshProUGUI` child, either (a) flatten it — put the TMP
  component directly on the object that's the Layout Group's direct child (preferred,
  simpler), or (b) give the wrapper its own `Vertical Layout Group` +
  `Content Size Fitter` to relay the child's size upward.
- **Scroll view content sizing**: `Content` needs `Vertical Layout Group` (Control
  Child Size Width+Height, Force Expand Width only) + `Content Size Fitter`
  (Horizontal Unconstrained, Vertical Preferred Size), anchors `Min(0,1)/Max(1,1)`,
  Pivot `(0.5,1)`. To force scroll position to the top after content changes:
  `LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect)` then
  `scrollRect.verticalNormalizedPosition = 1f` (or set `contentRect.anchoredPosition`
  directly, which is more reliable — see `StoryDisplay.cs`).
- TMP Auto Size (not code) is what's used for action buttons whose text length
  varies — no character-count-based manual resizing needed there.

## Not yet implemented

- Follow-up requests (`RequestInitialActions`/`RequestSuspects`/`RequestClues`) don't
  set a random `seed` (see table above).
