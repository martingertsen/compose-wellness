namespace ComposeWellness.Models;

public enum UpdateState
{
    /// <summary>No update has run since the service started.</summary>
    Idle,
    Running,

    /// <summary>The update finished and every attempted stack succeeded or was skipped.</summary>
    Finished,

    /// <summary>The update finished but at least one stack failed, or the update itself could not run.</summary>
    Failed,
}
