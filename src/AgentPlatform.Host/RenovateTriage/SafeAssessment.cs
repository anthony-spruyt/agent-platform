namespace AgentPlatform.Host.RenovateTriage;

public enum SafeAction
{
    Approve,
    CommentOnly,
    Fixable,
    Owner,
}

public sealed record SafeAssessment(SafeAction Action, string? FailedCheck = null);
