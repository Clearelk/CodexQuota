using CodexQuota.Core.Services;
using CodexQuota.Core.Providers;

namespace CodexQuota.Core.Transport;

/// <summary>Environment access seam so executable discovery is testable without a real machine layout.</summary>
public interface IExecutableEnvironment
{
    string? GetEnvironmentVariable(string name);

    IReadOnlyList<string> GetSearchDirectories();

    bool FileExists(string path);

    /// <summary>Full paths of subdirectories of <paramref name="directory"/>, or an empty list.</summary>
    IReadOnlyList<string> GetSubDirectories(string directory);

    bool IsWindows { get; }
}

/// <summary>Production environment probe.</summary>
public sealed class SystemExecutableEnvironment : IExecutableEnvironment
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public IReadOnlyList<string> GetSearchDirectories()
    {
        string path = GetEnvironmentVariable("PATH") ?? string.Empty;
        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<string> GetSubDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory)
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public bool IsWindows => OperatingSystem.IsWindows();
}

/// <summary>
/// Resolves the installed official Codex client without hard-coding a specific
/// machine layout. Preference order:
/// <list type="number">
/// <item>an explicit configured executable,</item>
/// <item>a native <c>codex.exe</c> on PATH,</item>
/// <item>the standard npm native package under a PATH entry,</item>
/// <item>Node plus the official JavaScript launcher.</item>
/// </list>
/// Arguments are always passed structurally; no <c>.cmd</c>/<c>.ps1</c> text is
/// executed and no user-specific path is persisted as a default.
/// </summary>
public sealed class CodexExecutableResolver
{
    private const string VendorRelativePath =
        "node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe";

    private const string VendorFlatRelativePath =
        "node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe";

    private const string LauncherRelativePath = "node_modules/@openai/codex/bin/codex.js";

    private readonly IExecutableEnvironment _environment;

    public CodexExecutableResolver(IExecutableEnvironment? environment = null) =>
        _environment = environment ?? new SystemExecutableEnvironment();

    /// <summary>Resolves a launch spec or throws a sanitized, actionable failure.</summary>
    public CodexLaunchSpec Resolve(string? configuredPath)
    {
        if (TryResolve(configuredPath, out CodexLaunchSpec? spec, out QuotaProviderException? failure) && spec is not null)
        {
            return spec;
        }

        throw failure ?? QuotaProviderException.CliNotFound(new AppMessage("CliNotFound"));
    }

    /// <summary>Non-throwing resolution used by settings/help UI.</summary>
    public bool TryResolve(string? configuredPath, out CodexLaunchSpec? spec, out QuotaProviderException? failure)
    {
        spec = null;
        failure = null;
        QuotaProviderException? nodeFailure = null;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string candidate = configuredPath.Trim();
            if (_environment.FileExists(candidate))
            {
                if (string.Equals(Path.GetFileName(candidate), "codex.exe", StringComparison.OrdinalIgnoreCase))
                {
                    spec = NativeSpec(candidate);
                    return true;
                }

                if (candidate.Replace('\\', '/').EndsWith("/node_modules/@openai/codex/bin/codex.js", StringComparison.OrdinalIgnoreCase))
                {
                    return TryResolveNodeLauncher(candidate, out spec, out failure);
                }
            }

            string? parent = SafeParentDirectory(candidate);
            if (parent is not null && TryResolveFromRoot(parent, out spec, out nodeFailure))
            {
                return true;
            }
        }

        foreach (string root in _environment.GetSearchDirectories())
        {
            if (TryResolveFromRoot(root, out spec, out QuotaProviderException? rootFailure))
            {
                return true;
            }

            nodeFailure ??= rootFailure;
        }

        foreach (string root in GlobalPackageRoots())
        {
            if (TryResolveFromRoot(root, out spec, out QuotaProviderException? rootFailure))
            {
                return true;
            }

            nodeFailure ??= rootFailure;
        }

        // Official standalone installs keep versioned native builds under LocalAppData.
        foreach (string candidate in StandaloneInstallCandidates())
        {
            if (_environment.FileExists(candidate))
            {
                spec = NativeSpec(candidate);
                return true;
            }
        }

        failure = nodeFailure ?? QuotaProviderException.CliNotFound(new AppMessage("CliNotFound"));
        return false;
    }

    private IReadOnlyList<string> StandaloneInstallCandidates()
    {
        var candidates = new List<string>();
        string? localAppData = _environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(localAppData))
        {
            return candidates;
        }

        string binDirectory = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        foreach (string versionDirectory in _environment.GetSubDirectories(binDirectory))
        {
            candidates.Add(Path.Combine(versionDirectory, "codex.exe"));
        }

        return candidates;
    }

    /// <summary>Locates Node for the official JavaScript launcher.</summary>
    public string? ResolveNodeExecutable()
    {
        string fileName = _environment.IsWindows ? "node.exe" : "node";

        foreach (string root in _environment.GetSearchDirectories())
        {
            string candidate = Path.Combine(root, fileName);
            if (_environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        foreach (string? programFiles in new[]
        {
            _environment.GetEnvironmentVariable("ProgramFiles"),
            _environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        })
        {
            if (string.IsNullOrEmpty(programFiles))
            {
                continue;
            }

            string candidate = Path.Combine(programFiles, "nodejs", fileName);
            if (_environment.FileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool TryResolveNodeLauncher(string launcherPath, out CodexLaunchSpec? spec, out QuotaProviderException? failure)
    {
        spec = null;
        failure = null;

        string? node = ResolveNodeExecutable();
        if (node is null)
        {
            failure = QuotaProviderException.NodeNotFound(
                new AppMessage("NodeMissing"));
            return false;
        }

        var arguments = new List<string>(CodexLaunchSpec.AppServerArguments.Count + 1) { launcherPath };
        arguments.AddRange(CodexLaunchSpec.AppServerArguments);
        spec = new CodexLaunchSpec(node, arguments, launcherPath);
        return true;
    }

    private bool TryResolveFromRoot(string root, out CodexLaunchSpec? spec, out QuotaProviderException? nodeFailure)
    {
        spec = null;
        nodeFailure = null;

        foreach (string relative in new[] { VendorRelativePath, VendorFlatRelativePath })
        {
            string nativePath = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), relative.Replace('/', Path.DirectorySeparatorChar));
            if (_environment.FileExists(nativePath))
            {
                spec = NativeSpec(nativePath);
                return true;
            }
        }

        if (_environment.IsWindows)
        {
            string exe = Path.Combine(root, "codex.exe");
            if (_environment.FileExists(exe))
            {
                spec = NativeSpec(exe);
                return true;
            }
        }

        string launcher = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), LauncherRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (_environment.FileExists(launcher))
        {
            return TryResolveNodeLauncher(launcher, out spec, out nodeFailure);
        }

        return false;
    }

    private static CodexLaunchSpec NativeSpec(string executable) =>
        new(executable, CodexLaunchSpec.AppServerArguments, executable);

    private IReadOnlyList<string> GlobalPackageRoots()
    {
        var roots = new List<string>();
        foreach (string? variable in new[] { "APPDATA", "LOCALAPPDATA" })
        {
            string? value = _environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
            {
                roots.Add(Path.Combine(value, "npm"));
            }
        }

        return roots;
    }

    private static string? SafeParentDirectory(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(directory) ? null : directory;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

}
