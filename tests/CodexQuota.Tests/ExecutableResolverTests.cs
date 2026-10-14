using CodexQuota.Core.Models;
using CodexQuota.Core.Providers;
using CodexQuota.Core.Transport;
using CodexQuota.Tests.Support;

namespace CodexQuota.Tests;

public sealed class ExecutableResolverTests
{
    private const string VendorRelative =
        @"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

    private const string LauncherRelative = @"node_modules\@openai\codex\bin\codex.js";

    [Fact]
    public void PrefersAnExplicitlyConfiguredNativeExecutable()
    {
        var environment = new FakeExecutableEnvironment();
        environment.AddFile(@"D:\tools\codex.exe");
        var resolver = new CodexExecutableResolver(environment);

        CodexLaunchSpec spec = resolver.Resolve(@"D:\tools\codex.exe");

        Assert.Equal(@"D:\tools\codex.exe", spec.FileName);
        Assert.Equal(CodexLaunchSpec.AppServerArguments, spec.Arguments);
    }

    [Fact]
    public void FindsTheNpmVendorExecutableOnThePath()
    {
        var environment = new FakeExecutableEnvironment();
        const string root = @"D:\npm_global";
        environment.SearchDirectories.Add(root);
        environment.AddFile(Path.Combine(root, VendorRelative));

        CodexLaunchSpec spec = new CodexExecutableResolver(environment).Resolve(null);

        Assert.Equal(Path.Combine(root, VendorRelative), spec.FileName);
        Assert.DoesNotContain("-c", spec.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToNodePlusTheOfficialLauncher()
    {
        var environment = new FakeExecutableEnvironment();
        const string root = @"C:\Users\测试 用户\AppData\Roaming\npm";
        const string programFiles = @"C:\Program Files";
        const string node = @"C:\Program Files\nodejs\node.exe";
        environment.SearchDirectories.Add(root);
        environment.Variables["ProgramFiles"] = programFiles;
        environment.AddFile(Path.Combine(root, LauncherRelative));
        environment.AddFile(node);

        CodexLaunchSpec spec = new CodexExecutableResolver(environment).Resolve(null);

        Assert.Equal(node, spec.FileName);
        Assert.Equal(Path.Combine(root, LauncherRelative), spec.Arguments[0]);
        Assert.Equal("app-server", spec.Arguments[1]);
    }

    [Fact]
    public void ReportsNodeNotFoundWhenOnlyTheJavaScriptLauncherExists()
    {
        var environment = new FakeExecutableEnvironment();
        const string root = @"C:\npm";
        environment.SearchDirectories.Add(root);
        environment.AddFile(Path.Combine(root, LauncherRelative));

        bool resolved = new CodexExecutableResolver(environment).TryResolve(null, out CodexLaunchSpec? spec, out QuotaProviderException? failure);

        Assert.False(resolved);
        Assert.Null(spec);
        Assert.Equal(ProviderFailureKind.NodeNotFound, failure!.Kind);
    }

    [Fact]
    public void ReportsMissingCliWhenNothingIsInstalled()
    {
        var environment = new FakeExecutableEnvironment();
        environment.SearchDirectories.Add(@"C:\empty");

        bool resolved = new CodexExecutableResolver(environment).TryResolve(null, out _, out QuotaProviderException? failure);

        Assert.False(resolved);
        Assert.Equal(ProviderFailureKind.CliNotFound, failure!.Kind);
        Assert.Contains("codex", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitConfiguredPathWithSpacesAndUnicodeIsUsedVerbatim()
    {
        var environment = new FakeExecutableEnvironment();
        const string configured = @"D:\我的 工具\codex\codex.exe";
        environment.AddFile(configured);

        CodexLaunchSpec spec = new CodexExecutableResolver(environment).Resolve(configured);

        Assert.Equal(configured, spec.FileName);
    }

    [Fact]
    public void ConfiguredShimDirectoryIsSearchedForTheNativePackage()
    {
        var environment = new FakeExecutableEnvironment();
        const string directory = @"D:\npm_global";
        environment.AddFile(Path.Combine(directory, "codex.cmd"));
        environment.AddFile(Path.Combine(directory, VendorRelative));

        CodexLaunchSpec spec = new CodexExecutableResolver(environment).Resolve(Path.Combine(directory, "codex.cmd"));

        Assert.Equal(Path.Combine(directory, VendorRelative), spec.FileName);
    }

    [Fact]
    public void FindsTheOfficialStandaloneInstallUnderLocalAppData()
    {
        var environment = new FakeExecutableEnvironment();
        environment.SearchDirectories.Add(@"C:\empty");
        environment.Variables["LOCALAPPDATA"] = @"C:\Users\测试\AppData\Local";
        const string bin = @"C:\Users\测试\AppData\Local\OpenAI\Codex\bin";
        const string versioned = @"C:\Users\测试\AppData\Local\OpenAI\Codex\bin\2e5e00daee91c61d";
        environment.SubDirectories[bin] = [versioned];
        environment.AddFile(Path.Combine(versioned, "codex.exe"));

        CodexLaunchSpec spec = new CodexExecutableResolver(environment).Resolve(null);

        Assert.Equal(Path.Combine(versioned, "codex.exe"), spec.FileName);
    }

    [Fact]
    public void ArgumentsKeepTheOfficialProviderAndBackendOrigin()
    {
        IReadOnlyList<string> arguments = CodexLaunchSpec.AppServerArguments;

        Assert.Contains("stdio://", arguments);
        Assert.Contains(arguments, argument => argument.Contains("model_provider=\"openai\"", StringComparison.Ordinal));
        Assert.Contains(arguments, argument => argument.Contains("chatgpt_base_url=\"https://chatgpt.com/backend-api/\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultsDoNotHardCodeAMachineSpecificPath()
    {
        Assert.Null(WidgetSettings.Default.CodexCliPath);
    }

    [Theory]
    [InlineData(@"D:\tools\arbitrary-helper.exe")]
    [InlineData(@"D:\tools\arbitrary-launcher.js")]
    public void ExplicitPathsCannotSelectArbitraryHelpers(string path)
    {
        var environment = new FakeExecutableEnvironment();
        environment.AddFile(path);
        environment.SearchDirectories.Add(@"D:\node");
        environment.AddFile(@"D:\node\node.exe");
        Assert.False(new CodexExecutableResolver(environment).TryResolve(path, out _, out _));
    }
}
