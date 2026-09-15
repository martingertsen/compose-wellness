using ComposeWellness.Services;

namespace ComposeWellness.Tests.Fakes;

public sealed class RecordingUpdateLog : IUpdateLog
{
    public List<string> AppLines { get; } = [];
    public List<string> ProcessLines { get; } = [];

    public void App(string text) => AppLines.Add(text);
    public void Process(string text) => ProcessLines.Add(text);
}
