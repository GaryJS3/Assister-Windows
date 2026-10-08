# Local wake word detection

Open **Settings**, select **Set up wake word engine**, then select **Save and connect** when setup finishes. The app downloads and checksum-verifies the official English GigaSpeech KWS model, unpacks it into Local AppData, creates the keyword file, and selects local listening. No script, external extractor, or file paths are required. Download progress and Stop download are available; closing Settings cancels setup. A verified installation is reused on subsequent setup clicks. The initial wake phrase is **light up**, an upstream example suitable for checking detection. Listening is off by default until you save.

The earlier `Setup-WakeWord.ps1` remains an optional manual setup tool. Existing custom paths are preserved when opening Settings; select **Advanced: custom model and keywords** to edit them. Clicking setup explicitly selects the managed engine and its starter phrase.

Settings also shows the installed app version and **Check for updates**. This uses the saved server, downloads and verifies an available update, and reports the result. Close Settings after an update is ready; installation waits until there is no active interaction, unsent draft, or submission. Automatic startup/15-minute checks remain enabled. Both paths share one check at a time. Closing Settings during a manual check cancels that check. Wake word models remain in Local AppData across app updates.

Customize `keywords.txt` with model-token sequences, one phrase per line, and an optional `@label`. Plain English text is not a tokenized keyword. Follow the [official keyword preparation documentation](https://k2-fsa.github.io/sherpa/onnx/kws/index.html) and use the selected model's tokens/BPE data when creating other phrases, such as “hey Assister”. A different model requires its own matching keyword tokens. Model files stay outside the executable and are retained through app updates.

The default Windows microphone supplies 16 kHz mono PCM. Wake inference uses sherpa-onnx on CPU; wake audio stays local. Detection restores the window and replaces the text composer with **Listening....** and large **Send** / **Cancel** buttons without focusing a text field. Wake detection pauses while command recording owns the microphone. Typed drafts are preserved.

The microphone button beside Send starts the same voice flow manually, even when wake word listening is disabled. It pauses wake detection and response playback, shows the listening notice, and uses the same silence timeout, Send/Cancel actions, and transcript handling. A connected server with audio input support is required.

Command capture stops and sends automatically after approximately one second of silence following speech. Send also finishes and submits a recording immediately; Cancel discards it before submission. An eight-second no-speech timeout discards an empty recording, and recordings are capped at 30 seconds (then sent if speech was detected). Endpointing uses an energy threshold, so background noise may hold recording open and very quiet speech may not trigger it; physical microphone tuning is pending.

Automatic sending and the Send button upload buffered PCM to the saved server and submit a voice interaction through the same guarded path. Partial/final transcripts from the server replace the voice placeholder in the chat. There are no live words during capture: the canonical API transcribes only after upload. Failed submissions wait for an explicit retry and retain the same attachment ID and request key; cancelling after a failed send cannot undo a request already accepted by the server. Wake detection resumes after sending/cancelling. Command recordings remain in memory only. Response audio playback and listening after app exit are not implemented.

Windows may restrict foreground activation. Disable listening in Settings to release the microphone. Windows microphone privacy restrictions, unsupported device formats, or unplugged devices appear in the sidebar; save Settings again to restart after fixing them. Settings, new-conversation changes, and update installation wait until the voice session is finished.

For a repeatable setup and native inference check, run:

```powershell
dotnet run --project .\checks\WakeWordCheck -- --setup
dotnet run --project .\checks\WakeWordCheck -- "$env:LOCALAPPDATA\Assister\WakeWord\sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01" "$env:LOCALAPPDATA\Assister\WakeWord\managed-sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01\keywords.txt"
```

The setup check exercises real download/extraction, installation reuse, cancellation before starting, and bad-checksum rejection. Native detection needs the official WAV fixtures in the full model archive (the managed installation retains only runtime model files). The optional manual script installs those fixtures. It checks configuration rejection, silence, and streaming detection/reset. These checks do not validate live microphones or window activation.
