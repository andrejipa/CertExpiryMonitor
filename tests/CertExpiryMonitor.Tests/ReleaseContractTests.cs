using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class ReleaseContractTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CertExpiryMonitor.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repo root not found.");
    }

    [Fact]
    public void InstallerVersionMatchesProjectVersion()
    {
        var project = XDocument.Load(Path.Combine(Root, "CertExpiryMonitor.csproj"));
        var projectVersion = project.Descendants("Version").Single().Value;
        var installer = File.ReadAllText(Path.Combine(Root, "installer", "CertExpiryMonitor.iss"));
        var match = Regex.Match(installer, "^#define MyAppVersion \"(?<version>[^\"]+)\"", RegexOptions.Multiline);

        Assert.True(match.Success, "MyAppVersion deve existir no instalador.");
        Assert.Equal(projectVersion, match.Groups["version"].Value);
    }

    [Fact]
    public void InstallerKeepsPerUserInstallationAndDelegatesStartupPreferenceToApp()
    {
        var installer = File.ReadAllText(Path.Combine(Root, "installer", "CertExpiryMonitor.iss"));

        Assert.Contains("PrivilegesRequired=lowest", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DisableDirPage=yes", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/create /tn", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reg add", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Flags: nowait postinstall skipifsilent", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Flags: nowait runhidden skipifnotsilent", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--background", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainWorkflowUsesLockedRestore()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "build.yml"));

        Assert.Contains("--locked-mode", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: read", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void BugHuntRecognizesSuccessfulPopupFallback()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Run-BugHunt.ps1"));

        Assert.Contains("app popup fallback was used", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invoke-AppWindowButton $background.Id \"Certificados digitais\" \"Ver detalhes\"", script, StringComparison.Ordinal);
        Assert.Contains("Popup de teste registrou falha de exibicao", script, StringComparison.Ordinal);
        Assert.Contains("\"Testar aviso agora\" 20", script, StringComparison.Ordinal);
        Assert.Contains("BugHuntNativeMethods]::PostMessage", script, StringComparison.Ordinal);
        Assert.Contains("Fallback abriu instancia duplicada do app", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ForCondition { Test-Path $telemetryPath }", script, StringComparison.Ordinal);
        Assert.Contains("Nenhum caminho de toast/fallback foi observado no log", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePublishKeepsConservativeSingleFileSettingsForSQLite()
    {
        var project = XDocument.Load(Path.Combine(Root, "CertExpiryMonitor.csproj"));
        var properties = project.Root?.Elements("PropertyGroup").Elements()
            .GroupBy(element => element.Name.LocalName)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase)
            ?? throw new InvalidOperationException("Projeto sem PropertyGroup.");
        var packages = project.Descendants("PackageReference")
            .ToDictionary(
                element => element.Attribute("Include")?.Value ?? string.Empty,
                element => element.Attribute("Version")?.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal("false", properties["PublishTrimmed"]);
        Assert.Equal("true", properties["EnableCompressionInSingleFile"]);
        Assert.Equal("true", properties["IncludeNativeLibrariesForSelfExtract"]);
        Assert.Equal("8.0.27", packages["Microsoft.Data.Sqlite"]);
    }

    [Fact]
    public void DurableWriterRequestsPhysicalFlush()
    {
        var durableWriter = File.ReadAllText(Path.Combine(Root, "Services", "DurableFileWriter.cs"));
        Assert.Contains("FileOptions.WriteThrough", durableWriter, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", durableWriter, StringComparison.Ordinal);
        Assert.Contains("File.Replace(tempPath, path, backupPath", durableWriter, StringComparison.Ordinal);
        Assert.Contains("File.Delete(backupPath)", durableWriter, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePublishChecksInstallerDependencyAndGuardsCleanup()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Publish-Release.ps1"));
        var dependencyCheck = script.IndexOf("if (-not $iscc)", StringComparison.Ordinal);
        var versionWrite = script.IndexOf("Set-Content -Path $csproj", StringComparison.Ordinal);
        Assert.True(dependencyCheck >= 0 && dependencyCheck < versionWrite);
        Assert.Contains("throw \"Inno Setup 6 nao encontrado", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileAttributes]::ReparsePoint", script, StringComparison.Ordinal);
        Assert.Contains("$resolvedPublish.Equals($expectedPublish", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedPublish -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("throw \"ISCC terminou sem gerar o instalador esperado", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteInstallScriptRequiresExplicitInsecureChoices()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Install-FromUrl.ps1"));

        Assert.Contains("AllowInsecureUrl", script, StringComparison.Ordinal);
        Assert.Contains("SkipHashValidation", script, StringComparison.Ordinal);
        Assert.Contains("Nao combine -AllowInsecureUrl com -SkipHashValidation", script, StringComparison.Ordinal);
        Assert.Contains("Sha256 e obrigatorio", script, StringComparison.Ordinal);
        Assert.Contains("[Guid]::NewGuid()", script, StringComparison.Ordinal);
        Assert.Contains("^[0-9a-fA-F]{64}$", script, StringComparison.Ordinal);
        Assert.Contains("$tempRootWithSeparator", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-InstallerProcess", script, StringComparison.Ordinal);
        Assert.Contains("WaitForExit(120000)", script, StringComparison.Ordinal);
        Assert.Contains("Outra instancia de $appName esta rodando fora da instalacao atual", script, StringComparison.Ordinal);
        Assert.Contains("Test-ProcessPathEquals", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoForeignAppProcess $exePath", script, StringComparison.Ordinal);
        Assert.Contains("Stop-InstalledAppProcessAndWait $exePath", script, StringComparison.Ordinal);
        Assert.Contains("$deadline = (Get-Date).AddSeconds(10)", script, StringComparison.Ordinal);
        Assert.Contains("Nao foi possivel encerrar a instancia instalada antes de executar o instalador", script, StringComparison.Ordinal);
        Assert.Contains("/norestart", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process -FilePath $installerPath -ArgumentList $arguments -Wait -PassThru", script, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallScriptGuardsRecursiveDeletePath()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Uninstall-CurrentUser.ps1"));

        Assert.Contains("Assert-SafeInstallDirectory", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::GetFullPath", script, StringComparison.Ordinal);
        Assert.Contains("StartsWith($programsRootWithSeparator", script, StringComparison.Ordinal);
        Assert.Contains("InstallDirectory deve ser exatamente", script, StringComparison.Ordinal);
        Assert.Contains("Test-ProcessPathEquals", script, StringComparison.Ordinal);
        Assert.Contains("Stop-InstalledAppProcessAndWait $installedExe", script, StringComparison.Ordinal);
        Assert.Contains("$deadline = (Get-Date).AddSeconds(10)", script, StringComparison.Ordinal);
        Assert.Contains("CertExpiryMonitor.exe", script, StringComparison.Ordinal);
        Assert.Contains("unins000.exe", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $InstallDirectory -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("HKCU:\\Software\\Classes\\cert-expiry-monitor", script, StringComparison.Ordinal);
        Assert.Contains("CertExpiryMonitor.lnk", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process -Name \"CertExpiryMonitor\" -ErrorAction SilentlyContinue | Stop-Process -Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerUninstallRemovesTaskAndRegistryFallback()
    {
        var installer = File.ReadAllText(Path.Combine(Root, "installer", "CertExpiryMonitor.iss"));

        Assert.Contains("Where-Object {{ $_.Path -ieq", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("Where-Object {{ $_.Path -ieq '{app}\\{#MyAppExeName}' }}", installer, StringComparison.Ordinal);
        Assert.Contains("Where-Object { $_.Path -ieq", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill /IM", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schtasks /delete /tn", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reg delete \"\"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\"\"", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/v \"\"{#MyAppName}\"\"", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reg delete \"\"HKCU\\Software\\Classes\\cert-expiry-monitor\"\"", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualInstallScriptDoesNotPickFirstRecursiveExeSilently()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Install-CurrentUser.ps1"));

        Assert.Contains("Assert-SafeInstallDirectory", script, StringComparison.Ordinal);
        Assert.Contains("InstallDirectory deve ser exatamente", script, StringComparison.Ordinal);
        Assert.Contains("Test-ProcessPathEquals", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoForeignAppProcess $installedExe", script, StringComparison.Ordinal);
        Assert.Contains("Stop-InstalledAppProcessAndWait $installedExe", script, StringComparison.Ordinal);
        Assert.Contains("$deadline = (Get-Date).AddSeconds(10)", script, StringComparison.Ordinal);
        Assert.Contains("Resolve-SourceExecutable", script, StringComparison.Ordinal);
        Assert.Contains("publish\\CertExpiryMonitor.exe", script, StringComparison.Ordinal);
        Assert.Contains("Mais de um CertExpiryMonitor.exe encontrado", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("$exe = Resolve-SourceExecutable", StringComparison.Ordinal) <
            script.IndexOf("New-Item -ItemType Directory -Path $InstallDirectory", StringComparison.Ordinal),
            "Install-CurrentUser nao deve criar destino antes de validar origem.");
        Assert.DoesNotContain("Sort-Object FullName |\r\n    Select-Object -First 1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ToastShortcutIsAlwaysRecreatedToRefreshAppUserModelId()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "ToastNotifierService.cs"));

        Assert.DoesNotContain("File.GetLastWriteTimeUtc(shortcutPath)", source, StringComparison.Ordinal);
        Assert.Contains("AppUserModelId", source, StringComparison.Ordinal);
        Assert.Contains("persistFile.Save(shortcutPath, true)", source, StringComparison.Ordinal);
        Assert.Contains("ProtocolScheme = \"cert-expiry-monitor\"", source, StringComparison.Ordinal);
        Assert.Contains("Registry.CurrentUser.CreateSubKey($@\"Software\\Classes\\{ProtocolScheme}\")", source, StringComparison.Ordinal);
        Assert.Contains("Registry.CurrentUser.CreateSubKey($@\"Software\\Classes\\{ProtocolScheme}\\shell\\open\\command\")", source, StringComparison.Ordinal);
        Assert.Contains("return $\"\\\"{executable}\\\" --details \\\"%1\\\"\";", source, StringComparison.Ordinal);
        Assert.Contains("EnsureProtocolHandler(executable)", source, StringComparison.Ordinal);
        Assert.Contains("Toast registration ready.", source, StringComparison.Ordinal);
        Assert.Contains("notification plan is empty.", source, StringComparison.Ordinal);
        Assert.Contains("shortcut or protocol registration is not ready.", source, StringComparison.Ordinal);
        Assert.Contains("Windows toast notification was submitted successfully.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ToastXmlUsesProtocolActivationForDurableDetailsLaunch()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "ToastXmlBuilder.cs"));

        Assert.Contains("activationType=\"protocol\"", source, StringComparison.Ordinal);
        Assert.Contains("launch=\"{ToastNotifierService.DetailsProtocolUri}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("launch=\"action=view-details\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ToastShowFallsBackWhenWindowsNotificationsAreDisabled()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "ToastNotifierService.cs"));
        var methodStart = source.IndexOf("public bool Show(NotificationPlan plan", StringComparison.Ordinal);
        var canAttempt = source.IndexOf("if (!CanAttemptToast(notifier))", methodStart, StringComparison.Ordinal);
        var fallbackReturn = source.IndexOf("return false;", canAttempt, StringComparison.Ordinal);
        var showIndex = source.IndexOf("notifier.Show(toast);", methodStart, StringComparison.Ordinal);
        var helperStart = source.IndexOf("private bool CanAttemptToast(ToastNotifier notifier)", StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "ToastNotifierService.Show deve existir.");
        Assert.True(canAttempt > methodStart, "Show deve consultar se pode tentar toast nativo.");
        Assert.True(fallbackReturn > canAttempt, "Setting desabilitado deve retornar false para acionar fallback.");
        Assert.True(showIndex > fallbackReturn, "Toast so deve ser enviado depois da checagem de Setting.");
        Assert.True(helperStart > showIndex, "CanAttemptToast deve existir como helper testavel por contrato.");
        Assert.Contains("var setting = notifier.Setting;", source, StringComparison.Ordinal);
        Assert.Contains("setting == NotificationSetting.Enabled", source, StringComparison.Ordinal);
        Assert.Contains("Windows toast notification setting could not be read; attempting toast anyway", source, StringComparison.Ordinal);
        Assert.Contains("return true;", source[source.IndexOf("catch (Exception ex)", helperStart, StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public void BugHuntCertificatePrivateKeyIsNotExportable()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Run-BugHunt.ps1"));

        Assert.Contains("-KeyExportPolicy NonExportable", script, StringComparison.Ordinal);
        Assert.Contains("[System.Security.Cryptography.Oid]::new(\"2.5.29.32\")", script, StringComparison.Ordinal);
        Assert.Contains("0x60,0x4C,0x01,0x02,0x01,0x01", script, StringComparison.Ordinal);
        Assert.Contains("-Extension $a1PolicyExtension", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-KeyExportPolicy Exportable `", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BugHuntValidatesAndRestoresToastProtocolRegistration()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Run-BugHunt.ps1"));

        Assert.Contains("$protocolRegistryPath = \"HKCU:\\Software\\Classes\\cert-expiry-monitor\"", script, StringComparison.Ordinal);
        Assert.Contains("cert-expiry-monitor-protocol.reg", script, StringComparison.Ordinal);
        Assert.Contains("function Test-ProcessPathEquals", script, StringComparison.Ordinal);
        Assert.Contains("reg.exe export $protocolRegExePath $protocolBackupPath /y", script, StringComparison.Ordinal);
        Assert.Contains("reg.exe import $protocolBackupPath", script, StringComparison.Ordinal);
        Assert.Contains("Get-ProtocolCommand", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath \"cert-expiry-monitor://details\"", script, StringComparison.Ordinal);
        Assert.Contains("Ativacao via protocolo cert-expiry-monitor nao abriu detalhes", script, StringComparison.Ordinal);
        Assert.Contains("Get-ShortcutExtendedProperty", script, StringComparison.Ordinal);
        Assert.Contains("System.AppUserModel.ID", script, StringComparison.Ordinal);
        Assert.Contains("CertExpiryMonitor.Windows", script, StringComparison.Ordinal);
        Assert.Contains("shortcut-after-run.txt", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BugHuntScriptGuardsRecursiveDeletePathWithPathBoundary()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Run-BugHunt.ps1"));

        Assert.Contains("Remove-DirectoryUnder", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::GetFullPath", script, StringComparison.Ordinal);
        Assert.Contains("$resolvedParentWithSeparator", script, StringComparison.Ordinal);
        Assert.Contains("Equals($resolvedParent.TrimEnd('\\')", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedPath -Recurse -Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BugHuntRestoresOriginalProcessArguments()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "Run-BugHunt.ps1"));

        Assert.Contains("function Get-OriginalArguments", script, StringComparison.Ordinal);
        Assert.Contains("function Test-IsUnderDirectory", script, StringComparison.Ordinal);
        Assert.Contains("$process.CommandLine", script, StringComparison.Ordinal);
        Assert.Contains("Test-IsUnderDirectory $process.ExecutablePath $installDir", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $process.ExecutablePath -ArgumentList $arguments", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $process.ExecutablePath -ArgumentList \"--background\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$process.ExecutablePath.StartsWith($installDir", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureUiDoesNotKillForeignCertExpiryMonitorProcesses()
    {
        var script = File.ReadAllText(Path.Combine(Root, "scripts", "CaptureUi.ps1"));

        Assert.Contains("Assert-NoForeignAppProcess $ExePath", script, StringComparison.Ordinal);
        Assert.Contains("Stop-AppProcessesAndWait $ExePath", script, StringComparison.Ordinal);
        Assert.Contains("Test-ProcessPathEquals $_.Path $targetFullPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process CertExpiryMonitor -ErrorAction SilentlyContinue |\r\n        Stop-Process -Force", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ForcedImmediateCheckIsRetriedWhenTimerCheckDoesNotRun()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "TrayApplicationContext.cs"));
        var timerStart = source.IndexOf("private void OnTimerTick", StringComparison.Ordinal);
        var retryGuard = source.IndexOf("!result.Ran && ignoreConfiguredTime && _forceNextScheduledNotification", timerStart, StringComparison.Ordinal);
        var rearmIgnore = source.IndexOf("_ignoreConfiguredTimeOnNextTimer = true;", retryGuard, StringComparison.Ordinal);
        var retryTimer = source.IndexOf("ScheduleTimer(TimeSpan.FromSeconds(1));", retryGuard, StringComparison.Ordinal);
        var retryFlag = source.IndexOf("retryScheduled = true;", retryGuard, StringComparison.Ordinal);
        var finalGuard = source.IndexOf("if (!retryScheduled)", retryGuard, StringComparison.Ordinal);

        Assert.True(timerStart >= 0, "OnTimerTick deve existir.");
        Assert.True(retryGuard > timerStart, "Timer deve rearmar check imediato quando o check forçado nao executa.");
        Assert.True(rearmIgnore > retryGuard, "Retry deve continuar ignorando horario configurado.");
        Assert.True(retryTimer > retryGuard, "Retry deve ser agendado rapidamente.");
        Assert.True(retryFlag > retryTimer, "Retry imediato deve impedir reagendamento diario no finally.");
        Assert.True(finalGuard > retryFlag, "Finally nao deve sobrescrever retry imediato com agenda diaria.");
    }

    [Fact]
    public void ToggleStartupPersistsOnlyAfterStartupRegistrationSucceeds()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "TrayApplicationContext.cs"));
        var methodStart = source.IndexOf("private void ToggleStartup", StringComparison.Ordinal);
        var startupChangedIndex = source.IndexOf("var startupChanged = newSettings.StartupEnabled", methodStart, StringComparison.Ordinal);
        var failureGuardIndex = source.IndexOf("if (!startupChanged)", methodStart, StringComparison.Ordinal);
        var saveIndex = source.IndexOf("if (!_settingsStore.Save(newSettings))", methodStart, StringComparison.Ordinal);
        var ensureIndex = source.IndexOf("_startup.EnsureRegistered()", saveIndex, StringComparison.Ordinal);
        var removeIndex = source.IndexOf("_startup.Remove()", saveIndex, StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "ToggleStartup deve existir.");
        Assert.True(startupChangedIndex > methodStart, "ToggleStartup deve aplicar Task Scheduler/HKCU antes de persistir a opcao.");
        Assert.True(failureGuardIndex > startupChangedIndex, "Falha ao alterar startup nao deve ser persistida em settings.");
        Assert.True(saveIndex > failureGuardIndex, "Settings so deve ser salvo apos registro/remocao de startup bem-sucedido.");
        Assert.True(ensureIndex > saveIndex, "Falha de save deve tentar rollback para startup ligado quando era o estado anterior.");
        Assert.True(removeIndex > saveIndex, "Falha de save deve tentar rollback para startup desligado quando era o estado anterior.");
    }

    [Fact]
    public void ClosingFallbackPopupDoesNotMarkNotificationAsShown()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "FallbackNotificationWindow.cs"));
        var methodStart = source.IndexOf("public static bool Show", StringComparison.Ordinal);
        var dialogReturn = source.IndexOf("return form.ShowDialog() == DialogResult.OK;", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "FallbackNotificationWindow.Show deve existir.");
        Assert.True(dialogReturn > methodStart, "Fechar fallback sem Ver detalhes nao deve contar como notificacao efetiva.");
        Assert.DoesNotContain("form.ShowDialog();\r\n            return true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupDiagnosticsObservesRegistrationResult()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "StartupDiagnosticsWindow.cs"));
        var registerStart = source.IndexOf("registerBtn.Click += async", StringComparison.Ordinal);
        var resultAssign = source.IndexOf("var registered = await Task.Run(_startup.EnsureRegistered);", registerStart, StringComparison.Ordinal);
        var resultGuard = source.IndexOf("if (registered)", resultAssign, StringComparison.Ordinal);

        Assert.True(registerStart >= 0, "Botao de registrar deve existir.");
        Assert.True(resultAssign > registerStart, "Resultado bool de EnsureRegistered deve ser capturado.");
        Assert.True(resultGuard > resultAssign, "UI deve distinguir sucesso de falha de EnsureRegistered.");
        Assert.DoesNotContain("await Task.Run(_startup.EnsureRegistered);\r\n                await RefreshAsync();\r\n                MessageBox.Show", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramAppliesLoggerSettingsBeforeStartupLog()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        var settingsLoadIndex = source.IndexOf("settingsStore.TryLoad(out var currentSettings)", StringComparison.Ordinal);
        var applyIndex = source.IndexOf("logger.ApplySettings(currentSettings);", StringComparison.Ordinal);
        var startupLogIndex = source.IndexOf("logger.Info($\"CertExpiryMonitor v{version} starting", StringComparison.Ordinal);

        Assert.True(settingsLoadIndex >= 0, "Program deve carregar settings no startup.");
        Assert.True(applyIndex > settingsLoadIndex, "Logger deve aplicar settings carregadas.");
        Assert.True(startupLogIndex > applyIndex, "Primeiro log de startup deve respeitar LogFormat=Json.");
        Assert.DoesNotContain("starting\" acima ainda vao em texto", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRegistrationCleansRegistryFallbackWhenTaskIsCurrent()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "StartupRegistration.cs"));
        var validTaskIndex = source.IndexOf("Startup already registered via Task Scheduler", StringComparison.Ordinal);
        var cleanupIndex = source.LastIndexOf("RemoveFromRegistry();", validTaskIndex, StringComparison.Ordinal);

        Assert.True(validTaskIndex >= 0, "EnsureRegistered deve reconhecer task atual.");
        Assert.True(cleanupIndex >= 0, "HKCU Run antigo deve ser limpo antes do retorno por task valida.");
    }

    [Fact]
    public void StartupTaskRemovalDoesNotTreatAnyExitCodeOneAsSuccess()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Services", "StartupRegistration.cs"));
        var methodStart = source.IndexOf("private bool RemoveFromTaskScheduler", StringComparison.Ordinal);
        var precheck = source.IndexOf("if (QueryTaskSchedulerCommand() is null)", methodStart, StringComparison.Ordinal);
        var successGuard = source.IndexOf("if (process.ExitCode == 0)", methodStart, StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "RemoveFromTaskScheduler deve existir.");
        Assert.True(precheck > methodStart, "Tarefa ausente deve ser tratada antes de chamar schtasks /delete.");
        Assert.True(successGuard > precheck, "Apos schtasks /delete, apenas ExitCode 0 deve ser sucesso.");
        Assert.DoesNotContain("process.ExitCode == 0 || process.ExitCode == 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLikeFilesDoNotContainNulBytes()
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".csproj",
            ".iss",
            ".json",
            ".md",
            ".ps1",
            ".yml",
            ".yaml"
        };
        var ignoredSegments = new[]
        {
            $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}.dotnet-local{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}coverage{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}installer-output{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}StrykerOutput{Path.DirectorySeparatorChar}"
        };

        var offendingFiles = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path)))
            .Where(path => !ignoredSegments.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .Where(path => File.ReadAllBytes(path).Contains((byte)0))
            .Select(path => Path.GetRelativePath(Root, path))
            .ToArray();

        Assert.Empty(offendingFiles);
    }

}
