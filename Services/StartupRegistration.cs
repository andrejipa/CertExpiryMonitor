using System.Diagnostics;
using Microsoft.Win32;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

public sealed class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "CertExpiryMonitor";
    private const string TaskName   = "CertExpiryMonitor";

    private readonly FileLogger _logger;

    public StartupRegistration(FileLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registra inicializacao no logon. Tenta Task Scheduler primeiro;
    /// se falhar, cai no registro HKCU\Run.
    /// <para>
    /// E idempotente: se ja estiver registrado, sobrescreve com o caminho atual do
    /// exe (importante apos atualizacao da versao, quando o caminho fisico muda).
    /// </para>
    /// </summary>
    public bool EnsureRegistered()
    {
        // Pre-check: se ambos caminhos ja estao registrados E apontam para o exe atual,
        // nao precisa fazer nada. Reduz overhead a cada startup do app.
        var status = QueryStatus();
        if (status.TaskSchedulerRegistered &&
            IsCommandForExecutable(status.TaskSchedulerCommand, status.ResolvedExecutablePath))
        {
            RemoveFromRegistry();
            _logger.Info($"Startup already registered via Task Scheduler at {status.ResolvedExecutablePath}");
            return true;
        }

        if (!TryRegisterWithTaskScheduler())
        {
            return RegisterWithRegistry();
        }

        RemoveFromRegistry();
        return true;
    }

    public bool Remove()
    {
        var taskRemoved = RemoveFromTaskScheduler();
        var registryRemoved = RemoveFromRegistry();
        return taskRemoved && registryRemoved;
    }

    /// <summary>
    /// Resultado de uma verificacao de status de inicializacao automatica.
    /// </summary>
    public sealed record StartupStatus(
        bool TaskSchedulerRegistered,
        string? TaskSchedulerCommand,
        bool RegistryRegistered,
        string? RegistryCommand,
        string ResolvedExecutablePath)
    {
        public bool TaskSchedulerMatchesCurrentExecutable =>
            TaskSchedulerRegistered &&
            IsCommandForExecutable(TaskSchedulerCommand, ResolvedExecutablePath);

        public bool RegistryMatchesCurrentExecutable =>
            RegistryRegistered &&
            IsCommandForExecutable(RegistryCommand, ResolvedExecutablePath);

        public bool IsRegistered => TaskSchedulerMatchesCurrentExecutable || RegistryMatchesCurrentExecutable;
    }

    /// <summary>
    /// Consulta o estado atual de registro de inicializacao automatica.
    /// Nao modifica nada — apenas le. Usado pelo menu de diagnostico.
    /// </summary>
    public StartupStatus QueryStatus()
    {
        var taskCommand     = QueryTaskSchedulerCommand();
        var registryCommand = QueryRegistryCommand();
        return new StartupStatus(
            TaskSchedulerRegistered: taskCommand is not null,
            TaskSchedulerCommand:    taskCommand,
            RegistryRegistered:      registryCommand is not null,
            RegistryCommand:         registryCommand,
            ResolvedExecutablePath:  ResolveExecutablePath());
    }

    private string? QueryTaskSchedulerCommand()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = $"/query /tn \"{TaskName}\" /fo CSV /v",
                WindowStyle            = ProcessWindowStyle.Hidden,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            });
            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            _ = stderrTask.GetAwaiter().GetResult();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

            return ExtractTaskSchedulerCommand(stdout);
        }
        catch
        {
            return null;
        }
    }

    private string? QueryRegistryCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Task Scheduler
    // -------------------------------------------------------------------------

    private bool TryRegisterWithTaskScheduler()
    {
        try
        {
            var exePath = ResolveExecutablePath();

            // schtasks /create cria ou sobrescreve (/f) uma tarefa ONLOGON
            // com privilegios normais (/rl LIMITED), sem elevacao.
            var args = BuildCreateTaskArguments(exePath);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = args,
                WindowStyle            = ProcessWindowStyle.Hidden,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            });

            if (process is null) return false;

            // Le ambos os streams de forma assincrona ANTES do WaitForExit
            // para evitar deadlock se stdout/stderr encherem o buffer (~4KB).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited     = process.WaitForExit(10_000);

            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                _logger.Info("Task Scheduler registration timed out after 10s; will fall back to registry");
                return false;
            }

            var success = process.ExitCode == 0;
            if (success)
            {
                _logger.Info("Startup registered via Task Scheduler");
            }
            else
            {
                var stderr = stderrTask.GetAwaiter().GetResult()?.Trim();
                var detail = string.IsNullOrEmpty(stderr) ? string.Empty : $": {stderr}";
                _logger.Info($"Task Scheduler registration returned exit code {process.ExitCode}{detail}; will fall back to registry");
            }

            // Drena stdout para nao deixar a task pendurada.
            _ = stdoutTask.GetAwaiter().GetResult();
            return success;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Task Scheduler registration failed; falling back to registry");
            return false;
        }
    }

    /// <summary>
    /// Resolve o caminho do executavel. <see cref="Environment.ProcessPath"/> e
    /// preferido por ser confiavel em publish single-file; <see cref="Application.ExecutablePath"/>
    /// e fallback caso ProcessPath retorne null (cenarios raros).
    /// </summary>
    private static string ResolveExecutablePath() =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    private bool RemoveFromTaskScheduler()
    {
        try
        {
            if (QueryTaskSchedulerCommand() is null)
            {
                return true;
            }

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName               = "schtasks.exe",
                Arguments              = $"/delete /tn \"{TaskName}\" /f",
                WindowStyle            = ProcessWindowStyle.Hidden,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            });

            if (process is null) return false;

            // Drena ambos os streams antes do WaitForExit (vide TryRegisterWithTaskScheduler).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            _ = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult()?.Trim();
            if (process.ExitCode == 0)
            {
                return true;
            }

            var detail = string.IsNullOrEmpty(stderr) ? string.Empty : $": {stderr}";
            _logger.Error(new InvalidOperationException($"schtasks delete failed with exit code {process.ExitCode}{detail}"), "Failed to remove Task Scheduler entry");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove Task Scheduler entry");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // HKCU\Run (fallback)
    // -------------------------------------------------------------------------

    private bool RegisterWithRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            var executable = ResolveExecutablePath();
            key.SetValue(ValueName, BuildRegistryCommand(executable), RegistryValueKind.String);
            _logger.Info("Startup registered via registry HKCU\\Run");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to register startup via registry");
            return false;
        }
    }

    private bool RemoveFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove startup from registry");
            return false;
        }
    }

    internal static string BuildCreateTaskArguments(string executablePath)
        => StartupRegistrationCommandBuilder.BuildCreateTaskArguments(executablePath);

    internal static string BuildRegistryCommand(string executablePath)
        => StartupRegistrationCommandBuilder.BuildRegistryCommand(executablePath);

    internal static string? ExtractTaskSchedulerCommand(string stdout)
        => StartupRegistrationCommandBuilder.ExtractTaskSchedulerCommand(stdout);

    internal static bool IsCommandForExecutable(string? command, string executablePath)
        => StartupRegistrationCommandBuilder.IsCommandForExecutable(command, executablePath);
}
