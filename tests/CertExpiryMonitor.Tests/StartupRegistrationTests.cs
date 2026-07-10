using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public void BuildCreateTaskArgumentsQuotesExecutableAndUsesLimitedOnLogonTask()
    {
        var exe = @"C:\Users\Test User\App Folder\CertExpiryMonitor.exe";

        var args = StartupRegistration.BuildCreateTaskArguments(exe);

        Assert.Contains("/create", args, StringComparison.Ordinal);
        Assert.Contains("/tn \"CertExpiryMonitor\"", args, StringComparison.Ordinal);
        Assert.Contains($"\\\"{exe}\\\" --background", args, StringComparison.Ordinal);
        Assert.Contains("/sc ONLOGON", args, StringComparison.Ordinal);
        Assert.Contains("/rl LIMITED", args, StringComparison.Ordinal);
        Assert.EndsWith("/f", args, StringComparison.Ordinal);
        Assert.Equal($"/create /tn \"CertExpiryMonitor\" /tr \"\\\"{exe}\\\" --background\" /sc ONLOGON /rl LIMITED /f", args);
    }

    [Fact]
    public void BuildRegistryCommandQuotesExecutable()
    {
        var exe = @"C:\Users\Test User\App Folder\CertExpiryMonitor.exe";

        var command = StartupRegistration.BuildRegistryCommand(exe);

        Assert.Equal($"\"{exe}\" --background", command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildCreateTaskArgumentsRejectsBlankExecutablePath(string? exe)
    {
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.BuildCreateTaskArguments(exe!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildRegistryCommandRejectsBlankExecutablePath(string? exe)
    {
        Assert.ThrowsAny<ArgumentException>(() => StartupRegistration.BuildRegistryCommand(exe!));
    }

    [Theory]
    [InlineData("C:\\App\\CertExpiryMonitor.exe\" --background")]
    [InlineData("C:\\App\\CertExpiryMonitor.exe\r\n")]
    public void BuildCreateTaskArgumentsRejectsAmbiguousExecutablePath(string exe)
    {
        Assert.Throws<ArgumentException>(() => StartupRegistration.BuildCreateTaskArguments(exe));
    }

    [Theory]
    [InlineData("C:\\App\\CertExpiryMonitor.exe\" --background")]
    [InlineData("C:\\App\\CertExpiryMonitor.exe\r\n")]
    public void BuildRegistryCommandRejectsAmbiguousExecutablePath(string exe)
    {
        Assert.Throws<ArgumentException>(() => StartupRegistration.BuildRegistryCommand(exe));
    }

    [Fact]
    public void ExtractTaskSchedulerCommandReturnsTaskToRunColumnWhenAvailable()
    {
        var stdout = string.Join(Environment.NewLine,
            "\"TaskName\",\"Task To Run\",\"Status\"",
            "\"\\CertExpiryMonitor\",\"\"\"C:\\Users\\Test User\\App Folder\\CertExpiryMonitor.exe\"\" --background\",\"Ready\"");

        var command = StartupRegistration.ExtractTaskSchedulerCommand(stdout);

        Assert.Equal("\"C:\\Users\\Test User\\App Folder\\CertExpiryMonitor.exe\" --background", command);
    }

    [Fact]
    public void ExtractTaskSchedulerCommandHandlesTaskToRunAsFirstColumn()
    {
        var stdout = string.Join(Environment.NewLine,
            "\"Task To Run\",\"TaskName\",\"Status\"",
            "\"\"\"C:\\App\\CertExpiryMonitor.exe\"\" --background\",\"\\CertExpiryMonitor\",\"Ready\"");

        var command = StartupRegistration.ExtractTaskSchedulerCommand(stdout);

        Assert.Equal("\"C:\\App\\CertExpiryMonitor.exe\" --background", command);
    }

    [Fact]
    public void ExtractTaskSchedulerCommandPreservesEscapedCommasInsideFields()
    {
        var stdout = string.Join(Environment.NewLine,
            "\"TaskName\",\"Comment\",\"Task To Run\",\"Status\"",
            "\"\\CertExpiryMonitor\",\"empresa, matriz\",\"\"\"C:\\App\\CertExpiryMonitor.exe\"\" --background\",\"Ready\"");

        var command = StartupRegistration.ExtractTaskSchedulerCommand(stdout);

        Assert.Equal("\"C:\\App\\CertExpiryMonitor.exe\" --background", command);
    }

    [Fact]
    public void ExtractTaskSchedulerCommandSkipsPreambleLinesBeforeCsvHeader()
    {
        var stdout = string.Join(Environment.NewLine,
            "INFO: There are no scheduled tasks currently running.",
            "\"TaskName\",\"Task To Run\",\"Status\"",
            "\"\\CertExpiryMonitor\",\"\"\"C:\\Users\\Test User\\App Folder\\CertExpiryMonitor.exe\"\" --background\",\"Ready\"");

        var command = StartupRegistration.ExtractTaskSchedulerCommand(stdout);

        Assert.Equal("\"C:\\Users\\Test User\\App Folder\\CertExpiryMonitor.exe\" --background", command);
    }

    [Fact]
    public void ExtractTaskSchedulerCommandFallsBackToDataLineForLocalizedHeaders()
    {
        var stdout = string.Join(Environment.NewLine,
            "\"Nome da Tarefa\",\"Acao\"",
            "\"\\CertExpiryMonitor\",\"\"\"C:\\App\\CertExpiryMonitor.exe\"\" --background\"");

        var command = StartupRegistration.ExtractTaskSchedulerCommand(stdout);

        Assert.Equal("\"C:\\App\\CertExpiryMonitor.exe\" --background", command);
        Assert.True(StartupRegistration.IsCommandForExecutable(command, @"C:\App\CertExpiryMonitor.exe"));
    }

    [Fact]
    public void ExtractTaskSchedulerCommandReturnsNullWhenCommandCannotBeIdentified()
    {
        var stdout = string.Join(Environment.NewLine,
            "\"Nome da Tarefa\",\"Acao\"",
            "\"\\CertExpiryMonitor\",\"Ready\"");

        var command = StartupRegistration.ExtractTaskSchedulerCommand(stdout);

        Assert.Null(command);
    }

    [Fact]
    public void IsCommandForExecutableRequiresExactExecutablePath()
    {
        var exe = @"C:\App\CertExpiryMonitor.exe";

        Assert.True(StartupRegistration.IsCommandForExecutable($"\"{exe}\" --background", exe));
        Assert.True(StartupRegistration.IsCommandForExecutable($"\"{exe}\"", exe));
        Assert.True(StartupRegistration.IsCommandForExecutable($@"{exe} --background", exe));
        Assert.False(StartupRegistration.IsCommandForExecutable($@"{exe}.old --background", exe));
        Assert.False(StartupRegistration.IsCommandForExecutable($@"""C:\App Other\CertExpiryMonitor.exe"" --background", exe));
        Assert.False(StartupRegistration.IsCommandForExecutable($"\"{exe}\"--background", exe));
        Assert.False(StartupRegistration.IsCommandForExecutable($"\"{exe}\"malformed", exe));
    }

    [Fact]
    public void IsCommandForExecutableHandlesPathsWithSpaces()
    {
        var exe = @"C:\Users\Test User\App Folder\CertExpiryMonitor.exe";

        Assert.True(StartupRegistration.IsCommandForExecutable($"\"{exe}\" --background", exe));
        Assert.False(StartupRegistration.IsCommandForExecutable(@"C:\Users\Test User\App Folder\CertExpiryMonitor.exe --background", exe));
    }

    [Fact]
    public void StartupStatusRequiresCommandForCurrentExecutable()
    {
        var current = @"C:\App\CertExpiryMonitor.exe";
        var stale = @"C:\Old\CertExpiryMonitor.exe";

        var staleTask = new StartupRegistration.StartupStatus(
            TaskSchedulerRegistered: true,
            TaskSchedulerCommand: $"\"{stale}\" --background",
            RegistryRegistered: false,
            RegistryCommand: null,
            ResolvedExecutablePath: current);

        var currentTask = staleTask with
        {
            TaskSchedulerCommand = $"\"{current}\" --background"
        };

        var staleRegistry = new StartupRegistration.StartupStatus(
            TaskSchedulerRegistered: false,
            TaskSchedulerCommand: null,
            RegistryRegistered: true,
            RegistryCommand: $"\"{stale}\" --background",
            ResolvedExecutablePath: current);

        var currentRegistry = staleRegistry with
        {
            RegistryCommand = $"\"{current}\" --background"
        };

        Assert.False(staleTask.IsRegistered);
        Assert.False(staleTask.TaskSchedulerMatchesCurrentExecutable);
        Assert.True(currentTask.IsRegistered);
        Assert.True(currentTask.TaskSchedulerMatchesCurrentExecutable);

        Assert.False(staleRegistry.IsRegistered);
        Assert.False(staleRegistry.RegistryMatchesCurrentExecutable);
        Assert.True(currentRegistry.IsRegistered);
        Assert.True(currentRegistry.RegistryMatchesCurrentExecutable);
    }
}
