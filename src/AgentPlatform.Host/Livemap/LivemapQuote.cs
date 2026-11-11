namespace AgentPlatform.Host.Livemap;

public static class LivemapQuote
{
    // The agent's words are shown as a code block with its backticks swapped out, so they cannot link, mention or close the block.
    public static string Block(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        return $"```\n{LivemapBuildStore.CutOnCharacter(text.Replace('`', '\''), max)}\n```";
    }
}
