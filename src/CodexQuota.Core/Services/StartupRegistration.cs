namespace CodexQuota.Core.Services;

/// <summary>
/// Registry-backed start-at-login adapter. The desktop implements it against
/// <c>HKCU/Software/Microsoft/Windows/CurrentVersion/Run</c>; tests substitute a fake
/// so quoting and toggle/cleanup behavior can be verified without touching the machine.
/// The registration (not a saved preference) is always the source of truth for the UI.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Name of the per-user run value owned by this widget.</summary>
    string ValueName { get; }

    /// <summary>True when the widget-owned run value currently exists.</summary>
    bool IsEnabled();

    /// <summary>Writes the widget-owned run value. Returns false when the write fails.</summary>
    bool Enable(string command);

    /// <summary>Removes only the widget-owned run value.</summary>
    void Disable();
}

/// <summary>Quoting helpers for a per-user run entry.</summary>
public static class StartupCommandLine
{
    /// <summary>
    /// Quotes an executable path so a Windows <c>Run</c> entry with spaces or Unicode
    /// resolves to a single command. Surrounding quotes are preserved by the shell.
    /// </summary>
    public static string Quote(string executablePath)
    {
        string trimmed = executablePath.Trim();
        if (trimmed.Length == 0)
        {
            return "\"\"";
        }

        return trimmed.StartsWith('"') && trimmed.EndsWith('"') && trimmed.Length >= 2
            ? trimmed
            : "\"" + trimmed + "\"";
    }
}
