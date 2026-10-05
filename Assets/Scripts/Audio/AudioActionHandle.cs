public sealed class AudioActionHandle
{
    private readonly AudioSystem _system;
    public ActionAudioVoice Voice { get; }

    public bool IsActive => Voice != null && Voice.IsActive;

    internal AudioActionHandle(AudioSystem system, ActionAudioVoice voice)
    {
        _system = system;
        Voice = voice;
    }

    public void SetControl(in ActionControlFrame frame)
    {
        if (Voice != null) Voice.SetControl(in frame);
    }

    public void Stop(bool immediate = false)
    {
        if (Voice != null) Voice.RequestRelease(immediate);
    }

    internal bool Tick(float deltaTime)
    {
        if (Voice == null) return false;
        if (Voice.Tick(deltaTime)) return true;
        _system.ReleaseActionVoice(Voice);
        return false;
    }
}
