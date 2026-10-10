namespace AgentPlatform.Host;

public interface IExitCodeSetter
{
    void Apply(int exitCode);
}

internal sealed class EnvironmentExitCodeSetter : IExitCodeSetter
{
    public void Apply(int exitCode) => Environment.ExitCode = exitCode;
}
