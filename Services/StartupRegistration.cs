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
    /// </summary>
    public void EnsureRegistered()
    {
        if (!TryRegisterWithTaskScheduler())
        {
            RegisterWithRegistry();
        }
    }

    public void Remove()
    {
        RemoveFromTaskScheduler();
        RemoveFromRegistry();
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
            var args = string.Join(" ",
                "/create",
                $"/tn \"{TaskName}\"",
                $"/tr \"\\\"{exePath}\\\" --background\"",
                "/sc ONLOGON",
                "/rl LIMITED",
                "/f");

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

    private void RemoveFromTaskScheduler()
    {
        try
        {
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

            if (process is null) return;

            // Drena ambos os streams antes do WaitForExit (vide TryRegisterWithTaskScheduler).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            _ = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove Task Scheduler entry");
        }
    }

    // -------------------------------------------------------------------------
    // HKCU\Run (fallback)
    // -------------------------------------------------------------------------

    private void RegisterWithRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            var executable = ResolveExecutablePath();
            key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
            _logger.Info("Startup registered via registry HKCU\\Run");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to register startup via registry");
        }
    }

    private void RemoveFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove startup from registry");
        }
    }
}
