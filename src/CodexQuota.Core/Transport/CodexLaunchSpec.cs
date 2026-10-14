namespace CodexQuota.Core.Transport;

/// <summary>
/// A resolved, ready-to-launch official Codex command. Arguments are passed
/// structurally (never shell-concatenated) so paths with spaces or Unicode are safe.
/// </summary>
public sealed record CodexLaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string DisplayPath)
{
    /// <summary>
    /// Arguments that select the app-server stdio transport while explicitly keeping
    /// the official <c>openai</c> provider and the official ChatGPT backend origin.
    /// </summary>
    public static IReadOnlyList<string> AppServerArguments { get; } =
    [
        "app-server",
        "--listen", "stdio://",
        "-c", "model_provider=\"openai\"",
        "-c", "chatgpt_base_url=\"https://chatgpt.com/backend-api/\"",
    ];
}
