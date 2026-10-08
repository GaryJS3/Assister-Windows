using Assister.Windows.App.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
foreach (var speechFirst in new[] { false, true })
{
    var state = new FollowUpVoiceState(voiceInput: true);
    state.SetResponse("Which room did you mean?");
    Require(!state.TryConsume(), "Listening began before speech and execution finished.");
    if (speechFirst) state.CompletePlayback(); else state.CompleteInteraction();
    Require(!state.TryConsume(), "Listening began with only one completion signal.");
    if (speechFirst) state.CompleteInteraction(); else state.CompletePlayback();
    Require(state.TryConsume(), "Voice follow-up did not reopen after both completions.");
    state.CompleteInteraction();
    state.CompletePlayback();
    state.SetResponse("Which room did you mean?");
    Require(!state.TryConsume(), "Duplicate events/manual replay reopened the microphone twice.");
}
foreach (var voiceInput in new[] { false, true })
{
    var state = new FollowUpVoiceState(voiceInput);
    state.SetResponse(voiceInput ? "The light is now on." : "Which room?");
    state.CompleteInteraction();
    state.CompletePlayback();
    Require(!state.TryConsume(), "Statement or typed/historical question opened the microphone.");
}
foreach (var abandonBeforeCompletion in new[] { false, true })
{
    var state = new FollowUpVoiceState(true);
    state.SetResponse("What brightness?");
    if (abandonBeforeCompletion) state.Abandon();
    state.CompleteInteraction();
    state.CompletePlayback();
    if (!abandonBeforeCompletion) state.Abandon();
    Require(!state.TryConsume(), "Stopped/cancelled/navigated interaction reopened the microphone.");
}
var corrected = new FollowUpVoiceState(true);
corrected.SetResponse("Which room?");
corrected.SetResponse("The office light is on.");
corrected.CompleteInteraction();
corrected.CompletePlayback();
Require(!corrected.TryConsume(), "Superseded question opened the microphone.");
var chain = new[] { "Which room?", "What brightness?", "Done." };
var reopened = 0;
foreach (var text in chain)
{
    var turn = new FollowUpVoiceState(true);
    turn.SetResponse(text);
    turn.CompleteInteraction();
    turn.CompletePlayback();
    if (turn.TryConsume()) reopened++;
}
Require(reopened == 2, "Multi-turn clarification did not stop after the final answer.");
Console.WriteLine("PASS: both speech/terminal orderings, one-shot follow-up, statements, typed/history questions, stop/cancel/navigation, authoritative correction, chained clarifications.");
