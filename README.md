# CertExpiryMonitor

[![Build and Test](https://github.com/andrejipa/CertExpiryMonitor/actions/workflows/build.yml/badge.svg)](https://github.com/andrejipa/CertExpiryMonitor/actions)
![Tests](https://img.shields.io/badge/tests-335%20passing-brightgreen)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)

Aplicativo Windows leve para monitorar certificados digitais A1 no perfil do usuário logado.

> **Status:** 335 testes passando (build limpo, 0 warnings); Stryker global `71,67%`; publish single-file validado em `75,50 MB`.

**Repositório:** https://github.com/andrejipa/CertExpiryMonitor

## Arquitetura

- `Program`: inicialização, mutex de instância única e composição dos serviços.
- `TrayApplicationContext`: host em background com ícone de bandeja, timer diário, configuração de horário e ações de notificação.
- `CertificateCheckService`: encapsula a lógica de verificação (guards de horário/data/hash, cálculo do snapshot hash).
- `CertificateReader`: lê apenas `CurrentUser\My` com `X509Store(StoreName.My, StoreLocation.CurrentUser)` em modo somente leitura.
- `ExpiryEvaluator`: aplica as faixas configuradas, deduplica por thumbprint e respeita estados persistidos.
- `ExpiryThresholds`: modelo de faixas configuráveis (padrão: 30/15/7/1 dias); `Normalized()` garante ordering.
- `JsonStateStore`: persiste estado por thumbprint em `certificate-state.json` com envelope versionado (v1) e suporte a formato legado.
- `JsonSettingsStore`: persiste configurações do usuário em `settings.json`.
- `DiagnosticEventStore`: registra diagnóstico técnico mínimo em `diagnostics.db` SQLite local, sempre redigido.
- `ToastNotifierService`: cria atalho COM com AppUserModelID e emite Toast Notifications compactas; janela propria e usada apenas como fallback se o Windows rejeitar o toast.
- `StartupRegistration`: registra inicialização via Task Scheduler (ONLOGON, sem elevação); fallback para `HKCU\Run`.
- `DiagnosticsBundleService`: exporta um `.zip` local com logs, métricas, configurações e resumo seguro para análise.

## Regras implementadas

- Escopo limitado a `CurrentUser\My`.
- Certificados considerados: `HasPrivateKey == true`, thumbprint presente e `NotAfter` válido.
- Estados: `None`, `Notified30`, `Notified15`, `Notified7`, `Notified1`, `Dismissed`.
- Faixas de notificação configuráveis pelo usuário (padrão: 30/15/7/1 dias antes do vencimento).
- Verificação automática diária no horário configurado; skip se já verificou hoje com os mesmos certificados (SHA-256 do snapshot).
- Atraso inicial padrão de 5 minutos após login.
- Horario diario configuravel em formato `HH:mm`.
- Um único aviso consolidado por verificação.
- Notificação: toast compacto e urgente do Windows; ao clicar no corpo do aviso, abre a tela de detalhes. Popup proprio aparece apenas se o Windows rejeitar a chamada do toast.
- Na lista de certificados, o botão direito permite remover selecionados e abrir `certmgr.msc` do usuário atual (`Pessoal > Certificados`).

## Segurança

- Não exporta certificados.
- Não acessa chave privada; usa somente `HasPrivateKey`.
- Não armazena senha ou PFX.
- Não usa `LocalMachine`, `Root` ou `TrustedPeople`.
- Não requer administrador.
- Não envia dados para rede.
- Logs não gravam chave privada nem conteúdo exportável.
- Exportação de diagnóstico não grava chave privada, PFX ou senha; certificados entram apenas como resumo com documento redigido.
- `diagnostics.db` usa hash de thumbprint, prefixo de serial e dados redigidos; não substitui os JSONs de estado/configuração.

## Validação

- Sem duplicação de notificação: `ExpiryEvaluator` usa `HashSet` por thumbprint dentro do plano e estado persistido por thumbprint.
- Persistência: settings/state são salvos em JSON por usuário com escrita atômica.
- Horário: timer agenda a próxima execução com base no horário salvo e `LastCheckDate` impede repetição diária.
- Reboot/login: inicialização via Task Scheduler (ONLOGON, sem elevação), com fallback automático para `HKCU\Run` se o Task Scheduler falhar; ao iniciar, aguarda o atraso inicial e executa se o horário do dia já passou.
- Sem certificados: scanner retorna lista vazia, não mostra popup e salva execução sem erro.

## Bugs possíveis

- Toast em app não empacotado depende de AppUserModelID e atalho no Start Menu; há popup proprio topmost apenas como fallback quando a chamada de toast falha.
- Mudança manual de data/hora do Windows pode alterar o comportamento de `LastCheckDate`.
- Ambientes com políticas corporativas podem bloquear Toast Notifications ou acesso ao registro `HKCU\Run`.
- Clique tardio em toast antigo pode dispensar certificado já renovado se o mesmo thumbprint ainda existir no estado.

## Edge cases

- Certificado renovado normalmente recebe novo thumbprint e será tratado como novo certificado.
- Certificado expirado há mais de um dia é ignorado pelas faixas atuais.
- Certificados duplicados no store são consolidados por thumbprint.
- JSON corrompido é ignorado com fallback seguro; a escrita atômica reduz a chance de corrupção.
- Se o usuário executar verificação manual, ela conta como verificação do dia.

## Melhorias enterprise

- Assinar o executável e publicar via MSIX/Intune/SCCM.
- Adicionar EventLog opcional com níveis de log controlados por política.
- Criar templates ADMX/GPO para horário padrão e bloqueio de configurações.
- Adicionar telemetria local opt-in sem rede, por exemplo contadores no EventLog.

## Como compilar

Requisitos:

- Windows 10/11.
- .NET SDK 8 ou superior.

Compilação rápida:

```powershell
dotnet build CertExpiryMonitor.csproj
```

**Publicação para release** (sincroniza versão no `.csproj` e no `.iss`, gera binário standalone):

```powershell
.\scripts\Publish-Release.ps1 -Version "1.0.0"
# Com geração do instalador Inno Setup:
.\scripts\Publish-Release.ps1 -Version "1.0.0" -BuildInstaller
```

O projeto principal exclui a pasta `tests\` da compilação. O binário gerado é standalone para Windows x64 e não exige SDK ou runtime .NET instalado.


## Como rodar os testes

Os testes usam xUnit, FsCheck e coverlet e ficam em `tests\CertExpiryMonitor.Tests`.

```powershell
dotnet test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj
# Com cobertura de codigo:
dotnet test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj --collect:"XPlat Code Coverage"
# Mutation testing local:
dotnet tool restore
dotnet tool run dotnet-stryker
# Rodada E2E agressiva no Windows real:
powershell -ExecutionPolicy Bypass -File .\scripts\Run-BugHunt.ps1 -Maximum -KeepArtifacts
```

**Em máquinas sem o .NET 8 SDK instalado globalmente**, é possível baixá-lo localmente sem afetar o sistema:

```powershell
Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 8.0 -InstallDir .\.dotnet-local -NoPath
.\.dotnet-local\dotnet.exe test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj -c Release
```

A pasta `.dotnet-local\` está no `.gitignore`. O CI no GitHub Actions já tem o SDK pré-instalado via `actions/setup-dotnet`.

**Cobertura dos testes (335 casos, todos verdes):**

| Suite | O que cobre |
|---|---|
| `ExpiryEvaluatorTests` | Faixas padrão, expirado, Dismissed, progresso entre buckets, deduplicação, MarkNotified |
| `ExpiryEvaluatorThresholdsTests` | Faixas customizadas, boundary, progresso, ReminderPlan |
| `ExpiryThresholdsTests` | `Normalized()` — zeros, negativos, invertidos, iguais, idempotência, `ForBucket` |
| `JsonStateStoreTests` | Round-trip, formato legado (array), envelope sem `version`/com `version` malformado, migração, thumbprint case/espaços, JSON corrompido |
| `JsonStateStoreConcurrencyTests` | Mutex global sob race: `Parallel.For` Save+Load não corrompe, não lança exceções |
| `JsonSettingsStoreTests` | Envelope versionado v1, compat com formato legado, envelope sem `version`/com `version` malformado, migração automática, JSON corrompido preservado |
| `CertificateCheckServiceTests` | Guards de skip (horário/data/hash), snapshot hash com deduplicação por thumbprint, race do `_isChecking` entre threads, null guards |
| `CertificateDocumentHelpersTests` | `FormatDocument` (CPF/CNPJ), proteção contra texto com dígitos embutidos, `ParseHolder`, `GetCommonNameFallback` |
| `CertificateStatusHelpersTests` | `GetStatusText`/`GetStatusCategory` com thresholds padrão e customizados — garante que o grid colore corretamente quando o usuário muda as faixas |
| `CertificateReaderTests` | Certificado sem chave privada é ignorado |
| `PropertyBasedTests` | Fuzz/property tests de argumentos de toast, thresholds, stores JSON, helpers de documento e XML de toast |
| `ToastAndActivationTests` | XML de toast urgente/compacto, som silenciado quando configurado, limite de ações e round-trip dos argumentos de ativação |
| `DetailsFormTimeTests` | Validação do horário diário em formato 24h, de `00:00` a `23:59`, e orçamento vertical do painel de resumo |
| `StartupRegistrationTests` | Contratos de Task Scheduler/HKCU Run sem executar registro real |
| `ReleaseContractTests` | Contratos de CI, restore locked, instalador e script remoto seguro |
| `FileLoggerTests` | Log JSON, rotação e aplicação de configurações avançadas |
| `TelemetryServiceTests` | Métricas locais opt-in, reset, persistência, timestamps e leitura tolerante de `version` malformado |
| `AppPathsTests` | Caminhos com `rootOverride` para isolamento de testes |
| `DiagnosticEventStoreTests` | SQLite local de diagnóstico: schema, redaction, corrupção, retenção e snapshot consultável |
| `DiagnosticsBundleServiceTests` | Pacote `.zip` de diagnóstico com logs/métricas, redaction de hash/documento e status de startup com match/mismatch do executável atual |

**Mutation testing da camada SQLite/diagnóstico (Stryker 4.14.1, relatório `StrykerOutput\2026-05-23.12-36-09`):**

| Arquivo | Score |
|---|---:|
| `Services\DiagnosticEventStore.cs` | `71,69%` |
| `Services\DiagnosticRedactor.cs` | `84,87%` |
| `Services\DiagnosticsBundleService.cs` | `72,48%` |
| Global | `71,67%` |

**Tamanho do publish single-file com SQLite:** `75,50 MB` em `artifacts\release-size\sqlite-20260523-124219`, contra baseline pré-SQLite de `74,51 MB`; delta `+0,99 MB` (`+1,33%`). Mantido `PublishTrimmed=false`, `EnableCompressionInSingleFile=true` e `IncludeNativeLibrariesForSelfExtract=true`.

## Exportar diagnostico para analise

No PC instalado, clique com o botão direito no ícone da bandeja e use **Exportar diagnóstico...**. O app gera um `.zip` para trazer e analisar depois.

O pacote inclui `monitor.log` e logs rotacionados, `diagnostics.db`, `telemetry.json` se a coleta local estiver ativa, snapshot de configurações com hash redigido, status de Task Scheduler/HKCU Run, versão do app/SO e resumo dos certificados por vencimento. Não exporta chave privada, PFX, senha nem documento completo do titular.

## Caca a bugs agressiva

A rodada E2E de bug hunt altera estado real do Windows, mas salva snapshot e restaura no `finally`: processo do app, `%LOCALAPPDATA%\CertExpiryMonitor`, tarefa `CertExpiryMonitor`, `HKCU\Run`, atalho do Start Menu e certificado de teste criado em `CurrentUser\My`.

```powershell
dotnet tool restore
dotnet tool run dotnet-stryker
powershell -ExecutionPolicy Bypass -File .\scripts\Run-BugHunt.ps1 -Maximum -KeepArtifacts
```

Artefatos ficam em `artifacts\bughunt\<timestamp>\`: transcript, publish usado, snapshots, capturas PNG e dump UIA.

## Como instalar no usuario atual

Depois do publish:

```powershell
.\scripts\Install-CurrentUser.ps1 -SourceDirectory .\publish
```

O script copia o app para `%LOCALAPPDATA%\Programs\CertExpiryMonitor` e inicia o processo em background. O próprio app registra a inicialização no **Task Scheduler** (ONLOGON, sem elevação) na primeira execução; usa `HKCU\Run` como fallback se o Task Scheduler falhar. Os dados ficam em `%LOCALAPPDATA%\CertExpiryMonitor`.

Para remover:

```powershell
.\scripts\Uninstall-CurrentUser.ps1
```

## Como gerar o instalador Inno Setup

Requisitos:

- Inno Setup 6 instalado.
- Publish standalone gerado na pasta `.\publish`.

Comando recomendado para gerar o publish:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o .\publish
```

Estrutura esperada:

```text
Vencimento dos Certificados\
  publish\
    CertExpiryMonitor.exe
  installer\
    CertExpiryMonitor.iss
```

Compilar o instalador pela interface do Inno Setup:

```text
Abra installer\CertExpiryMonitor.iss no Inno Setup Compiler e clique em Compile.
```

Ou via linha de comando:

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\CertExpiryMonitor.iss
```


Saida esperada:

```text
installer-output\CertExpiryMonitorSetup.exe
```

Instalacao silenciosa:

```powershell
.\installer-output\CertExpiryMonitorSetup.exe /silent
.\installer-output\CertExpiryMonitorSetup.exe /verysilent
```

O instalador usa `PrivilegesRequired=lowest`, instala os binários em `%LOCALAPPDATA%\Programs\CertExpiryMonitor`, cria atalho opcional no Menu Iniciar e inicia o app após a instalação. O registro de startup (Task Scheduler ou `HKCU\Run`) é gerenciado pelo próprio app.

Os dados por usuário ficam em `%LOCALAPPDATA%\CertExpiryMonitor`. O uninstall remove os binários, a tarefa agendada e a entrada `HKCU\Run` (fallback), mas preserva logs, settings e estado.

## Instalacao remota via PowerShell

Publique `installer-output\CertExpiryMonitorSetup.exe` em um local acessivel pelas maquinas, como SharePoint, OneDrive corporativo ou servidor interno HTTPS. Por padrao, o script exige HTTPS e SHA256 do instalador.

Exemplo recomendado:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-FromUrl.ps1 -InstallerUrl "https://servidor/CertExpiryMonitorSetup.exe" -Sha256 "COLE_O_SHA256_AQUI" -Silent
```

O SHA256 deve ter 64 caracteres hexadecimais. Para calcular o hash do instalador publicado:

```powershell
Get-FileHash -Path .\installer-output\CertExpiryMonitorSetup.exe -Algorithm SHA256
```

Uso legado ou controlado sem hash exige decisao explicita:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-FromUrl.ps1 -InstallerUrl "https://servidor/CertExpiryMonitorSetup.exe" -SkipHashValidation -Silent
```

URLs nao HTTPS, como HTTP ou file share, tambem exigem opt-in explicito:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-FromUrl.ps1 -InstallerUrl "http://servidor/CertExpiryMonitorSetup.exe" -Sha256 "COLE_O_SHA256_AQUI" -AllowInsecureUrl -Silent
```

O script instala no usuario atual, sem administrador, em `%LOCALAPPDATA%\Programs\CertExpiryMonitor`.

## Toast Notifications em app nao empacotado

O Windows exige um `AppUserModelID` associado a um atalho no Start Menu para que Toast Notifications funcionem de forma consistente em aplicativos desktop nao empacotados.

Este app usa o AppUserModelID `CertExpiryMonitor.Windows` e recria o atalho `CertExpiryMonitor.lnk` no Start Menu do usuario ao iniciar. Em ambientes corporativos, politicas do Windows podem bloquear Toasts. Quando a chamada de Toast falha, o app usa uma janela propria em primeiro plano como fallback; quando o Toast e aceito, nao abre segunda janela.

## Checklist de validacao manual

- Fazer login no Windows e confirmar que o processo inicia pelo usuario atual.
- Confirmar que `%LOCALAPPDATA%\CertExpiryMonitor\settings.json` salva o horario escolhido.
- Ajustar o horario desejado e confirmar uma unica verificacao por dia.
- Usar `Testar popup agora` na aba `Configuracoes` para validar o aviso sem esperar o horario diario.
- Simular execucao apos o horario configurado e confirmar que a verificacao roda depois do atraso inicial.
- Verificar que certificados sem chave privada nao aparecem na lista.
- Validar que o Toast compacto aparece quando ha certificados na faixa e que nao ha popup duplicado. A janela propria deve aparecer apenas se o Toast falhar.
- Confirmar que `Fechar aviso` fecha apenas a notificacao atual e nao persiste `Dismissed`.
- Rodar `dotnet test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj`.
