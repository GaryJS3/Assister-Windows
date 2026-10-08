# Assister Android Feature and Windows Port Handoff

Source reviewed: Android repository `progress.md`, `README.md`, the project specification, and current Kotlin implementation. This document describes behavior present in the Android app, distinguishes incomplete areas, and translates Android-specific mechanisms into Windows-port requirements. It is intended as a product/engineering handoff; it does not prescribe copying Android UI or platform APIs.

## Product summary

Assister Android is a native client for the Assister rich-client API. Its main experience is a live conversation: configure a server and bearer token, send typed text or buffered microphone audio, see execution and final-answer events as they arrive, reconnect and replay without duplicating the request, inspect context/trace, and optionally hear server-generated speech. It can also be selected as Android's system voice assistant. The server remains authoritative for conversation history, transcription, model/tool execution, and generated speech.

## Implemented user-facing features and how they work

### 1. Server setup and authentication

- Settings accepts a server origin and client bearer token. The app connects to `/api/client`, negotiates protocol v1 at `/protocol`, reads the server feature list, and enables voice, execution details, or streamed model output only when advertised.
- HTTPS is required by default; HTTP is allowed for localhost and private IPv4 development-LAN hosts. URLs with paths, query strings, fragments, or embedded credentials are rejected. HTTP redirects are disabled for authenticated API and audio requests.
- The endpoint, current conversation ID, unsent draft, and pending idempotent request are stored locally. The token is encrypted using AES-GCM with a key held by Android Keystore. Backup/device transfer is disabled/excluded for private state.
- Changing endpoint/token clears local response-audio cache and current conversation when safe. It will not switch servers while a submission is unconfirmed.

### 2. Conversation and typed chat

- On connect, the app creates a conversation if there is no saved one, otherwise restores that conversation, fetches its interaction snapshots, and restores the active or most recent interaction from server events.
- The chat shows each user input and corresponding Assister card. Drafts persist locally, are capped at 1,000 characters, and the input gives immediate pending/sending feedback.
- New Chat asks the server for a new conversation and clears the app's cached response audio. Current UI supports one current conversation, not a conversation list or arbitrary history picker.
- Responses render incrementally from `response.delta`; `response.completed` replaces provisional accumulated text with the authoritative final body.

### 3. Live execution timeline and inspection

- A pure reducer applies sequenced protocol events to interaction state outside the UI. It separately tracks answer, input/transcription, execution steps, context, connection, model-round output, audio, and errors.
- Semantic progress and steps are shown while work runs. Steps retain server IDs, parent IDs, kind, label, summary, status, timestamps, and duration. Nested children are indented. A compact timeline draws proportional timing bars and updates elapsed time every 100 ms while an interaction runs; it uses server timestamps/durations rather than inventing timing.
- Completed execution collapses to a step-count affordance; users can reopen it. Individual step rows expand to show summary, input, output, metadata, and truncation status.
- Context inspection fetches context records with source, model rounds, provenance, and truncation. Historical interactions can lazily replay events to reconstruct details before inspection. Trace inspection uses the authenticated trace endpoint; missing trace support is handled as unavailable.
- Ordinary model-round text is a distinct channel from both provider reasoning and the final answer. The client incrementally concatenates correlated `model.output.started/delta/completed` fragments by step ID, deduplicates by event sequence, retains failure/cancellation partial text and truncation, and shows provisional output during tool work. It falls back to `step.updated.output.assistantContent` or trace details with older servers. It does not display `reasoning.delta` as ordinary output.
- Unknown event types are ignored for presentation but advance the event cursor, preserving forward compatibility. Event gaps force replay/reconnect.
- No raw-event developer console is currently implemented. Sensitive trace/context payloads stay in UI state and are not written to ordinary Android logs.

### 4. Markdown and presentation

- Final answers and ordinary model-round text render CommonMark plus GitHub tables and strikethrough: emphasis, headings, nested ordered/bullet lists, quotes, inline/fenced/indented code, links, horizontal rules, and aligned tables.
- Text remains selectable. Wide tables and code blocks scroll horizontally. Incomplete markup is reparsed as streamed text changes. Tool payloads and metadata remain literal monospace data.
- Raw HTML is displayed as text; Markdown image markup displays alt text only and does not fetch remote images. Clickable links are limited to HTTP, HTTPS, and mailto schemes.
- The layout follows streaming output while the user stays near the bottom; dragging upward disables auto-follow until they return to the bottom.

### 5. Reconnect, replay, retry, and cancellation

- A WebSocket streams events for an interaction after its last applied sequence. The client reconnects with exponential backoff from 1 to 15 seconds and resumes after the cursor. The server event log is replayed; duplicate sequences are suppressed, gaps trigger recovery, and only one interaction remains in flight.
- Recoverable socket interruption immediately shows Reconnecting. A visible error banner is delayed until the connection has remained unavailable for five seconds; recovery clears the banner. Authentication, unsupported protocol, and not-found errors surface immediately.
- The current interaction and event/render state are owned by an application-scoped ViewModel, not an Activity, so rotation/Activity recreation does not resubmit. On process recreation, the saved conversation ID is used to fetch authoritative snapshots and replay from sequence zero. The server remains durable source of history; there is no offline history cache.
- Before sending, the app durably stores the request body and UUID idempotency key. A failed/unconfirmed send can retry using that same key. Voice upload stores the uploaded attachment ID so retry does not upload again. Cancel send discards only the local unconfirmed request/key/audio and does not replay it to discover whether the server accepted it; if the server already accepted work, use remote Cancel once its interaction ID is known.
- Active server work has a separate Cancel action. The UI immediately shows Cancelling, sends the authenticated interaction cancellation request, and then follows server terminal status. Cancelling an uncertain send is intentionally not represented as guaranteed remote cancellation.

### 6. Voice input

- The app requests microphone permission at point of use. Recording captures 16 kHz, mono, signed 16-bit PCM and caps input at 30 seconds.
- Normal voice capture automatically ends after detected speech followed by 1.4 seconds of quiet. The detector evaluates 20 ms RMS-energy frames, requires 120 ms of activity, slowly learns a noise floor, and reports no speech after eight seconds. This detects acoustic activity, not words. Manual Finish & Send and Discard are available; backgrounding the app discards an active recording.
- Captured audio is buffered locally, then uploaded to the server's attachment endpoint. The request references its returned attachment ID, includes `speak=true`, and uses the same durable idempotency/retry logic. Server-side transcription events update the visible input; transcription is not performed locally. Streaming microphone transport is not implemented.
- For voice requests, server-generated audio is automatically played once when ready and the app is foregrounded. Text remains visible independently of audio readiness.

### 7. Voice response playback

- Requests ask the server for speech. The client observes TTS events and downloads response WAV from the fixed authenticated interaction audio route, not a URL supplied by an event.
- Download is bounded to the protocol maximum (8 MiB plus WAV header), checks RIFF/WAVE header, writes a temporary file then atomically publishes into private no-backup storage. Failures do not erase/replace the text answer and offer retry. Cache keys are derived from server, conversation, and interaction IDs.
- Voice answers autoplay once in foreground; typed answers and restored history remain silent. A speaker control replays cached audio; an active/preparing playback exposes stop. Playback reports started/terminal state with a unique playback ID to the server.
- Android audio focus is transient and speech/assistant-classified. Playback stops on app backgrounding, focus loss, microphone recording, another playback, or headphone removal. Starting a new conversation or changing server/credential cancels downloads and deletes local cache.

### 8. Android system-assistant mode

- Android declares a `VoiceInteractionService`, `VoiceInteractionSessionService`, and session, with the protected system binding permission. Settings opens the OS assistant-role chooser, with a system-settings fallback.
- A system invocation opens a compact bottom assistant Activity, requests microphone permission when needed, and starts listening once connected and idle. Existing work is displayed rather than duplicated. The compact surface shows voice state, progress, Markdown response, timeline, playback and finish/discard/cancel/retry controls.
- Expand opens the full conversation using the same process-scoped ViewModel, interaction, recording/playback ownership, and state; it does not submit again. Closing/backgrounding discards unsubmitted assistant capture; accepted server work continues.
- The voice service lives in a separate `:voice` process and does not itself initialize networking or microphone state. There is no custom wake word, lock-screen behavior, or screen-context capture.

### 9. APK updates (Android-specific)

- Settings shows installed version/build and offers a manual update check against `/api/updates/assister/android/check`. Update checks/downloads do not send the chat credential.
- Download is streamed to private cache with progress and validated against advertised size and SHA-256, application ID, higher Android versionCode, and exact installed signing certificate. Same-host download URLs are enforced; redirects are disabled.
- Install passes the verified APK to Android's package installer using a narrowly scoped FileProvider. Android shows the user its installer confirmation and may require unknown-app install permission. No background polling occurs.
- A repository PowerShell publisher builds the debug APK, reads packaged version using Android SDK `aapt`, uploads raw APK bytes with a separate development upload token, then verifies returned metadata and unauthenticated same-host download against local size/hash. VersionName follows `year.month.day.release` in America/New_York; Android versionCode increases monotonically.

## Server contract and event behavior to preserve

The Android client is deliberately thin. Keep conversation persistence, STT, model selection/execution, tool/agent routing, context selection, and TTS generation server-side. Port against the current canonical rich-client protocol, not by copying Android implementation assumptions. The current client uses these API families:

- `/api/client/protocol` for v1 and feature advertisement
- conversation create/list-by-ID and interaction submission with idempotency key
- interaction snapshot, paged/replayed events, and WebSocket stream after sequence
- interaction cancel, context, trace, audio download, playback reports
- attachment upload for buffered microphone PCM

The client currently recognizes: interaction lifecycle, STT start/partial/final, response start/delta/completed, step start/update/completed/failed, context add/update, TTS lifecycle/audio/failure, model output start/delta/completed, subscription ready, and heartbeat. Sequence order is authoritative; do not double-append events on reconnect. Feature-detect optional `audio.input`, `execution.details`, and `model.output.stream` capabilities.

## Not implemented yet in Android (future or incomplete)

- Conversation chooser/list and opening arbitrary historical conversations from the UI (the server owns history, but app opens one saved current conversation).
- Attachments: file/photo picker, camera, previews/removal, upload progress/retry, and staged share-target content.
- Dynamic client/device naming and capability registration/advertisement; device capability dispatcher and structured device request/results, permissions, cancellation, notifications/deep links, location, clipboard, intents, and device information.
- Screen/assist context capture, consent/share state, and generic protocol conversion.
- Offline history cache and expired-cursor recovery; server snapshots and replay are used instead.
- Retry/regenerate completed requests (unavailable in current protocol v1).
- Raw event/developer diagnostics console and structured categorized logging.
- Adaptive tablet/multi-pane/landscape UX and API 26/OEM validation.
- Streaming microphone transport; input is buffered PCM upload. Local wake word/custom always-listening behavior is intentionally absent.

Android's own tracker notes that API 26, tablet, several physical-phone reconnect/cancellation/audio-focus edge cases, and final phone acceptance for some behaviors remain unverified. Automated API 37 emulator/unit evidence is recorded in `progress.md`; source presence should not be presented as broad Windows/platform validation.

## Windows port: implementation guidance

- Keep C# as requested by the Windows project, but reproduce the product behavior rather than Android architecture. Separate protocol DTOs/client, event reducer/state store, persistence/credential protection, audio capture/playback, update/install, and UI.
- Prefer one shared interaction state owner/view model independent of window/page lifetime. The compact assistant/full chat handoff maps to Windows window/pane transitions and must retain the same interaction ID, cursor, audio state, and pending request.
- Use OS-protected credential storage (for example Windows Credential Manager or DPAPI-backed storage), never plain settings/logs. Persist draft and unconfirmed request body/key before sending. Match the same URL restrictions appropriate to the Windows deployment; require HTTPS except explicitly trusted development origins.
- Reuse the server's protocol semantics: negotiate and feature-detect; submit with idempotency key; reduce ordered events; replay and deduplicate; immediately acknowledge local actions; stream final response and ordinary model-round output separately; preserve errors/truncation/parent-step detail; gracefully ignore unknown event types while advancing sequence.
- Build a responsive conversation surface with independent answer, timeline, context, trace, connection, voice and playback state. Keep selectable Markdown rendering and safe link/HTML/image behavior. Auto-follow only while the user has not scrolled away.
- Implement voice using Windows audio APIs with format conversion to the server-required 16 kHz mono signed PCM. Provide permission/device error handling, finish/discard, 30-second maximum, endpointing if desired, and clear distinction between local endpoint detection and server transcription. Implement TTS WAV validation/cache, manual replay/stop, voice-request autoplay policy, and audio-device/focus behavior suitable for Windows.
- Provide a Windows-native update mechanism (such as MSIX/Store/package updater appropriate to the Windows app's distribution). Do not carry over Android APK endpoints, FileProvider, unknown-source permission, signing-certificate comparison, or Android upload credential flow. Verify signed package identity and update integrity using Windows distribution conventions.
- Android phone-assistant role/services are platform-only and should not be copied. Decide separately whether Windows voice activation, push-to-talk/global shortcut, or compact overlay is desired and supported; do not imply a custom wake word exists in Android.
- Implement missing product features only if the Windows product scope calls for them; the Android future-milestone list is not proof those features exist.

## Suggested Windows parity checklist

1. Configure endpoint and protected bearer token; connect and display connection/errors.
2. Restore/create current conversation; send typed text with immediate optimistic/pending state.
3. Reduce event sequence and show live steps and streaming final output; render Markdown safely.
4. Implement reconnect/backoff/replay, dedupe/gap recovery, durable idempotency retry, and distinct active-work cancel vs uncertain-send discard.
5. Restore state on window recreation/process restart without duplicate submission.
6. Add context/trace and per-step model/tool details with truncation and provenance.
7. Add buffered microphone capture/upload, transcription display, response-audio download/playback controls.
8. Choose Windows update/package channel and validate signed updates.
9. Explicitly defer/plan attachments, conversation chooser, device capabilities, screen context, diagnostics, and large-screen-specific polish.

## Key Android implementation map

- `app/src/main/java/net/thegary/assister/protocol/Interaction.kt`: interaction/event models and ordered reducer.
- `.../network/AssisterClient.kt`: authenticated REST, protocol, audio upload/download, WebSocket.
- `.../interaction/ConversationViewModel.kt`: app-scoped conversation lifecycle, submissions, replay, retry, inspection, voice/audio, cancellation.
- `.../settings/ClientSettings.kt`: endpoint/draft/pending persistence and Keystore-encrypted credential.
- `.../ui/ConversationScreen.kt`, `ActivityTimeline.kt`, `MarkdownText.kt`, `AssistantContent.kt`: chat, controls, timeline/trace/context, Markdown and compact assistant.
- `.../voice/VoiceRecorder.kt`, `SpeechEndpointDetector.kt`, `ResponseAudioPlayer.kt`: microphone capture, acoustic endpointing and playback.
- `.../assistant/`: Android system-assistant integration.
- `.../updates/` and root `Publish-DebugApk.ps1`: Android APK updater/publisher.
- `progress.md`: dated implemented/validation evidence and remaining platform checks.
