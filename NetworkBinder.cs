using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NexusNetworkBinder
{
    /// <summary>
    /// Motor conservador do Nexus Network Binder.
    ///
    /// O modo seguro não injeta DLLs, não abre processos de jogos, não altera memória,
    /// não instala driver e não intercepta pacotes. Ele altera somente métricas de duas
    /// interfaces, cria rotas IPv4 explícitas e adiciona regras de bloqueio pertencentes
    /// ao próprio Nexus como proteção contra vazamento.
    /// </summary>
    public sealed class NetworkBinder : IDisposable
    {
        public const string Version = "21.1";
        private const string FirewallGroup = "Nexus Network Binder";
        private const string FirewallPrefix = "NexusBind_LeakGuard_";
        private const int WeakInterfaceMetric = 5;
        private const int StrongInterfaceMetric = 5000;
        private const int GameRouteMetric = 1;
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);

        // Somente ranges de jogo relativamente específicos. Blocos genéricos de AWS,
        // Azure e GCP foram removidos porque desviavam tráfego de outros aplicativos.
        public static readonly (string Cidr, string Desc)[] DefaultGameRoutes =
        {
            ("45.7.36.0/22",     "Riot BR — perfil inicial"),
            ("104.160.152.0/22", "Riot BR — perfil inicial alternativo"),
            ("138.0.12.0/23",    "Riot LATAM — perfil inicial"),
            ("45.7.40.0/22",     "Riot BR — perfil inicial 2"),
            ("162.249.72.0/21",  "Riot — infraestrutura de jogo")
        };

        private readonly AdapterInfo _weakAdapter;
        private readonly AdapterInfo _strongAdapter;
        private readonly List<RouteItem> _routes;
        private readonly List<GameExeItem> _applications;
        private readonly object _applicationLock = new();
        private readonly Action<string> _log;
        private readonly Action<bool, int> _onMonitor;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly object _stateLock = new();
        private CancellationTokenSource? _backgroundCts;
        private readonly List<Task> _backgroundTasks = new();
        private NetworkJournal? _journal;
        private bool _disposed;

        public BinderState State { get; private set; } = BinderState.Inactive;
        public event Action<BinderState, string>? StateChanged;

        public static bool HasPendingRecovery
        {
            get
            {
                if (!NetworkStateStore.TryLoad(out var journal, out _))
                    return NetworkStateStore.Exists;
                return journal is { Restored: false };
            }
        }

        public NetworkBinder(
            AdapterInfo weakAdapter,
            AdapterInfo strongAdapter,
            List<RouteItem> routes,
            List<GameExeItem> applications,
            Action<string> log,
            Action<bool, int> onMonitor)
        {
            _weakAdapter = weakAdapter;
            _strongAdapter = strongAdapter;
            _routes = routes;
            _applications = applications;
            _log = log;
            _onMonitor = onMonitor;
        }

        // Compatibilidade com código antigo que instanciava o motor usando somente IPs.
        public NetworkBinder(
            string weakIp,
            string strongIp,
            List<RouteItem> routes,
            List<GameExeItem> applications,
            Action<string> log,
            Action<bool, int> onMonitor)
            : this(
                GetAdapters().FirstOrDefault(a => a.Ip.Equals(weakIp, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("Adaptador fraco não encontrado."),
                GetAdapters().FirstOrDefault(a => a.Ip.Equals(strongIp, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("Adaptador forte não encontrado."),
                routes,
                applications,
                log,
                onMonitor)
        {
        }

        public void Activate() => ActivateAsync().GetAwaiter().GetResult();
        public void Deactivate() => DeactivateAsync().GetAwaiter().GetResult();

        public async Task<BinderOperationResult> ActivateAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State is BinderState.Active or BinderState.Preparing or BinderState.Degraded)
                    return BinderOperationResult.Fail("O Binder já está ativo ou em transição.");

                SetState(BinderState.Preparing, "Validando configuração e preparando transação...");
                var validation = ValidateConfiguration();
                if (!validation.Success)
                {
                    SetState(BinderState.Faulted, validation.Message);
                    return validation;
                }

                if (HasPendingRecovery)
                {
                    _log("[RECUPERAÇÃO] Foi encontrado um estado de rede não restaurado. Recuperando antes da nova ativação...");
                    var recovery = await JournalRecovery.RestorePendingAsync(_log, cancellationToken).ConfigureAwait(false);
                    if (!recovery.Success)
                    {
                        SetState(BinderState.Faulted, recovery.Message);
                        return recovery;
                    }
                }

                var currentWeak = ResolveCurrentAdapter(_weakAdapter.Id);
                var currentStrong = ResolveCurrentAdapter(_strongAdapter.Id);
                if (currentWeak == null || currentStrong == null)
                {
                    var message = "Um dos adaptadores selecionados não está mais disponível. Atualize a lista de interfaces.";
                    SetState(BinderState.Faulted, message);
                    return BinderOperationResult.Fail(message);
                }
                if (currentWeak.InterfaceIndex == currentStrong.InterfaceIndex)
                {
                    var message = "Os adaptadores fraco e forte precisam ser diferentes.";
                    SetState(BinderState.Faulted, message);
                    return BinderOperationResult.Fail(message);
                }
                if (string.IsNullOrWhiteSpace(currentWeak.Gateway) || string.IsNullOrWhiteSpace(currentStrong.Gateway))
                {
                    var message = "Os dois adaptadores precisam estar conectados e possuir gateway IPv4.";
                    SetState(BinderState.Faulted, message);
                    return BinderOperationResult.Fail(message);
                }

                _log("════════════════════════════════════════════════════════════");
                _log($" NEXUS NETWORK BINDER v{Version} — MODO SEGURO");
                _log(" Sem injeção, hooks, driver próprio ou acesso à memória de jogos.");
                _log("════════════════════════════════════════════════════════════");
                _log($"[ADAPTADOR] Padrão: {currentWeak.DisplayName}");
                _log($"[ADAPTADOR] Jogos:  {currentStrong.DisplayName}");

                var normalizedRoutes = NormalizeRoutes(_routes, out var routeWarnings);
                foreach (var warning in routeWarnings) _log("[AVISO] " + warning);

                _journal = new NetworkJournal
                {
                    WeakAdapterId = currentWeak.Id,
                    StrongAdapterId = currentStrong.Id,
                    WeakIp = currentWeak.Ip,
                    StrongIp = currentStrong.Ip,
                    StrongGateway = currentStrong.Gateway
                };

                var weakMetrics = await ReadInterfaceMetricsAsync(currentWeak, cancellationToken).ConfigureAwait(false);
                var strongMetrics = await ReadInterfaceMetricsAsync(currentStrong, cancellationToken).ConfigureAwait(false);
                if (!weakMetrics.Any(m => m.AddressFamily == "IPv4") ||
                    !strongMetrics.Any(m => m.AddressFamily == "IPv4"))
                {
                    var message = "Não foi possível capturar as métricas IPv4 originais das interfaces.";
                    SetState(BinderState.Faulted, message);
                    return BinderOperationResult.Fail(message);
                }
                _journal.Interfaces.AddRange(weakMetrics);
                _journal.Interfaces.AddRange(strongMetrics);

                foreach (var route in normalizedRoutes)
                {
                    var existingMetric = await GetExistingRouteMetricAsync(
                        route.Cidr,
                        currentStrong.InterfaceIndex,
                        currentStrong.Gateway,
                        cancellationToken).ConfigureAwait(false);
                    _journal.Routes.Add(new OwnedRouteJournal
                    {
                        DestinationPrefix = route.Cidr,
                        InterfaceIndex = currentStrong.InterfaceIndex,
                        NextHop = currentStrong.Gateway,
                        RouteMetric = GameRouteMetric,
                        WasPreExisting = existingMetric.HasValue,
                        OriginalRouteMetric = existingMetric ?? -1
                    });
                }

                PlanFirewallRules(_journal, "A");
                // O plano completo é salvo antes da primeira alteração. Se houver queda de energia
                // entre qualquer etapa, a recuperação conhece todos os objetos possíveis.
                try
                {
                    NetworkStateStore.Save(_journal);
                }
                catch (Exception ex)
                {
                    _journal = null;
                    var message = "Não foi possível criar o diário transacional; nenhuma alteração de rede foi aplicada. " + ex.Message;
                    SetState(BinderState.Faulted, message);
                    return BinderOperationResult.Fail(message);
                }

                try
                {
                    foreach (var snapshot in _journal.Interfaces.Where(i => i.AdapterId.Equals(currentWeak.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        snapshot.ChangeAttempted = true;
                        NetworkStateStore.Save(_journal);
                        await ApplyInterfaceMetricAsync(snapshot.InterfaceIndex, snapshot.AddressFamily, WeakInterfaceMetric, cancellationToken).ConfigureAwait(false);
                        snapshot.ChangeApplied = true;
                        NetworkStateStore.Save(_journal);
                    }
                    _log($"[MÉTRICA] ✓ {currentWeak.Name}: {WeakInterfaceMetric} (padrão IPv4/IPv6 disponível)");

                    foreach (var snapshot in _journal.Interfaces.Where(i => i.AdapterId.Equals(currentStrong.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        snapshot.ChangeAttempted = true;
                        NetworkStateStore.Save(_journal);
                        await ApplyInterfaceMetricAsync(snapshot.InterfaceIndex, snapshot.AddressFamily, StrongInterfaceMetric, cancellationToken).ConfigureAwait(false);
                        snapshot.ChangeApplied = true;
                        NetworkStateStore.Save(_journal);
                    }
                    _log($"[MÉTRICA] ✓ {currentStrong.Name}: {StrongInterfaceMetric} (rotas específicas IPv4)");

                    foreach (var route in _journal.Routes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        route.ApplyAttempted = true;
                        NetworkStateStore.Save(_journal);
                        if (route.WasPreExisting)
                        {
                            // Uma rota preexistente não pertence ao Nexus. Ela é reutilizada
                            // sem alterar sua métrica; se outra rota de mesma especificidade
                            // vencer, a verificação falha e a ativação é revertida.
                            route.Applied = true;
                            _log($"[ROTA] ✓ {route.DestinationPrefix} já existia no adaptador forte e não foi modificada");
                        }
                        else
                        {
                            await AddRouteAsync(route, cancellationToken).ConfigureAwait(false);
                            route.Applied = true;
                            _log($"[ROTA] ✓ {route.DestinationPrefix} → {route.NextHop}");
                        }
                        NetworkStateStore.Save(_journal);
                    }

                    foreach (var rule in _journal.FirewallRules)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        rule.ApplyAttempted = true;
                        NetworkStateStore.Save(_journal);
                        await CreateFirewallRuleAsync(rule, currentWeak.Ip, normalizedRoutes, cancellationToken)
                            .ConfigureAwait(false);
                        rule.Applied = true;
                        NetworkStateStore.Save(_journal);
                    }
                    if (_journal.FirewallRules.Count == 0)
                        _log("[PROTEÇÃO] Nenhum executável em modo 'Rotas seguras' foi habilitado.");
                    else
                        _log($"[PROTEÇÃO] ✓ {_journal.FirewallRules.Count} regra(s) de bloqueio de vazamento criada(s)");

                    var verification = VerifyAppliedState(currentWeak, currentStrong, normalizedRoutes);
                    if (!verification.Success)
                        throw new InvalidOperationException(verification.Message + " " + string.Join(" ", verification.Details));

                    _journal.ActivationCompleted = true;
                    NetworkStateStore.Save(_journal);
                    StartBackgroundTasks(currentWeak, currentStrong, normalizedRoutes);
                    SetState(BinderState.Active, "Roteamento ativo e verificado.");
                    PrintBanner(currentWeak, currentStrong, normalizedRoutes.Count);
                    return BinderOperationResult.Ok("Roteamento ativado e verificado.", routeWarnings);
                }
                catch (Exception ex)
                {
                    _log($"[ERRO] A ativação não foi concluída: {ex.Message}");
                    SetState(BinderState.RollingBack, "Falha na ativação; restaurando o estado anterior...");
                    var rollback = await JournalRecovery.RestoreAsync(_journal, _log, CancellationToken.None).ConfigureAwait(false);
                    _journal = null;
                    SetState(BinderState.Faulted, rollback.Success
                        ? "A ativação falhou, mas o estado anterior foi restaurado."
                        : "A ativação e a restauração falharam. Use a recuperação segura.");
                    return BinderOperationResult.Fail(
                        "Não foi possível ativar o roteamento. Nenhum estado parcial deveria permanecer.",
                        new[] { ex.Message, rollback.Message });
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task<BinderOperationResult> DeactivateAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (State == BinderState.Inactive && !HasPendingRecovery)
                    return BinderOperationResult.Ok("O Binder já está inativo.");

                SetState(BinderState.RollingBack, "Restaurando exatamente o estado anterior...");
                await StopBackgroundTasksAsync().ConfigureAwait(false);

                var journal = _journal;
                if (journal == null)
                {
                    if (!NetworkStateStore.TryLoad(out journal, out var journalError))
                    {
                        var message = "O diário transacional existe, mas está ilegível. A rede não foi alterada automaticamente. " + journalError;
                        SetState(BinderState.Faulted, message);
                        return BinderOperationResult.Fail(message);
                    }
                }
                if (journal == null)
                {
                    await RemoveAllOwnedFirewallRulesAsync(CancellationToken.None).ConfigureAwait(false);
                    SetState(BinderState.Inactive, "Nenhum diário pendente foi encontrado.");
                    return BinderOperationResult.Ok("O Binder foi desativado.");
                }

                var result = await JournalRecovery.RestoreAsync(journal, _log, CancellationToken.None).ConfigureAwait(false);
                _journal = null;
                SetState(result.Success ? BinderState.Inactive : BinderState.Faulted, result.Message);
                return result;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public void RefreshWfpRules() => RefreshFirewallRulesAsync(null, CancellationToken.None).GetAwaiter().GetResult();

        public Task<BinderOperationResult> RefreshFirewallRulesAsync(CancellationToken cancellationToken = default) =>
            RefreshFirewallRulesAsync(null, cancellationToken);

        public async Task<BinderOperationResult> RefreshFirewallRulesAsync(
            IEnumerable<GameExeItem>? applications,
            CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_journal == null || State is not (BinderState.Active or BinderState.Degraded))
                    return BinderOperationResult.Fail("Ative o Binder antes de atualizar a proteção.");

                if (applications != null)
                    ReplaceApplications(applications);

                var normalizedRoutes = NormalizeRoutes(_routes, out _);
                var oldRules = _journal.FirewallRules.ToList();
                var newRules = BuildFirewallRules(_journal, Guid.NewGuid().ToString("N")[..8]);

                // Mantém as regras antigas enquanto todas as novas são criadas. O diário
                // registra os dois conjuntos para que uma queda no meio seja recuperável.
                _journal.FirewallRules.AddRange(newRules);
                NetworkStateStore.Save(_journal);

                foreach (var rule in newRules)
                {
                    rule.ApplyAttempted = true;
                    NetworkStateStore.Save(_journal);
                    await CreateFirewallRuleAsync(rule, _journal.WeakIp, normalizedRoutes, cancellationToken)
                        .ConfigureAwait(false);
                    rule.Applied = true;
                    NetworkStateStore.Save(_journal);
                }

                foreach (var oldRule in oldRules)
                    await RemoveFirewallRuleAsync(oldRule.DisplayName, cancellationToken).ConfigureAwait(false);

                _journal.FirewallRules.RemoveAll(rule => oldRules.Contains(rule));
                NetworkStateStore.Save(_journal);
                _log($"[PROTEÇÃO] Lista atualizada transacionalmente: {newRules.Count} regra(s).");
                return BinderOperationResult.Ok("Proteção de vazamento atualizada.");
            }
            catch (Exception ex)
            {
                _log("[PROTEÇÃO] Falha ao atualizar; as regras anteriores foram preservadas quando possível: " + ex.Message);
                return BinderOperationResult.Fail("Falha ao atualizar as regras.", new[] { ex.Message });
            }
            finally { _operationGate.Release(); }
        }

        // Nome mantido apenas por compatibilidade com a UI antiga.
        public void RemoveAllWfpRules(bool silent = false)
        {
            RemoveAllOwnedFirewallRulesAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!silent) _log("[PROTEÇÃO] Regras pertencentes ao Nexus removidas.");
        }

        private BinderOperationResult ValidateConfiguration()
        {
            if (!IsAdmin())
                return BinderOperationResult.Fail("O motor de rede precisa ser executado com privilégios administrativos.");
            if (_weakAdapter.Id == _strongAdapter.Id || _weakAdapter.InterfaceIndex == _strongAdapter.InterfaceIndex)
                return BinderOperationResult.Fail("Escolha dois adaptadores diferentes.");

            var errors = new List<string>();
            foreach (var route in _routes.Where(r => r.Enabled))
            {
                if (!CidrUtility.TryNormalizeIPv4(route.Cidr, out _, out var error))
                    errors.Add($"{route.Cidr}: {error}");
                else if (CidrUtility.IsUnsafeDestination(route.Cidr, out var reason))
                    errors.Add($"{route.Cidr}: {reason}");
            }
            if (errors.Count > 0)
                return BinderOperationResult.Fail("Existem rotas inválidas ou perigosas.", errors);
            if (!_routes.Any(r => r.Enabled))
                return BinderOperationResult.Fail("Adicione ao menos uma rota habilitada para o adaptador forte.");
            return BinderOperationResult.Ok("Configuração válida.");
        }

        private static List<RouteItem> NormalizeRoutes(IEnumerable<RouteItem> routes, out List<string> warnings)
        {
            warnings = new List<string>();
            var result = new List<RouteItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var route in routes.Where(r => r.Enabled))
            {
                if (!CidrUtility.TryNormalizeIPv4(route.Cidr, out var normalized, out _)) continue;
                if (!seen.Add(normalized)) continue;
                var prefix = CidrUtility.GetPrefixLength(normalized);
                if (prefix < 16)
                    warnings.Add($"{normalized} é um bloco amplo; outros serviços dentro dele também usarão o adaptador forte.");
                result.Add(new RouteItem
                {
                    Cidr = normalized,
                    Desc = string.IsNullOrWhiteSpace(route.Desc) ? "Rota personalizada" : route.Desc.Trim(),
                    Enabled = true,
                    Source = route.Source
                });
            }
            return result;
        }

        private void PlanFirewallRules(NetworkJournal journal, string generation) =>
            journal.FirewallRules.AddRange(BuildFirewallRules(journal, generation));

        private List<FirewallRuleJournal> BuildFirewallRules(NetworkJournal journal, string generation)
        {
            var applications = SnapshotApplications();
            return applications
                .Where(a => a.Enabled &&
                            a.BindingMode == ApplicationBindingMode.GameRoutesSafe &&
                            a.PreferredAdapter == AdapterPreference.Strong &&
                            File.Exists(a.Path))
                .Select(a => Path.GetFullPath(a.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new FirewallRuleJournal
                {
                    ProgramPath = path,
                    DisplayName = FirewallPrefix + HashPath(path) + "_" + journal.TransactionId[..8] + "_" + generation
                })
                .ToList();
        }

        private void ReplaceApplications(IEnumerable<GameExeItem> applications)
        {
            lock (_applicationLock)
            {
                _applications.Clear();
                foreach (var application in applications)
                {
                    if (application.BindingMode == ApplicationBindingMode.GameRoutesSafe)
                        application.PreferredAdapter = AdapterPreference.Strong;
                    _applications.Add(application);
                }
            }
        }

        private List<GameExeItem> SnapshotApplications()
        {
            lock (_applicationLock) return _applications.ToList();
        }

        private async Task<List<InterfaceMetricSnapshot>> ReadInterfaceMetricsAsync(
            AdapterInfo adapter,
            CancellationToken cancellationToken)
        {
            var snapshots = new List<InterfaceMetricSnapshot>();
            foreach (var family in new[] { "IPv4", "IPv6" })
            {
                var script = $@"
$i=Get-NetIPInterface -InterfaceIndex {adapter.InterfaceIndex} -AddressFamily {family} -ErrorAction SilentlyContinue | Select-Object -First 1
if($null -ne $i){{
  $automatic=if($i.AutomaticMetric -eq 'Enabled' -or $i.AutomaticMetric -eq $true){{'Enabled'}}else{{'Disabled'}}
  Write-Output ($i.InterfaceIndex.ToString()+'|'+$automatic+'|'+$i.InterfaceMetric.ToString())
}}";
                var result = await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
                if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput)) continue;
                var parts = result.StandardOutput.Trim().Split('|');
                if (parts.Length < 3 || !int.TryParse(parts[0], out var index) || !int.TryParse(parts[2], out var metric))
                    continue;
                snapshots.Add(new InterfaceMetricSnapshot
                {
                    InterfaceIndex = index,
                    AdapterId = adapter.Id,
                    AdapterName = adapter.Name,
                    AddressFamily = family,
                    AutomaticMetric = parts[1],
                    InterfaceMetric = metric
                });
            }
            return snapshots;
        }

        private static async Task ApplyInterfaceMetricAsync(
            int interfaceIndex,
            string addressFamily,
            int metric,
            CancellationToken cancellationToken)
        {
            if (!addressFamily.Equals("IPv4", StringComparison.Ordinal) &&
                !addressFamily.Equals("IPv6", StringComparison.Ordinal))
                throw new ArgumentOutOfRangeException(nameof(addressFamily));
            var script = $@"
Set-NetIPInterface -InterfaceIndex {interfaceIndex} -AddressFamily {addressFamily} -AutomaticMetric Disabled -InterfaceMetric {metric} -ErrorAction Stop
Write-Output 'OK'";
            var result = await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"Não foi possível alterar a métrica {addressFamily} da interface {interfaceIndex}.");
        }

        private static async Task<int?> GetExistingRouteMetricAsync(
            string cidr,
            int interfaceIndex,
            string nextHop,
            CancellationToken cancellationToken)
        {
            var script = $@"
$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix {PowerShellRunner.Quote(cidr)} -InterfaceIndex {interfaceIndex} -ErrorAction SilentlyContinue |
Where-Object {{$_.NextHop -eq {PowerShellRunner.Quote(nextHop)}}} | Sort-Object RouteMetric | Select-Object -First 1
if($null -ne $r){{Write-Output $r.RouteMetric}}";
            var result = await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return null;
            return int.TryParse(result.StandardOutput.Trim(), out var metric) ? metric : null;
        }

        private static async Task AddRouteAsync(OwnedRouteJournal route, CancellationToken cancellationToken)
        {
            var script = $@"
New-NetRoute -AddressFamily IPv4 -DestinationPrefix {PowerShellRunner.Quote(route.DestinationPrefix)} `
-InterfaceIndex {route.InterfaceIndex} -NextHop {PowerShellRunner.Quote(route.NextHop)} `
-RouteMetric {route.RouteMetric} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
Write-Output 'OK'";
            var result = await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"Falha ao criar a rota {route.DestinationPrefix}.");
        }

        private static async Task SetExistingRouteMetricAsync(
            OwnedRouteJournal route,
            int metric,
            CancellationToken cancellationToken)
        {
            var script = $@"
$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix {PowerShellRunner.Quote(route.DestinationPrefix)} `
-InterfaceIndex {route.InterfaceIndex} -ErrorAction Stop | Where-Object {{$_.NextHop -eq {PowerShellRunner.Quote(route.NextHop)}}}
$r | Set-NetRoute -RouteMetric {metric} -ErrorAction Stop
Write-Output 'OK'";
            var result = await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"Falha ao ajustar a rota existente {route.DestinationPrefix}.");
        }

        private async Task CreateFirewallRuleAsync(
            FirewallRuleJournal rule,
            string weakIp,
            IReadOnlyCollection<RouteItem> routes,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(rule.ProgramPath) || !File.Exists(rule.ProgramPath))
                throw new FileNotFoundException("O executável da regra de proteção não foi encontrado.", rule.ProgramPath);
            if (routes.Count == 0)
                throw new InvalidOperationException("Não há destinos válidos para a regra de proteção.");

            var ranges = string.Join(",", routes.Select(r => PowerShellRunner.Quote(r.Cidr)));
            var script = $@"
Remove-NetFirewallRule -DisplayName {PowerShellRunner.Quote(rule.DisplayName)} -ErrorAction SilentlyContinue
$addresses=@({ranges})
New-NetFirewallRule -DisplayName {PowerShellRunner.Quote(rule.DisplayName)} `
-Group {PowerShellRunner.Quote(FirewallGroup)} -Direction Outbound -Action Block `
-Program {PowerShellRunner.Quote(Path.GetFullPath(rule.ProgramPath))} -LocalAddress {PowerShellRunner.Quote(weakIp)} `
-RemoteAddress $addresses -Profile Any -Enabled True -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
Write-Output 'OK'";
            var result = await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"Falha ao criar a proteção para {Path.GetFileName(rule.ProgramPath)}.");
            _log($"[PROTEÇÃO] ✓ {Path.GetFileNameWithoutExtension(rule.ProgramPath)}: bloqueio de vazamento pela interface padrão");
        }

        private static async Task RemoveFirewallRuleAsync(string displayName, CancellationToken cancellationToken)
        {
            var script = $"Remove-NetFirewallRule -DisplayName {PowerShellRunner.Quote(displayName)} -ErrorAction SilentlyContinue";
            await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
        }

        private static async Task RemoveAllOwnedFirewallRulesAsync(CancellationToken cancellationToken)
        {
            var script = $"Get-NetFirewallRule -Group {PowerShellRunner.Quote(FirewallGroup)} -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue";
            await PowerShellRunner.RunAsync(script, CommandTimeout, cancellationToken).ConfigureAwait(false);
        }

        private BinderOperationResult VerifyAppliedState(
            AdapterInfo weak,
            AdapterInfo strong,
            IReadOnlyCollection<RouteItem> routes)
        {
            var errors = new List<string>();
            var defaultProbe = ChooseDefaultProbe(routes);
            var defaultInterface = GetBestInterfaceIndex(defaultProbe);
            if (defaultInterface <= 0)
                errors.Add($"Não foi possível determinar a interface padrão para {defaultProbe}.");
            else if (defaultInterface != weak.InterfaceIndex)
                errors.Add($"O tráfego padrão ainda escolhe a interface {defaultInterface}, não '{weak.Name}'. Uma VPN ou política externa pode estar sobrepondo as métricas.");

            var ipv6DefaultInterface = GetBestInterfaceIndexFromPowerShell("2606:4700:4700::1111");
            if (ipv6DefaultInterface.HasValue && ipv6DefaultInterface.Value != weak.InterfaceIndex)
                errors.Add($"O tráfego padrão IPv6 escolhe a interface {ipv6DefaultInterface.Value}, não '{weak.Name}'.");

            foreach (var route in routes)
            {
                var probe = CidrUtility.GetProbeAddress(route.Cidr);
                var best = GetBestInterfaceIndex(probe);
                if (best != strong.InterfaceIndex)
                    errors.Add($"{route.Cidr} escolheu a interface {best}, em vez de '{strong.Name}'.");
            }

            return errors.Count == 0
                ? BinderOperationResult.Ok("Estado verificado.")
                : BinderOperationResult.Fail("A verificação de rota falhou.", errors);
        }

        private static int? GetBestInterfaceIndexFromPowerShell(string remoteAddress)
        {
            var script = $@"
$r=Find-NetRoute -RemoteIPAddress {PowerShellRunner.Quote(remoteAddress)} -ErrorAction SilentlyContinue | Select-Object -First 1
if($null -ne $r){{Write-Output $r.InterfaceIndex}}";
            var result = PowerShellRunner.Run(script, TimeSpan.FromSeconds(8));
            return result.Success && int.TryParse(result.StandardOutput.Trim(), out var index) ? index : null;
        }

        private static string ChooseDefaultProbe(IEnumerable<RouteItem> routes)
        {
            var candidates = new[] { "9.9.9.9", "1.1.1.1", "8.8.8.8", "208.67.222.222" };
            return candidates.FirstOrDefault(ip => routes.All(r => !CidrUtility.Contains(r.Cidr, ip))) ?? "9.9.9.9";
        }

        private void StartBackgroundTasks(
            AdapterInfo weak,
            AdapterInfo strong,
            IReadOnlyCollection<RouteItem> routes)
        {
            _backgroundCts = new CancellationTokenSource();
            var token = _backgroundCts.Token;
            _backgroundTasks.Clear();
            _backgroundTasks.Add(Task.Run(() => WatchdogLoopAsync(weak, strong, routes, token), token));
            _backgroundTasks.Add(Task.Run(() => ApplicationPresenceLoopAsync(token), token));
            _backgroundTasks.Add(Task.Run(() => DiscordTcpMonitorLoopAsync(strong.Ip, token), token));
        }

        private async Task StopBackgroundTasksAsync()
        {
            if (_backgroundCts == null) return;
            _backgroundCts.Cancel();
            try { await Task.WhenAll(_backgroundTasks).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false); }
            catch { }
            _backgroundTasks.Clear();
            _backgroundCts.Dispose();
            _backgroundCts = null;
        }

        private async Task WatchdogLoopAsync(
            AdapterInfo weak,
            AdapterInfo strong,
            IReadOnlyCollection<RouteItem> routes,
            CancellationToken cancellationToken)
        {
            _log("[WATCHDOG] Monitor seguro iniciado.");
            using var timer = new PeriodicTimer(WatchdogInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var currentWeak = ResolveCurrentAdapter(weak.Id);
                    var currentStrong = ResolveCurrentAdapter(strong.Id);
                    if (currentWeak == null || currentStrong == null)
                    {
                        SetState(BinderState.Degraded, "Um adaptador ficou offline. Nenhuma configuração externa será apagada.");
                        continue;
                    }
                    if (!currentWeak.Ip.Equals(weak.Ip, StringComparison.OrdinalIgnoreCase) ||
                        !currentStrong.Ip.Equals(strong.Ip, StringComparison.OrdinalIgnoreCase) ||
                        !currentStrong.Gateway.Equals(strong.Gateway, StringComparison.OrdinalIgnoreCase))
                    {
                        SetState(BinderState.Degraded, "DHCP ou gateway mudou. Desative e reative para criar uma nova transação segura.");
                        continue;
                    }

                    var journal = _journal;
                    if (journal != null)
                    {
                        foreach (var route in journal.Routes.Where(r => r.Applied && !r.WasPreExisting))
                        {
                            var metric = await GetExistingRouteMetricAsync(
                                route.DestinationPrefix,
                                route.InterfaceIndex,
                                route.NextHop,
                                cancellationToken).ConfigureAwait(false);
                            if (!metric.HasValue)
                            {
                                _log($"[WATCHDOG] Rota própria ausente; recriando {route.DestinationPrefix}...");
                                await AddRouteAsync(route, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    var verification = VerifyAppliedState(currentWeak, currentStrong, routes);
                    if (!verification.Success)
                    {
                        SetState(BinderState.Degraded, verification.Details.FirstOrDefault() ?? verification.Message);
                    }
                    else if (State == BinderState.Degraded)
                    {
                        SetState(BinderState.Active, "Roteamento voltou ao estado esperado.");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log("[WATCHDOG] " + ex.Message);
                    SetState(BinderState.Degraded, "O monitor detectou uma falha de verificação.");
                }
            }
            _log("[WATCHDOG] Encerrado.");
        }

        private async Task ApplicationPresenceLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var app in SnapshotApplications().Where(a => a.Enabled && File.Exists(a.Path)))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(app.Path);
                        if (Process.GetProcessesByName(name).Length > 0) app.LastSeen = DateTime.Now;
                    }
                    catch { }
                }
            }
        }

        private async Task DiscordTcpMonitorLoopAsync(string strongIp, CancellationToken cancellationToken)
        {
            _log("[MONITOR] Monitor TCP IPv4 do Discord iniciado. UDP e IPv6 não são afirmados neste modo.");
            using var timer = new PeriodicTimer(MonitorInterval);
            var lastLeak = false;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var pids = Process.GetProcessesByName("Discord").Select(p => p.Id.ToString()).ToHashSet();
                    if (pids.Count == 0)
                    {
                        _onMonitor(false, 0);
                        continue;
                    }
                    var output = RunProcess("netstat.exe", new[] { "-ano", "-p", "tcp" }, TimeSpan.FromSeconds(8));
                    var leaks = output.Split('\n').Count(line =>
                    {
                        var columns = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        return columns.Length >= 5 &&
                               pids.Contains(columns[^1]) &&
                               columns[1].StartsWith(strongIp + ":", StringComparison.OrdinalIgnoreCase);
                    });
                    if (leaks > 0 && !lastLeak)
                        _log($"[MONITOR] Discord possui {leaks} conexão(ões) TCP IPv4 na interface forte.");
                    if (leaks == 0 && lastLeak)
                        _log("[MONITOR] Nenhum vazamento TCP IPv4 do Discord detectado.");
                    lastLeak = leaks > 0;
                    _onMonitor(lastLeak, leaks);
                }
                catch { }
            }
        }

        private void PrintBanner(AdapterInfo weak, AdapterInfo strong, int routeCount)
        {
            var protectedApps = SnapshotApplications().Count(a => a.Enabled && a.BindingMode == ApplicationBindingMode.GameRoutesSafe);
            _log("");
            _log("╔══════════════════════════════════════════════════════════╗");
            _log($"║  NEXUS BINDER v{Version} — ATIVO E VERIFICADO             ║");
            _log($"║  Padrão: {TrimForBanner(weak.Name),-42}║");
            _log($"║  Jogos : {TrimForBanner(strong.Name),-42}║");
            _log($"║  Rotas : {routeCount,-42}║");
            _log($"║  Apps protegidos: {protectedApps,-33}║");
            _log("╚══════════════════════════════════════════════════════════╝");
        }

        private static string TrimForBanner(string value) => value.Length <= 42 ? value : value[..39] + "...";

        private void SetState(BinderState state, string message)
        {
            lock (_stateLock) State = state;
            _log($"[ESTADO] {state}: {message}");
            try { StateChanged?.Invoke(state, message); } catch { }
        }

        private static string HashPath(string path)
        {
            var normalized = Path.GetFullPath(path).ToUpperInvariant();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash.AsSpan(0, 8));
        }

        private static void EnsureSuccess(ProcessResult result, string message)
        {
            if (result.Success) return;
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : message + " " + detail);
        }

        private static string RunProcess(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process == null) return "";
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "";
            }
            return output.GetAwaiter().GetResult();
        }

        public static List<AdapterInfo> GetAdapters()
        {
            var adapters = new List<AdapterInfo>();
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up) continue;
                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                try
                {
                    var properties = networkInterface.GetIPProperties();
                    var ip = properties.UnicastAddresses
                        .Select(u => u.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (ip == null) continue;
                    var gateway = properties.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "";
                    var ipv4 = properties.GetIPv4Properties();
                    if (ipv4 == null) continue;
                    var text = (networkInterface.Name + " " + networkInterface.Description).ToLowerInvariant();
                    var isVirtual = text.Contains("virtual") || text.Contains("vmware") || text.Contains("hyper-v") ||
                                    text.Contains("wsl") || text.Contains("tap") || text.Contains("vpn");
                    adapters.Add(new AdapterInfo(
                        networkInterface.Id,
                        networkInterface.Name,
                        ip.ToString(),
                        networkInterface.Description,
                        ipv4.Index,
                        gateway,
                        isVirtual));
                }
                catch { }
            }
            return adapters.OrderBy(a => a.IsVirtual).ThenBy(a => a.Name).ToList();
        }

        private static AdapterInfo? ResolveCurrentAdapter(string id) =>
            GetAdapters().FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public static bool IsAdmin()
        {
            try
            {
                var principal = new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent());
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static (uint start, uint end)? ParseCidrRange(string cidr) =>
            CidrUtility.TryGetRange(cidr, out var start, out var end) ? (start, end) : null;

        public static bool IsIpInRange((uint start, uint end) range, string ip)
        {
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                return false;
            var bytes = address.GetAddressBytes();
            var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            return value >= range.start && value <= range.end;
        }

        public static RouteCompatibilityResult AnalyzeRouteCompatibility(string exePath, List<RouteItem> routes)
        {
            var result = new RouteCompatibilityResult { ExePath = exePath };
            var details = new List<string>();
            var normalized = NormalizeRoutes(routes, out _);
            var configured = 0;
            var correctlySelected = 0;

            details.Add("── TABELA DE ROTAS E MELHOR INTERFACE ──");
            foreach (var route in normalized)
            {
                var script = $@"
$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix {PowerShellRunner.Quote(route.Cidr)} -ErrorAction SilentlyContinue |
Sort-Object {{$_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric}} | Select-Object -First 1
if($null -ne $r){{Write-Output ($r.InterfaceIndex.ToString()+'|'+$r.InterfaceAlias+'|'+$r.NextHop+'|'+$r.RouteMetric.ToString())}}";
                var ps = PowerShellRunner.Run(script);
                var line = ps.StandardOutput.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    details.Add($"  ✗ {route.Cidr,-20} sem rota explícita");
                    continue;
                }
                configured++;
                var columns = line.Split('|');
                var probe = CidrUtility.GetProbeAddress(route.Cidr);
                var best = GetBestInterfaceIndex(probe);
                var routeIndex = columns.Length > 0 && int.TryParse(columns[0], out var idx) ? idx : -1;
                if (best == routeIndex)
                {
                    correctlySelected++;
                    details.Add($"  ✓ {route.Cidr,-20} → {columns.ElementAtOrDefault(1)} ({columns.ElementAtOrDefault(2)})");
                }
                else
                {
                    details.Add($"  ⚠ {route.Cidr,-20} existe, mas o Windows escolhe a interface {best}");
                }
            }

            var processName = Path.GetFileNameWithoutExtension(exePath);
            var pids = new HashSet<int>();
            try { foreach (var process in Process.GetProcessesByName(processName)) pids.Add(process.Id); } catch { }
            var running = pids.Count > 0;
            details.Add("");
            details.Add(running
                ? $"── PROCESSO: {processName} em execução (PID: {string.Join(", ", pids)}) ──"
                : $"── PROCESSO: {processName} não está em execução ──");

            if (running)
            {
                var pidList = string.Join(",", pids);
                var script = $@"
$pids=@({pidList})
Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
Where-Object {{$_.OwningProcess -in $pids -and $_.RemoteAddress -match '^\d+\.\d+\.\d+\.\d+$'}} |
ForEach-Object {{Write-Output ($_.RemoteAddress+'|'+$_.RemotePort)}}";
                var live = PowerShellRunner.Run(script).StandardOutput
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                details.Add("");
                details.Add("── CONEXÕES TCP IPv4 OBSERVADAS ──");
                if (live.Count == 0) details.Add("  ℹ Nenhuma conexão TCP estabelecida agora.");
                foreach (var connection in live.Take(20)) details.Add("  [TCP] " + connection.Replace('|', ':'));
                details.Add("  ℹ UDP remoto não é inferido pelo cmdlet de endpoints locais. O modo seguro não apresenta destino UDP falso.");
            }

            if (normalized.Count == 0)
            {
                result.Status = CompatibilityStatus.NoMatch;
                result.Summary = "Nenhuma rota válida foi configurada.";
            }
            else if (correctlySelected == normalized.Count)
            {
                result.Status = running ? CompatibilityStatus.Compatible : CompatibilityStatus.NotRunning;
                result.Summary = $"✓ Todas as {normalized.Count} rota(s) existem e são escolhidas pelo Windows.\n" +
                                 (running ? "✓ O processo está em execução." : "ℹ O processo não está aberto; a rota pode ser verificada mesmo assim.");
            }
            else if (configured > 0)
            {
                result.Status = CompatibilityStatus.Partial;
                result.Summary = $"⚠ {correctlySelected}/{normalized.Count} rota(s) estão sendo escolhidas corretamente.";
            }
            else
            {
                result.Status = running ? CompatibilityStatus.NoMatch : CompatibilityStatus.NotRunning;
                result.Summary = "✗ As rotas configuradas não foram encontradas na tabela do Windows.";
            }
            result.Details = details;
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SockaddrIn
        {
            public short Family;
            public ushort Port;
            public uint Address;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Zero;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetBestInterfaceEx(IntPtr destinationAddress, out uint bestInterfaceIndex);

        private static int GetBestInterfaceIndex(string destination)
        {
            if (!IPAddress.TryParse(destination, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return -1;
            var socketAddress = new SockaddrIn
            {
                Family = (short)AddressFamily.InterNetwork,
                Port = 0,
                Address = BitConverter.ToUInt32(ip.GetAddressBytes(), 0),
                Zero = new byte[8]
            };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<SockaddrIn>());
            try
            {
                Marshal.StructureToPtr(socketAddress, pointer, false);
                return GetBestInterfaceEx(pointer, out var index) == 0 ? unchecked((int)index) : -1;
            }
            catch { return -1; }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NetworkBinder));
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { StopBackgroundTasksAsync().GetAwaiter().GetResult(); } catch { }
            _operationGate.Dispose();
            _disposed = true;
        }
    }

    internal static class JournalRecovery
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

        public static async Task<BinderOperationResult> RestorePendingAsync(
            Action<string> log,
            CancellationToken cancellationToken)
        {
            if (!NetworkStateStore.TryLoad(out var journal, out var loadError))
                return BinderOperationResult.Fail(
                    "O diário de recuperação está ilegível. Nenhuma alteração automática foi feita.",
                    new[] { loadError });
            if (journal == null || journal.Restored)
                return BinderOperationResult.Ok("Nenhum estado pendente.");
            return await RestoreAsync(journal, log, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<BinderOperationResult> RestoreAsync(
            NetworkJournal journal,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            var errors = new List<string>();
            var validationErrors = ValidateJournal(journal);
            if (validationErrors.Count > 0)
            {
                foreach (var error in validationErrors) log("[RESTAURAÇÃO] ✗ Diário inválido: " + error);
                return BinderOperationResult.Fail(
                    "O diário não passou na validação de propriedade. Nenhuma alteração automática foi feita.",
                    validationErrors);
            }

            log("[RESTAURAÇÃO] Removendo somente objetos registrados no diário da transação...");

            foreach (var rule in journal.FirewallRules)
            {
                try
                {
                    var script = $"Remove-NetFirewallRule -DisplayName {PowerShellRunner.Quote(rule.DisplayName)} -ErrorAction SilentlyContinue";
                    var ps = await PowerShellRunner.RunAsync(script, Timeout, cancellationToken).ConfigureAwait(false);
                    if (!ps.Success)
                        errors.Add($"Regra {rule.DisplayName}: {ps.StandardError.Trim()}");
                }
                catch (Exception ex) { errors.Add($"Regra {rule.DisplayName}: {ex.Message}"); }
            }

            var currentStrongIndex = TryResolveAdapterIndex(journal.StrongAdapterId);
            foreach (var route in journal.Routes.AsEnumerable().Reverse())
            {
                try
                {
                    if (!route.ApplyAttempted) continue;

                    // Nunca usa um índice que possa ter sido reutilizado por outra placa.
                    // Se a mesma interface não ocupa mais o índice registrado, o diário é
                    // preservado para inspeção em vez de tocar uma rota potencialmente externa.
                    if (!currentStrongIndex.HasValue || currentStrongIndex.Value != route.InterfaceIndex)
                    {
                        errors.Add($"Rota {route.DestinationPrefix}: o adaptador original não está mais no índice {route.InterfaceIndex}; nenhuma rota foi alterada.");
                        continue;
                    }

                    if (route.WasPreExisting)
                    {
                        if (route.OriginalRouteMetric >= 0 && route.ExistingMetricChanged)
                        {
                            var script = $@"
$r=Get-NetRoute -AddressFamily IPv4 -DestinationPrefix {PowerShellRunner.Quote(route.DestinationPrefix)} `
-InterfaceIndex {route.InterfaceIndex} -ErrorAction SilentlyContinue | Where-Object {{$_.NextHop -eq {PowerShellRunner.Quote(route.NextHop)}}}
if($null -ne $r){{$r | Set-NetRoute -RouteMetric {route.OriginalRouteMetric} -ErrorAction Stop}}";
                            var ps = await PowerShellRunner.RunAsync(script, Timeout, cancellationToken).ConfigureAwait(false);
                            if (!ps.Success) errors.Add($"Rota {route.DestinationPrefix}: {ps.StandardError.Trim()}");
                        }
                    }
                    else if (route.ApplyAttempted)
                    {
                        var script = $@"
Get-NetRoute -AddressFamily IPv4 -DestinationPrefix {PowerShellRunner.Quote(route.DestinationPrefix)} `
-InterfaceIndex {route.InterfaceIndex} -ErrorAction SilentlyContinue |
Where-Object {{$_.NextHop -eq {PowerShellRunner.Quote(route.NextHop)} -and $_.RouteMetric -eq {route.RouteMetric}}} |
Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
                        var ps = await PowerShellRunner.RunAsync(script, Timeout, cancellationToken).ConfigureAwait(false);
                        if (!ps.Success) errors.Add($"Rota {route.DestinationPrefix}: {ps.StandardError.Trim()}");
                    }
                }
                catch (Exception ex) { errors.Add($"Rota {route.DestinationPrefix}: {ex.Message}"); }
            }

            foreach (var snapshot in journal.Interfaces.AsEnumerable().Reverse())
            {
                try
                {
                    if (journal.SchemaVersion >= 3 && !snapshot.ChangeAttempted)
                        continue;

                    var currentIndex = TryResolveAdapterIndex(snapshot.AdapterId);
                    if (!currentIndex.HasValue)
                    {
                        errors.Add($"Métrica {snapshot.AddressFamily} de {snapshot.AdapterName}: o adaptador original não está disponível; nenhuma outra interface foi alterada.");
                        continue;
                    }

                    string script;
                    if (snapshot.AutomaticMetric.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
                    {
                        script = $"Set-NetIPInterface -InterfaceIndex {currentIndex.Value} -AddressFamily {snapshot.AddressFamily} -AutomaticMetric Enabled -ErrorAction Stop";
                    }
                    else
                    {
                        script = $"Set-NetIPInterface -InterfaceIndex {currentIndex.Value} -AddressFamily {snapshot.AddressFamily} -AutomaticMetric Disabled -InterfaceMetric {snapshot.InterfaceMetric} -ErrorAction Stop";
                    }
                    var ps = await PowerShellRunner.RunAsync(script, Timeout, cancellationToken).ConfigureAwait(false);
                    if (!ps.Success) errors.Add($"Métrica {snapshot.AddressFamily} de {snapshot.AdapterName}: {ps.StandardError.Trim()}");
                }
                catch (Exception ex) { errors.Add($"Métrica {snapshot.AddressFamily} de {snapshot.AdapterName}: {ex.Message}"); }
            }

            journal.Restored = errors.Count == 0;
            try { NetworkStateStore.Save(journal); } catch { }
            if (errors.Count == 0)
            {
                NetworkStateStore.Delete();
                log("[RESTAURAÇÃO] ✓ Rotas próprias, regras e métricas originais restauradas.");
                return BinderOperationResult.Ok("Estado anterior restaurado com segurança.");
            }

            foreach (var error in errors) log("[RESTAURAÇÃO] ✗ " + error);
            return BinderOperationResult.Fail("Parte da restauração falhou. O diário foi mantido para nova tentativa.", errors);
        }

        private static List<string> ValidateJournal(NetworkJournal journal)
        {
            var errors = new List<string>();
            if (journal.SchemaVersion < 1 || journal.SchemaVersion > 3)
                errors.Add($"Versão de diário não suportada: {journal.SchemaVersion}.");
            if (string.IsNullOrWhiteSpace(journal.TransactionId) ||
                journal.TransactionId.Length > 64 ||
                journal.TransactionId.Any(c => !Uri.IsHexDigit(c)))
                errors.Add("Identificador de transação inválido.");
            if (string.IsNullOrWhiteSpace(journal.WeakAdapterId) || string.IsNullOrWhiteSpace(journal.StrongAdapterId))
                errors.Add("Identidade dos adaptadores ausente.");
            if (journal.Interfaces == null || journal.Routes == null || journal.FirewallRules == null)
            {
                errors.Add("Uma ou mais coleções obrigatórias do diário estão ausentes.");
                return errors;
            }
            if (journal.Interfaces.Count > 8 || journal.Routes.Count > 4096 || journal.FirewallRules.Count > 4096)
                errors.Add("O diário excede os limites de segurança.");

            foreach (var snapshot in journal.Interfaces)
            {
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.AdapterId) || snapshot.InterfaceIndex <= 0)
                {
                    errors.Add("Snapshot de interface inválido.");
                    continue;
                }
                if (!snapshot.AddressFamily.Equals("IPv4", StringComparison.Ordinal) &&
                    !snapshot.AddressFamily.Equals("IPv6", StringComparison.Ordinal))
                    errors.Add("Família de endereço inválida no snapshot.");
                if (!snapshot.AutomaticMetric.Equals("Enabled", StringComparison.Ordinal) &&
                    !snapshot.AutomaticMetric.Equals("Disabled", StringComparison.Ordinal))
                    errors.Add("Estado de métrica automática inválido.");
                if (snapshot.InterfaceMetric < 0 || snapshot.InterfaceMetric > 9999)
                    errors.Add("Métrica de interface fora do intervalo esperado.");
            }

            foreach (var route in journal.Routes)
            {
                if (route == null)
                {
                    errors.Add("Entrada de rota nula no diário.");
                    continue;
                }
                if (!CidrUtility.TryNormalizeIPv4(route.DestinationPrefix, out var normalized, out _) ||
                    !normalized.Equals(route.DestinationPrefix, StringComparison.OrdinalIgnoreCase) ||
                    CidrUtility.IsUnsafeDestination(normalized, out _))
                    errors.Add($"Rota não reconhecida como propriedade segura: {route.DestinationPrefix}.");
                if (!IPAddress.TryParse(route.NextHop, out var nextHop) || nextHop.AddressFamily != AddressFamily.InterNetwork)
                    errors.Add($"Gateway inválido no diário: {route.NextHop}.");
                if (route.InterfaceIndex <= 0 || route.RouteMetric < 0 || route.RouteMetric > 9999 ||
                    route.OriginalRouteMetric < -1 || route.OriginalRouteMetric > 9999)
                    errors.Add($"Métricas ou interface inválidas para {route.DestinationPrefix}.");
            }

            foreach (var rule in journal.FirewallRules)
            {
                if (rule == null)
                {
                    errors.Add("Entrada de firewall nula no diário.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(rule.DisplayName) ||
                    !rule.DisplayName.StartsWith("NexusBind_LeakGuard_", StringComparison.Ordinal) ||
                    rule.DisplayName.Length > 200)
                    errors.Add("Nome de regra de firewall fora do namespace do Nexus.");
            }

            return errors.Distinct(StringComparer.Ordinal).ToList();
        }

        private static int? TryResolveAdapterIndex(string adapterId)
        {
            if (string.IsNullOrWhiteSpace(adapterId)) return null;
            try
            {
                var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(item => item.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
                return networkInterface?.GetIPProperties().GetIPv4Properties()?.Index;
            }
            catch { return null; }
        }
    }

    public static class EmergencyReset
    {
        /// <summary>
        /// Recuperação segura: usa somente o diário persistente do Nexus. Não remove rotas
        /// /32 genéricas, não redefine todas as interfaces e não altera gateways de terceiros.
        /// </summary>
        public static List<string> Run()
        {
            var lines = new List<string>();
            void Log(string line) => lines.Add(line);
            try
            {
                var result = JournalRecovery.RestorePendingAsync(Log, CancellationToken.None).GetAwaiter().GetResult();
                if (!NetworkBinder.HasPendingRecovery)
                {
                    var cleanup = $"Get-NetFirewallRule -Group {PowerShellRunner.Quote("Nexus Network Binder")} -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue";
                    PowerShellRunner.Run(cleanup);
                }
                lines.Add(result.Success
                    ? "[RESET] ✓ Recuperação segura concluída. Nenhuma rota externa foi tocada."
                    : "[RESET] ✗ A recuperação ficou incompleta; o diário foi preservado.");
            }
            catch (Exception ex)
            {
                lines.Add("[RESET] ✗ " + ex.Message);
            }
            return lines;
        }
    }
}
