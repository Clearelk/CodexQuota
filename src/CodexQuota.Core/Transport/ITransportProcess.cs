using CodexQuota.Core.Services;
using System.Diagnostics;
using System.Text;

namespace CodexQuota.Core.Transport;

/// <summary>
/// Owned child process abstraction. The transport owns exactly one of these per
/// refresh cycle and is responsible for terminating the whole tree on disposal.
/// </summary>
public interface ITransportProcess : IAsyncDisposable
{
    StreamWriter StandardInput { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    bool HasExited { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    void KillEntireTree();
}

/// <summary>Creates owned child processes. Injected so protocol tests can substitute a fake.</summary>
public interface ITransportProcessFactory
{
    ITransportProcess Start(CodexLaunchSpec spec);
}

/// <summary>Production process factory: hidden window, redirected UTF-8 stdio, no shell.</summary>
public sealed class ProcessTransportProcessFactory : ITransportProcessFactory
{
    public ITransportProcess Start(CodexLaunchSpec spec) => ProcessTransportProcess.Start(spec);
}

internal sealed class ProcessTransportProcess : ITransportProcess
{
    private readonly Process _process;
    private readonly WindowsProcessJob? _job;

    private ProcessTransportProcess(Process process)
    {
        _process = process;
        _job = OperatingSystem.IsWindows() ? new WindowsProcessJob(process) : null;
        StandardInput = process.StandardInput;
        StandardOutput = process.StandardOutput;
        StandardError = process.StandardError;
    }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public bool HasExited => _process.HasExited;

    public static ProcessTransportProcess Start(CodexLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (string argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw Providers.QuotaProviderException.CliNotFound(new AppMessage("CliStartFailure", spec.DisplayPath));
        }

        try
        {
            return new ProcessTransportProcess(process);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            throw Providers.QuotaProviderException.Transport(new AppMessage("ProcessCleanupFailure"));
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void KillEntireTree()
    {
        if (_job is not null) { _job.Terminate(); }
        else if (!_process.HasExited) { _process.Kill(entireProcessTree: true); }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _job?.Dispose();
        _process.Dispose();
    }
}
