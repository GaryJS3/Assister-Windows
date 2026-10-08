namespace Assister.Windows.App.Services;

// The v1 API has no explicit follow-up signal. Match the satellite question heuristic.
internal sealed class FollowUpVoiceState(bool voiceInput)
{
    private bool _question;
    private bool _interactionCompleted;
    private bool _playbackCompleted;
    private bool _consumed;
    private bool _abandoned;

    public void SetResponse(string text) => _question = text.Contains('?');
    public void CompleteInteraction() => _interactionCompleted = true;
    public void CompletePlayback() => _playbackCompleted = true;
    public void Abandon() => _abandoned = true;

    public bool TryConsume()
    {
        if (!voiceInput || !_question || !_interactionCompleted || !_playbackCompleted || _consumed || _abandoned)
            return false;
        _consumed = true;
        return true;
    }
}
