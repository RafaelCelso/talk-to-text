# Working on this repo

Read this before changing anything. It is written for a coding agent picking the project up
cold, and it is mostly a list of things that look wrong but aren't, plus things that look
fine and will bite you.

---

## What this is

Push-to-talk dictation. Hold a key, talk, release, and cleaned-up text is typed into
whatever had focus. Two independent implementations:

| | macOS | Windows |
|---|---|---|
| Language | Swift 6 | C# / .NET 10 |
| UI | SwiftUI | Avalonia |
| Speech | Apple `SpeechAnalyzer`, or Parakeet via FluidAudio | Parakeet via sherpa-onnx |
| Location | repo root | `windows/` |

**The macOS app works and is in daily use.**

**The Windows app is complete but has never run on real hardware.** Every layer exists;
CI builds it, runs 63 tests, publishes a single-file executable, launches it on Windows and
confirms the platform layer loads and constructs. What has never happened is a person
holding the key and speaking into a microphone. Describe it that way — not as "working",
not as "unfinished".

---

## The one rule that matters

**`shared/dictionary-test-vectors.json` is the specification for correction behaviour.**

Both implementations run it in CI. If you change how corrections work, change the vectors
first, watch both sides go red, then make them green. Changing one implementation to "fix"
a failing vector without changing the other is how the two silently diverge — and only one
of them can be exercised by hand.

```bash
swift test --filter VectorTests                    # macOS side
cd windows && dotnet test Murmur.CrossPlatform.slnf # Windows side, runs anywhere
```

The Swift copy at `Tests/MurmurDictionaryTests/dictionary-test-vectors.json` is a copy, and
CI fails if it drifts from `shared/`. After editing the shared file:

```bash
cp shared/dictionary-test-vectors.json Tests/MurmurDictionaryTests/
```

---

## Things that look like bugs and are not

**`dotnet build Murmur.sln` fails on macOS** with `NETSDK1073`. Expected —
`Murmur.Platform.Windows` targets `net10.0-windows`. Use `Murmur.CrossPlatform.slnf`, which
omits it; everything else, including the whole UI suite, builds and tests on macOS in about
half a second.

**`swift build` fails with "input file was modified during the build."** The repo lives in an
iCloud-synced folder and the sync engine touches files mid-compile. **Always build with
`make`**, which uses `--scratch-path` outside the synced tree. A bare `swift build` also
writes a `.build/` directory into iCloud, which makes every subsequent build minutes slower.
If you see this error, wait a few seconds and retry.

**Compare mode doesn't type anything.** By design — `Settings.compareMode` runs every engine
on one recording and shows them side by side. If both injected, two transcripts would fight
over one text field. This is the single most confusing behaviour in the app.

**The timing column isn't comparing like with like.** Apple and Parakeet are timed on local
compute with the clock started *after* model load. Wispr Flow's number is its own
`e2eLatency`, which includes a network round trip and its cleanup pass. Don't present them
as one ranking.

**`MainActor.assumeIsolated` will crash the process.** It does not check the claim, it
asserts it. Use `await MainActor.run` from any non-main-actor context. This took the app
down once already.

**Mutating `@State` inside a `Canvas` draw closure floods the log and corrupts state.** The
VU meter keeps its needle physics in a plain reference type the view merely holds, which is
invisible to SwiftUI's state graph. Don't "clean that up" into `@State`.

---

## Design system

`Sources/MurmurYouTube/UI/DesignSystem.swift` defines every colour, size, radius, duration
and material token. **Views must not contain literal values.** If a component needs a number
that isn't a token, add the token rather than inlining it.

The direction is 1980s field recorders — Sony TC-D5, Marantz PMD, Nakamichi, Braun. Silver
face in light appearance, black face in dark. Two rules that are not negotiable:

- **Red means recording.** Nothing else in the app is red.
- **Amber and green are instrumentation only** — level meters, never UI chrome.

Explicitly ruled out: neon, vaporwave, synthwave, purple/pink gradients, glowing text, chrome
lettering, grid horizons. There are **no gradients anywhere**; depth comes from flat panels,
hairline bevels and procedurally-drawn brushed grain.

---

## macOS specifics

**Code signing is load-bearing, not cosmetic.** TCC stores a code-signing *requirement* per
entry, not just a path. An ad-hoc signature changes every build, so the rebuilt binary stops
satisfying the stored requirement — and the symptom lies: the Accessibility toggle still
shows as **on** while the app is untrusted. The `Makefile` auto-detects a Developer ID via
`security find-identity`. Don't replace that with `--sign -`.

If a grant does get wedged, reset that one row — never toggle, and never omit the bundle ID:

```bash
tccutil reset Accessibility ai.pivotstudio.murmur-youtube
```

A bare `tccutil reset Accessibility` wipes every app on the machine. Then quit System
Settings entirely (⌘Q) before reopening; the Privacy pane caches its list.

**`log` may be shadowed in the user's shell.** Use `/usr/bin/log` explicitly.

**Don't run the `.app` from the repo folder.** It's iCloud-synced and the sync engine can
corrupt the signature. `make install` puts the running copy in `/Applications`.

---

## Windows specifics

The specifics below were expensive to establish and several were found the hard way. Treat
them as load-bearing. Full detail in `windows/README.md` and `docs/PARAKEET-WINDOWS.md`.

**Three pinned versions that break silently at "latest":**

| Package | Pin | Why |
|---|---|---|
| `NAudio` | 2.3.0 | 3.x targets .NET 9+ and will not restore |
| `Avalonia.Headless.XUnit` | 11.3.20 | 12.x requires xUnit **v3**, a different package line |
| `org.k2fsa.sherpa.onnx` | 1.13.5 | Bundles ONNX Runtime — never also reference `Microsoft.ML.OnnxRuntime` |

**Right Alt is AltGr** on German, Polish, UK, Nordic and most Latin-American layouts. Binding
push-to-talk there — and especially suppressing it — breaks typing `@`, `€`, `\`, `|` for
those users. Default is **Ctrl + Win** (one or two keys, configurable), and the hook
**observes without swallowing**: if the key-down is swallowed and the key-up escapes, the
target app believes Ctrl is held forever. The hook only reports raw key transitions; which
chord counts, hold vs. toggle, and "a third key cancels" live in `TalkToText.Core/Shortcut.cs`
where they are tested. Win/Alt releases are masked with an injected `vkE8` so the Start menu
does not open.

**The Windows app is now Talk-to-Text** (projects `TalkToText.*`, exe `TalkToText.exe`, data
in `%LOCALAPPDATA%\TalkToText`). Its UI follows OpenWhispr's MIT dark theme
(`TalkToText.App/Design/DesignTokens.cs`), not the macOS field-recorder design system — and
every surface is **glass**: translucent white frost (`GlassFill`, `GlassFillRaised`) with a rim lit
along the top (`GlassRim`), over an acrylic-blurred window. Use those tokens, not the old greys
(`Raised`, `Card`), for any new card, row or button. The radii stay OpenWhispr's (a test pins them).

**`SettingsData` uses setters, not `init`.** The source-generated JSON serializer writes
`default` into every missing `init` property, so an older settings file loaded with no
shortcut at all.

**The recording pill must never take focus** — the transcript is pasted into whatever has
focus. It is shown with `ShowActivated = false` and marked `WS_EX_NOACTIVATE`.

**Meeting transcription** (`TalkToText.Core/MeetingSession.cs`): the microphone is the user,
WASAPI loopback (`SpeakerAudioCapture`) is everyone else. Remote utterances are grouped by
WeSpeaker voice prints (threshold 0.65 — measured: same voice 0.86–0.94, different 0.50–0.55;
3D-Speaker CAM++ was unusable on the same clips). A meeting is a meeting app **with the
microphone open**, read from **active WASAPI capture sessions**. Not from the registry's
`CapabilityAccessManager` `LastUsedTime` values: on Windows 11 build 26300 those had stopped
updating, and a Teams call in progress went undetected. The registry is only a fallback. Reading Teams' active
speaker via UI Automation is experimental and has not been checked against a real call.

**The speakers' loopback goes through `BufferedWaveProvider`, which must have `ReadFully = false`.**
By default it pads an empty buffer with silence instead of returning 0, so the drain loop in
`WasapiAudioCapture.OnDataAvailable` never ended: the other people in a call were never
transcribed (their audio was pushed out by zeros) and the capture could not stop. The microphone
gets 16 kHz from the OS and never takes that path, which is why only one side was ever broken.
`MeetingSession` checks the stop token per chunk and disposes the VADs under a lock, because a
capture past its deadline once called into a disposed sherpa-onnx VAD and crashed the app.

**Stopping a meeting must never wait forever.** `MeetingSession.StopAsync` records and saves
`EndedAt` before anything else, and gives the sources 5 s to stop: a Teams call once ended and
stop never returned (most likely a UI Automation call into the closing window), so the meeting
read as live and its duration kept counting. Meetings cut off by the app being killed are closed
at startup by `MeetingStore.CloseUnfinished`, at their last message.

**Audio files** ("Transcrever áudio" page, `AudioFilesView` + `Core/AudioFileTranscriber.cs`): a
file is decoded by `Platform.Windows/AudioFileDecoder.cs` (NAudio `AudioFileReader`: WAV, MP3, and
M4A/AAC/WMA via Media Foundation) to 16 kHz mono, a second at a time, then cut by Silero VAD,
grouped by WeSpeaker voice prints and transcribed like a meeting. Results are `MeetingTranscript`s
with `SourceFile` set, in their own store (`audio-files`), and reuse `ChatView`. Attaching never
starts work: Transcribe and "Resumir com IA" are separate buttons, both interruptible; an
interrupted transcription is saved as "(parcial)". The Settings "Instrução para a IA"
(`SettingsData.AiInstruction`) goes into the summary's system prompt for audio files only; it
cannot change what Parakeet hears — that is the Dictionary's job. A transcript keeps the file's
full path (`SourceFile`; older ones only the name, hence "Localizar arquivo") and plays it with
`AudioPlayerBar` → `Platform.Windows/AudioFilePlayer.cs` (NAudio `WaveOutEvent`, mono). Speed
keeps the pitch through `Abstractions/TimeStretcher.cs` (WSOLA), which lives outside the Windows
layer so it is unit-tested; plain resampling would turn 2× speech into chipmunks. The seek bar is
`Controls/PlaybackTimeline.cs`: the recording's loudness (`Core/AudioEnvelope.cs`, computed in the
background) drawn with the recording pill's bars and glow, animated by a frame timer while playing.
Speed sits behind a gear flyout. **Playback belongs to the app, not a page**: `PlaybackHub` (owned by
`MainWindow`) holds the one file playing; every `AudioPlayerBar` asks it for the player each frame
and owns nothing, so leaving the transcript does not stop the audio (a bar that disposed its player
once crashed the app when its page came back). While audio is active and its transcript's bar is
off screen, `MiniPlayer` floats bottom-right in the window; while the window is minimised or in the
tray, `PlayerPillWindow` shows it at the bottom centre (lifted above the recording pill when both
are up). The pill window is built at startup — a test builds both layouts, because a layout bug
there stopped the app opening at all. While playing, `ChatView.FollowPlayback` highlights the message being heard and scrolls to
it; bubbles are tracked by index because saving a summary reloads the transcript as new objects.
The summary's "Dados citados" section is not written by the model (it kept answering "none"): the
app appends every line with a number, date or deadline, quoted verbatim with its time. Screenshots in test scripts use `PrintWindow`, never a screen
copy: the user's own windows can be on top. The player stops
when it leaves the screen, so the page must not rebuild the open transcript on every save.

**Live meeting window**: while a meeting is transcribed and the main window is minimised or in
the tray, `LiveTranscriptWindow` floats the live `ChatView` in the bottom-right corner (no focus,
like the pills); it collapses to the app icon with a red dot. On by default, switchable in
Settings (`SettingsData.ShowLiveTranscriptWindow`). Polled from `App` like the player pill.

**Chat page** (`ChatPage` + `Core/TranscriptAssistant.cs`): questions about meetings and audio
files, answered by the same local Qwen model. Transcripts are cut into passages (summary first,
then 12 messages each) and picked by shared words — a name or title in the question narrows to
those meetings, "última" to the newest of what is left — and capped at ~2,800 tokens: a full 8k
context took three minutes on a CPU, the cap brings it to under a minute. The model is loaded per
question and released, and refuses while a summary is being written (two would need ~5 GB).
Conversations are saved per tab (`Core/ChatStore.cs`, `chats` folder) and listed beside the chat
with the meeting or recording they were about; refilling the selector must not count as the user
picking another scope (`_opening`), or returning to the page would drop the open conversation.

**Search and export** (`Core/TranscriptSearch.cs`, `Core/TranscriptExport.cs`): the Meetings and
audio-file lists filter as you type — every word must appear somewhere, accents and case ignored —
and show the line where it was said. "Exportar" on a transcript writes Word (Office Open XML built by
hand, no library; checked by opening it in Word), Markdown or text, chosen by the file type in the
save dialog. **Voice prints are kept with each speaker** (`MeetingSpeaker.Voice`/`VoiceCount`, live
meetings and audio files alike), so naming someone after the meeting can still "Lembrar esta voz";
transcripts from before this have none and the option is hidden for them.

**PDF export looks like the app** (`Views/TranscriptPdf.cs`): drawn with SkiaSharp into Skia's PDF
backend — the dark shell, Inter loaded from `avares://Avalonia.Fonts.Inter`, the glass summary card
with spectrum-coloured topics, chat bubbles with avatars — so text stays selectable. App pixels ×
0.75 = points. Blocks (header, summary card, bubble) are laid out first and placed on A4 pages whole;
a summary or message longer than a page continues in another card or bubble. SkiaSharp 2.88's
`SKFont.MeasureText` takes glyph ids, not text: measure through an `SKPaint`. Check a published
build with `TalkToText.exe --export-pdf <path>` (newest meeting) and look at the pages with
`artifacts/pdf-para-png.ps1`, which uses Windows' own PDF renderer. The Windows 11 save dialog does
not expose its file-name box to UI Automation by the classic id 1001, so test scripts cannot fill it.

**The Chat is a conversation** (`TranscriptAssistant.IsSmallTalk`): greetings, thanks and questions
about the assistant are answered without reading transcripts; a question sharing no word with any
meeting gets no passages (it used to be padded with unrelated ones); a short follow-up is looked up
together with the question before it. The Chat borrows dictation's loaded model
(`LocalAiRewriter.AcquireAsync`), so there is no load wait between messages and never two copies.

**Dictation clean-up and command mode.** `Core/SpeechCleanup.cs` takes out "éé", "hã", "hum" and a
short word said twice, by rule and instantly (on by default) — never a bare "é", which is the verb.
"Revisar com IA" (`PolishWithAi`, off by default) sends each dictation through `ITextRewriter`
(`App/LocalAiRewriter.cs`, the same Qwen, kept loaded 5 minutes after use): ~5 s warm, ~12 s
cold on the Ryzen 4600H; too slow, failing, or a rewrite far shorter or longer than what was said,
and the dictation is typed as it was. **Command mode** (off by default, Shift + Win) is a second
`ShortcutDetector` in `DictationEngine`: what is said is an instruction, `ISelectionReader`
(`Platform.Windows/ClipboardSelectionReader.cs`) copies the selection with a synthetic Ctrl+C
*after the user's modifiers are up* (Ctrl+C with Shift down is Ctrl+Shift+C) and restores the
clipboard, and the AI's result is pasted over the selection; with nothing selected — read from the
clipboard sequence number, not from its contents — it writes what was asked. Hand-check pieces
with `TalkToText.exe --try-ai` and `--read-selection`. The first-run setup has a microphone step
that shows the level and what was heard, using the real shortcut.

**First-run setup** (`Views/OnboardingWindow.cs`): shown over the main window once, while
`SettingsData.OnboardingDone` is false — on a fresh install and, once, on settings files from before
it existed. A welcome, then five steps, each skippable: name, hold/toggle, shortcut (the same
`ShortcutPicker` Settings uses), the local AI download (~2 GB, keeps running if the user moves on)
and the AI instruction. Each "Continuar" saves its step at once; finishing, skipping everything or
closing the window marks it done. Settings → "Configuração inicial" opens it again.

**Meeting summaries are on demand only** — the user asked for a "Gerar resumo" button, not an
automatic summary; don't make it run after every meeting. They come from a local model, no API key:
Qwen2.5-3B-Instruct Q4_K_M (2.1 GB, downloaded on the first summary to
`%LOCALAPPDATA%\TalkToText\models\summary`) through LLamaSharp's CPU backend, about 6 tokens/s.
Long meetings are summarised map-reduce (`TalkToText.Core/MeetingSummarizer.cs`). **llama.cpp's
natives must sit loose beside the exe** in `runtimes\win-x64\native\<avx|avx2|avx512|noavx>\`:
LLamaSharp picks the CPU variant by path, which it cannot do inside the single-file bundle.
`TalkToText.App.csproj` keeps them out of the bundle and copies them straight from the NuGet
package after publish (re-marking publish items `ExcludeFromSingleFile` left them nowhere), and
drops the other OSes' copies (73 MB). The installer ships that folder; the self-test checks it loads.
**The AI runs on the graphics card through Vulkan** (`LLamaSharp.Backend.Vulkan.Windows`, folder
`runtimes\win-x64\native\vulkan`), chosen once at startup by `SummaryModel.ConfigureBackend` from
`SettingsData.UseGpuForAi` (on by default); without a Vulkan driver LLamaSharp falls back to the CPU
build, and a model that does not fit on the card reloads on the CPU. **The package's `ggml.dll`
imports a `ggml-cpu.dll` it does not ship** — without it Vulkan never loads and nothing says so — so
the publish copies the AVX2 one in, and Vulkan is only requested on AVX2 CPUs. Measured with
`TalkToText.exe --benchmark-ai cpu|gpu [batch]` on a Ryzen 5 4600H (integrated Radeon), a 2,000-token
Chat prompt: CPU 54 s (42 tokens/s read, 8.8 written), GPU 35 s (69 read, 9.8 written). Batch size
128–1024 made no difference. An integrated GPU shares system memory, so writing barely improves;
a dedicated card would gain far more.

**Never do slow work on the keyboard hook's thread**, and never `SendInput` from inside it.
Windows silently removes a hook whose callback runs ~1 s; the Start-menu mask and mic opening
are dispatched to the thread pool for that reason.

**Reflection-created platform types need unambiguous public constructors.** A public
`(bool)` beside a public `(string?)` made `Activator.CreateInstance(type, [null])` throw; the
published self-test caught it. Hence `SpeakerAudioCapture` rather than a flag.

**Icons must have DIB entries below 256 px.** Avalonia's Win32 icon reader fails on
PNG-only `.ico` files and the main window then throws on `Show()`.

**UI Automation cannot inject text.** `TextPattern` is documented read-only and
`ValuePattern` replaces a whole field rather than inserting at the caret. `SendInput` is the
primary path, not a fallback.

**`Murmur.App` loads the platform layer by reflection, not by reference.** A direct
reference would force the UI onto `net10.0-windows` and you would lose the ability to run it
on your own machine. Two consequences that have already bitten once: the assembly is
invisible to `PublishSingleFile`, so it is published as a loose file beside the exe *and*
resolved by an explicit `AssemblyLoadContext` handler; and the published self-test checks
this, because when it breaks the app starts perfectly and then does nothing at all when the
key is pressed.

**Keep `Murmur.Platform.Windows` logic-free.** Anything living there is code CI cannot
exercise. Retries, debouncing and device-change handling belong in the platform-neutral
projects behind an interface — those target plain `net10.0`, so `CA1416` turns any accidental
Win32 call into a build error.

**CI is the only place the Windows code is compiled.** Warnings are errors and the analyzers
are strict on purpose. `--no-incremental` is mandatory: Roslyn does not re-emit analyzer
warnings on a cached build, so without it the gate proves nothing.

---

## Regex, if you touch the dictionary

The two engines are not identical. Measured across 30 cases, **9 diverged**. Two affect this
code and are handled — don't remove either:

- `RegexOptions.CultureInvariant` on the C# side, or Turkish `İ` matches `i`.
- **NFC normalization on both sides.** macOS returns decomposed strings, so without it an
  accented trigger silently never fires.

Two more are unfixable and simply avoided: ICU folds `ß` to `ss` and .NET doesn't; .NET's `.`
splits surrogate pairs. Stay inside the safe subset — `\b`, `\d`, `\w`, `\s`, character
classes, greedy/lazy quantifiers, alternation, `(?<name>…)`, fixed-length lookbehind,
lookahead, `\p{L}`, and `$1`–`$9` in replacements. Nothing else.

---

## What isn't built

1. **Command Mode** on macOS — select text, hold a second key, "make this more formal." (Built on
   Windows; see "Dictation clean-up and command mode".)
2. **Onboarding** — a first-run window walking through the macOS permissions.
3. **Notarization** (macOS) and **code signing** (Windows). Both apps are unsigned for
   distribution, so Windows users will meet SmartScreen.
4. **An installer** for Windows, and model download from inside the app rather than by
   following `docs/PARAKEET-WINDOWS.md` by hand.

## What no amount of CI can verify

On Windows, nobody has yet held the key and spoken. Specifically unverified:

- Text injection landing in a foreground app — runners have an interactive desktop but
  cannot take the foreground.
- A real microphone: format negotiation, the OS privacy block, unplugging mid-capture.
- The keyboard hook firing on a physical keypress.
- Parakeet transcribing real speech, and whether ~2 GB resident is tolerable.

Everything those feed into is behind an interface and tested with fakes. The bindings
themselves are not. **First real-hardware run should start with `--selftest`, then a single
short dictation into Notepad.**
