namespace CodexQuota.Core.LocalUsage;

/// <summary>
/// Resolves the local Codex home the same way the official client does: the
/// <c>CODEX_HOME</c> environment variable when set, otherwise the current user's
/// <c>.codex</c> directory. Only rollout records below <c>sessions</c> and
/// <c>archived_sessions</c> are ever inspected; credentials, configuration, history and
/// databases are never opened.
/// </summary>
public static class LocalUsagePaths
{
    public const string SessionsFolder = "sessions";
    public const string ArchivedSessionsFolder = "archived_sessions";

    /// <summary>Resolves the Codex home for the current process.</summary>
    public static string ResolveCodexHome(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        string? configured = environment("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? Path.Combine(".codex")
            : Path.Combine(profile, ".codex");
    }
}
