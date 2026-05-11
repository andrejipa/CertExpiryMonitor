# CertExpiryMonitor

<!-- Após o primeiro push para o GitHub, substitua andrejipa/CertExpiryMonitor pelo caminho real do repositório. -->
![Build and Test](https://github.com/andrejipa/CertExpiryMonitor/actions/workflows/build.yml/badge.svg)
![Tests](https://img.shields.io/badge/tests-104%20passing-brightgreen)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)

Aplicativo Windows leve para monitorar certificados digitais A1 no perfil do usuário logado.

> **Status:** 104 testes passando (build limpo, 0 warnings); publish single-file validado em ~75 MB.

## Como publicar este repositório no GitHub

```powershell
# (uma vez) Autenticar gh CLI no GitHub:
gh auth login --web

# Criar repositório público e fazer push da branch main:
gh repo create CertExpiryMonitor --public --source=. --remote=origin --push

# OU, manualmente:
git remote add origin https://github.com/<seu-usuario>/CertExpiryMonitor.git
git push -u origin main
```

Depois do push, o workflow `.github/workflows/build.yml` roda automaticamente e o badge acima fica verde. Se o seu usuário não for `andre-jipa`, atualize a URL do badge no início deste README.

## Arquitetura

- `Program`: inicialização, mutex de instância única e composição dos serviços.
- `TrayApplicationContext`: host em background com ícone de bandeja, timer diário, configuração de horário e ações de notificação.
- `CertificateCheckService`: encapsula a lógica de verificação (guards de horário/data/hash, cálculo do snapshot hash).
- `CertificateReader`: lê apenas `CurrentUser\My` com `X509Store(StoreName.My, StoreLocation.CurrentUser)` em modo somente leitura.
- `ExpiryEvaluator`: aplica as faixas configuradas, deduplica por thumbprint e respeita estados persistidos.
- `ExpiryThresholds`: modelo de faixas configuráveis (padrão: 30/15/7/1 dias); `Normalized()` garante ordering.
- `JsonStateStore`: persiste estado por thumbprint em `certificate-state.json` com envelope versionado (v1) e suporte a formato legado.
- `JsonSettingsStore`: persiste configurações do usuário em `settings.json`.
- `ToastNotifierService`: cria atalho COM com AppUserModelID e emite Toast Notifications consolidadas com 5 botões de ação.
- `StartupRegistration`: registra inicialização via Task Scheduler (ONLOGON, sem elevação); fallback para `HKCU\Run`.

## Regras implementadas

- Escopo limitado a `CurrentUser\My`.
- Certificados considerados: `HasPrivateKey == true`, thumbprint presente e `NotAfter` válido.
- Estados: `None`, `Notified30`, `Notified15`, `Notified7`, `Notified1`, `Dismissed`.
- Faixas de notificação configuráveis pelo usuário (padrão: 30/15/7/1 dias antes do vencimento).
- Verificação automática diária no horário configurado; skip se já verificou hoje com os mesmos certificados (SHA-256 do snapshot).
- Atraso inicial padrão de 5 minutos após login.
- Horario diario configuravel em formato `HH:mm`.
- Um único popup consolidado por verificação.
- Botões: lembrar depois, não lembrar este (dismiss-one), não lembrar nenhum (dismiss-all), configurar horário, ver detalhes.

## Segurança

- Não exporta certificados.
- Não acessa chave privada; usa somente `HasPrivateKey`.
- Não armazena senha ou PFX.
- Não usa `LocalMachine`, `Root` ou `TrustedPeople`.
- Não requer administrador.
- Não envia dados para rede.
- Logs não gravam chave privada nem conteúdo exportável.

## Validação

- Sem duplicação de notificação: `ExpiryEvaluator` usa `HashSet` por thumbprint dentro do plano e estado persistido por thumbprint.
- Persistência: settings/state são salvos em JSON por usuário com escrita atômica.
- Horário: timer agenda a próxima execução com base no horário salvo e `LastCheckDate` impede repetição diária.
- Reboot/login: inicialização via Task Scheduler (ONLOGON, sem elevação), com fallback automático para `HKCU\Run` se o Task Scheduler falhar; ao iniciar, aguarda o atraso inicial e executa se o horário do dia já passou.
- Sem certificados: scanner retorna lista vazia, não mostra popup e salva execução sem erro.

## Bugs possíveis

- Toast em app não empacotado depende de AppUserModelID e atalho no Start Menu; há fallback para balloon tip, mas sem botões.
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

Os testes usam xUnit + coverlet e ficam em `tests\CertExpiryMonitor.Tests`.

```powershell
dotnet test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj
# Com cobertura de codigo:
dotnet test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj --collect:"XPlat Code Coverage"
```

**Em máquinas sem o .NET 8 SDK instalado globalmente**, é possível baixá-lo localmente sem afetar o sistema:

```powershell
Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 8.0 -InstallDir .\.dotnet-local -NoPath
.\.dotnet-local\dotnet.exe test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj -c Release
```

A pasta `.dotnet-local\` está no `.gitignore`. O CI no GitHub Actions já tem o SDK pré-instalado via `actions/setup-dotnet`.

**Cobertura dos testes (104 casos, todos verdes):**

| Suite | O que cobre |
|---|---|
| `ExpiryEvaluatorTests` | Faixas padrão, expirado, Dismissed, progresso entre buckets, deduplicação, MarkNotified |
| `ExpiryEvaluatorThresholdsTests` | Faixas customizadas, boundary, progresso, ReminderPlan |
| `ExpiryThresholdsTests` | `Normalized()` — zeros, negativos, invertidos, iguais, idempotência, `ForBucket` |
| `JsonStateStoreTests` | Round-trip, formato legado (array), migração, thumbprint case/espaços, JSON corrompido |
| `JsonStateStoreConcurrencyTests` | Mutex global sob race: `Parallel.For` Save+Load não corrompe, não lança exceções |
| `JsonSettingsStoreTests` | Envelope versionado v1, compat com formato legado, migração automática, JSON corrompido preservado |
| `CertificateCheckServiceTests` | Guards de skip (horário/data/hash), snapshot hash, race do `_isChecking` entre threads, null guards |
| `CertificateDocumentHelpersTests` | `FormatDocument` (CPF/CNPJ), `ParseHolder`, `GetCommonNameFallback` |
| `CertificateStatusHelpersTests` | `GetStatusText`/`GetStatusCategory` com thresholds padrão e customizados — garante que o grid colore corretamente quando o usuário muda as faixas |
| `CertificateReaderTests` | Certificado sem chave privada é ignorado |

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

Publique `installer-output\CertExpiryMonitorSetup.exe` em um local acessivel pelas maquinas, como SharePoint, OneDrive corporativo, servidor interno HTTPS ou file share.

Exemplo com URL HTTPS:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-FromUrl.ps1 -InstallerUrl "https://servidor/CertExpiryMonitorSetup.exe" -Silent
```

Exemplo com validacao de hash:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Install-FromUrl.ps1 -InstallerUrl "https://servidor/CertExpiryMonitorSetup.exe" -Sha256 "COLE_O_SHA256_AQUI" -Silent
```

O script instala no usuario atual, sem administrador, em `%LOCALAPPDATA%\Programs\CertExpiryMonitor`.

## Toast Notifications em app nao empacotado

O Windows exige um `AppUserModelID` associado a um atalho no Start Menu para que Toast Notifications funcionem de forma consistente em aplicativos desktop nao empacotados.

Este app usa o AppUserModelID `CertExpiryMonitor.Windows` e recria o atalho `CertExpiryMonitor.lnk` no Start Menu do usuario ao iniciar. Em ambientes corporativos, politicas do Windows podem bloquear Toasts ou ocultar botoes de acao. Quando a chamada de Toast falha, o app usa uma janela propria em primeiro plano como fallback.

## Checklist de validacao manual

- Fazer login no Windows e confirmar que o processo inicia pelo usuario atual.
- Confirmar que `%LOCALAPPDATA%\CertExpiryMonitor\settings.json` salva o horario escolhido.
- Ajustar o horario desejado e confirmar uma unica verificacao por dia.
- Usar `Testar popup agora` na aba `Configuracoes` para validar o aviso sem esperar o horario diario.
- Simular execucao apos o horario configurado e confirmar que a verificacao roda depois do atraso inicial.
- Verificar que certificados sem chave privada nao aparecem na lista.
- Validar que o Toast aparece quando ha certificados na faixa e que a janela propria aparece se Toast estiver bloqueado.
- Confirmar que `Ignorar agora` fecha apenas a notificacao atual e nao persiste `Dismissed`.
- Rodar `dotnet test .\tests\CertExpiryMonitor.Tests\CertExpiryMonitor.Tests.csproj`.
