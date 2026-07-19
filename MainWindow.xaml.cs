using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.Windows.Threading;
using System.Net.Sockets;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace NexusNetworkBinder
{
    // ── Animação de GridLength ─────────────────────────────────────────────
    public class GridLengthAnimation : AnimationTimeline
    {
        public GridLength From { get => (GridLength)GetValue(FromProperty); set => SetValue(FromProperty, value); }
        public static readonly DependencyProperty FromProperty = DependencyProperty.Register("From", typeof(GridLength), typeof(GridLengthAnimation));
        public GridLength To   { get => (GridLength)GetValue(ToProperty);   set => SetValue(ToProperty, value); }
        public static readonly DependencyProperty ToProperty   = DependencyProperty.Register("To",   typeof(GridLength), typeof(GridLengthAnimation));
        public IEasingFunction? EasingFunction { get; set; }
        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
        public override object GetCurrentValue(object d, object dd, AnimationClock clock)
        {
            double p = clock.CurrentProgress ?? 0;
            if (EasingFunction != null) p = EasingFunction.Ease(p);
            return new GridLength(From.Value + (To.Value - From.Value) * p);
        }
    }

    // ── ViewModels de tráfego/ping ─────────────────────────────────────────
    public class AdapterTrafficItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        void N(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public string AdapterName { get; set; } = "";
        public string Ip          { get; set; } = "";
        string _down="0 bps",_up="0 bps",_pct="--"; double _bar=0;
        public string DownSpeed    { get=>_down; set{_down=value;N(nameof(DownSpeed));} }
        public string UpSpeed      { get=>_up;   set{_up=value;N(nameof(UpSpeed));} }
        public string UsagePercent { get=>_pct;  set{_pct=value;N(nameof(UsagePercent));} }
        public double BarWidth     { get=>_bar;  set{_bar=value;N(nameof(BarWidth));} }
        public long LastRecv, LastSent; public string NicId=""; public long SpeedBps=0;
        public DateTime LastSampleUtc { get; set; } = DateTime.UtcNow;
    }

    public class AdapterPingItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        void N(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public string AdapterName{get;set;}=""; public string Ip{get;set;}="";
        string _pt="--",_jt="--"; SolidColorBrush _pc=new(Color.FromRgb(0x47,0x55,0x69));
        public string          PingText   {get=>_pt;set{_pt=value;N(nameof(PingText));}}
        public string          JitterText {get=>_jt;set{_jt=value;N(nameof(JitterText));}}
        public SolidColorBrush PingColor  {get=>_pc;set{_pc=value;N(nameof(PingColor));}}
        public List<long> History=new(); public int Sent,Lost;
    }

    /// <summary>
    /// ViewModel para a grade de exes na aba Jogos.
    /// Wraps GameExeItem com notificação de propriedade para o DataGrid.
    /// </summary>
    public class GameExeViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        void N(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public GameExeItem Model { get; }
        public GameExeViewModel(GameExeItem m) { Model = m; }

        public string Path    { get => Model.Path;    set { Model.Path=value;    N(nameof(Path));    N(nameof(FileName)); N(nameof(FileExists)); N(nameof(StatusColor)); } }
        public string Name    { get => Model.Name;    set { Model.Name=value;    N(nameof(Name)); } }
        public bool   Enabled { get => Model.Enabled; set { Model.Enabled=value; N(nameof(Enabled)); N(nameof(StatusColor)); } }
        public ApplicationBindingMode BindingMode { get => Model.BindingMode; set { Model.BindingMode=value; N(nameof(BindingMode)); N(nameof(BindingModeText)); N(nameof(PreferredAdapterText)); } }
        public AdapterPreference PreferredAdapter { get => Model.PreferredAdapter; set { Model.PreferredAdapter=value; N(nameof(PreferredAdapter)); N(nameof(PreferredAdapterText)); } }
        public string BindingModeText => BindingMode switch
        {
            ApplicationBindingMode.GameRoutesSafe => "Rotas seguras",
            ApplicationBindingMode.ProxyCompatible => "Proxy TCP",
            _ => "Somente observar"
        };
        public string PreferredAdapterText => BindingMode == ApplicationBindingMode.GameRoutesSafe
            ? "Forte (rotas)"
            : PreferredAdapter == AdapterPreference.Strong ? "Forte" : "Padrão";

        public string FileName    => System.IO.Path.GetFileName(Path);
        public bool   FileExists  => File.Exists(Path);
        public string LastSeenStr => Model.LastSeen.HasValue
            ? Model.LastSeen.Value.ToString("HH:mm:ss")
            : "--";
        public SolidColorBrush StatusColor => !FileExists
            ? new SolidColorBrush(Color.FromRgb(0xEF,0x44,0x44))
            : Enabled
                ? new SolidColorBrush(Color.FromRgb(0x10,0xB9,0x81))
                : new SolidColorBrush(Color.FromRgb(0x94,0xA3,0xB8));

        public void Refresh() { N(nameof(LastSeenStr)); N(nameof(StatusColor)); N(nameof(FileExists)); N(nameof(BindingModeText)); N(nameof(PreferredAdapterText)); }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MainWindow
    // ══════════════════════════════════════════════════════════════════════════
    public partial class MainWindow : Window
    {
        // ── Estado ────────────────────────────────────────────────────────────
        NetworkBinder? _binder;
        bool _active = false;
        List<AdapterInfo> _adapters = new();

        DispatcherTimer? _netTimer;
        NetworkInterface? _inspectedNic;
        readonly ObservableCollection<AdapterTrafficItem> _trafficItems = new();

        DispatcherTimer? _pingTimer;
        readonly ObservableCollection<AdapterPingItem>    _pingItems    = new();
        bool _pinging = false;
        bool _pingTickRunning = false;

        readonly ObservableCollection<RouteItem>       _customRoutes = new();
        readonly ObservableCollection<GameExeViewModel> _gameExeVMs   = new();
        readonly ProxyBindingManager _proxyBindingManager = new();
        string? _savedWeakAdapterId;
        string? _savedStrongAdapterId;

        System.Windows.Forms.NotifyIcon? _trayIcon;
        bool _forceClose = false;
        bool _closeRestoreInProgress = false;
        bool _allowCloseAfterRestore = false;
        NetworkAddressChangedEventHandler? _netChangeHandler;
        bool _isDarkTheme = true;
        bool _navCollapsedByResponsiveLayout = false;
        bool _responsiveLayoutInitialized = false;
        BinderState _currentBinderState = BinderState.Inactive;
        string _currentBinderDetail = "Inativo";

        // ── Config ─────────────────────────────────────────────────────────────
        public class AppConfig
        {
            public bool            StartWithWindows { get; set; }
            public bool            MinimizeToTray   { get; set; }
            public bool            AutoActivate     { get; set; }
            public bool            KillDiscord      { get; set; } // legado: ignorado
            public string          Theme           { get; set; } = "Dark";
            public string          WeakAdapterId    { get; set; } = "";
            public string          StrongAdapterId  { get; set; } = "";
            public List<RouteItem>    CustomRoutes  { get; set; } = new();
            public List<GameExeItem>  GameExes      { get; set; } = new();
        }

        // ── Construtor ─────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            FitWindowToWorkArea();

            if (!NetworkBinder.IsAdmin())
            {
                MessageBox.Show("Execute como ADMINISTRADOR.", "Erro de Privilégio",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            // Vincula coleções observáveis aos controles
            DgCustomRoutes.ItemsSource    = _customRoutes;
            DgGameExes.ItemsSource        = _gameExeVMs;
            AdapterTrafficList.ItemsSource= _trafficItems;
            AdapterPingList.ItemsSource   = _pingItems;

            // Sugestões legadas são carregadas desabilitadas. Endereços de servidores
            // mudam e não existe uma lista pública estável que permita ativação cega.
            foreach (var (cidr, desc) in NetworkBinder.DefaultGameRoutes)
                _customRoutes.Add(new RouteItem
                {
                    Cidr = cidr,
                    Desc = desc,
                    Enabled = false,
                    Source = "Sugestão legada — validar"
                });

            // Timer de tráfego de rede
            _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _netTimer.Tick += NetTimer_Tick;

            // Timer para atualizar "Visto em" na aba Jogos
            var gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            gameTimer.Tick += (_, _) => { foreach (var vm in _gameExeVMs) vm.Refresh(); };
            gameTimer.Start();

            _netChangeHandler = (s, a) =>
            {
                try
                {
                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshAdapters));
                }
                catch { }
            };
            NetworkChange.NetworkAddressChanged += _netChangeHandler;

            SetupTrayIcon();
            this.Loaded += OnLoaded;

            if (App.StartHidden) { WindowState = WindowState.Minimized; ShowInTaskbar = false; }
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            PopulateRouteList();
            RefreshAdapters();
            UpdateResponsiveLayout(ActualWidth, ActualHeight, animateNavigation: false);
            _responsiveLayoutInitialized = true;
            _netTimer!.Start();

            if (NetworkBinder.HasPendingRecovery)
            {
                SetUiState(BinderState.RollingBack, "Restaurando uma transação anterior...");
                Log("[RECUPERAÇÃO] Uma transação incompleta foi encontrada na inicialização.");
                var recovery = await JournalRecovery.RestorePendingAsync(Log, CancellationToken.None);
                SetUiState(recovery.Success ? BinderState.Inactive : BinderState.Faulted, recovery.Message);
                if (!recovery.Success)
                {
                    MessageBox.Show(
                        recovery.Message,
                        "Recuperação incompleta",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            if (ChkAutoActivate.IsChecked == true)
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => BtnActivate_Click(null!, null!)));
        }

        // ── Rotas no Painel Principal ──────────────────────────────────────────
        void PopulateRouteList()
        {
            RouteListPanel.Children.Clear();
            var surface = (Brush)FindResource("SurfaceElevatedBrush");
            var borderBrush = (Brush)FindResource("BorderBrush");
            var primary = (Brush)FindResource("TextPrimaryBrush");
            var secondary = (Brush)FindResource("TextSecondaryBrush");
            var success = (Brush)FindResource("SuccessBrush");
            var muted = (Brush)FindResource("TextMutedBrush");

            foreach (var r in _customRoutes)
            {
                var row = new Border
                {
                    Background = surface,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(11, 9, 11, 9),
                    Margin = new Thickness(0, 0, 0, 8),
                    Opacity = r.Enabled ? 1.0 : 0.56
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(138) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                grid.Children.Add(new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = r.Enabled ? success : muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                });

                var cidr = new TextBlock
                {
                    Text = r.Cidr,
                    FontSize = 10.8,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    Foreground = primary,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(cidr, 1);
                grid.Children.Add(cidr);

                var description = new TextBlock
                {
                    Text = r.Desc,
                    FontSize = 10.8,
                    Foreground = secondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(description, 2);
                grid.Children.Add(description);

                row.Child = grid;
                RouteListPanel.Children.Add(row);
            }

            if (_customRoutes.Count == 0)
            {
                RouteListPanel.Children.Add(new TextBlock
                {
                    Text = "Nenhum destino configurado.",
                    Foreground = secondary,
                    FontSize = 11.5,
                    Margin = new Thickness(2, 8, 0, 8)
                });
            }
        }

        // ── Adapters ───────────────────────────────────────────────────────────
        void RefreshAdapters()
        {
            var previousWeakId = (CbWeak.SelectedItem as AdapterInfo)?.Id ?? _savedWeakAdapterId;
            var previousStrongId = (CbStrong.SelectedItem as AdapterInfo)?.Id ?? _savedStrongAdapterId;
            _adapters = NetworkBinder.GetAdapters();

            var ipToNic = new Dictionary<string, NetworkInterface>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        ipToNic[ua.Address.ToString()] = ni;
            }

            CbWeak.DisplayMemberPath = nameof(AdapterInfo.DisplayName);
            CbStrong.DisplayMemberPath = nameof(AdapterInfo.DisplayName);
            CbWeak.ItemsSource = _adapters;
            CbStrong.ItemsSource = _adapters;
            var strongIndex = previousStrongId == null
                ? 0
                : _adapters.FindIndex(a => a.Id.Equals(previousStrongId, StringComparison.OrdinalIgnoreCase));
            if (strongIndex < 0) strongIndex = 0;

            var weakIndex = previousWeakId == null
                ? (_adapters.Count >= 2 ? 1 : 0)
                : _adapters.FindIndex(a => a.Id.Equals(previousWeakId, StringComparison.OrdinalIgnoreCase));
            if (weakIndex < 0) weakIndex = _adapters.Count >= 2 ? 1 : 0;
            if (_adapters.Count >= 2 && weakIndex == strongIndex)
                weakIndex = strongIndex == 0 ? 1 : 0;

            CbStrong.SelectedIndex = _adapters.Count == 0 ? -1 : strongIndex;
            CbWeak.SelectedIndex = _adapters.Count == 0 ? -1 : weakIndex;

            _trafficItems.Clear(); _pingItems.Clear();

            foreach (var a in _adapters)
            {
                ipToNic.TryGetValue(a.Ip, out var ni);
                long spd = ni != null ? Math.Max(0, ni.Speed) : 0;
                var item = new AdapterTrafficItem
                {
                    AdapterName = a.Name, Ip = a.Ip,
                    NicId = ni?.Id ?? "", SpeedBps = spd,
                    DownSpeed = "0 bps", UpSpeed = "0 bps",
                    LastSampleUtc = DateTime.UtcNow
                };

                if (ni != null)
                {
                    try
                    {
                        var stats = ni.GetIPStatistics();
                        item.LastRecv = stats.BytesReceived;
                        item.LastSent = stats.BytesSent;
                    }
                    catch { }
                }

                _trafficItems.Add(item);
                _pingItems.Add(new AdapterPingItem { AdapterName = a.Name, Ip = a.Ip });
            }
            if (CbInspectAdapter!=null) { int prev=CbInspectAdapter.SelectedIndex; CbInspectAdapter.DisplayMemberPath=nameof(AdapterInfo.DisplayName); CbInspectAdapter.ItemsSource=_adapters; CbInspectAdapter.SelectedIndex=prev>=0&&prev<_adapters.Count?prev:0; FillAdapterStats(CbInspectAdapter.SelectedIndex,ipToNic); }
            Log($"[NET] {_adapters.Count} adaptador(es) detectado(s)");
        }

        void FillAdapterStats(int idx, Dictionary<string, NetworkInterface> ipToNic)
        {
            if (idx<0||idx>=_adapters.Count) return;
            var a=_adapters[idx]; if (!ipToNic.TryGetValue(a.Ip,out var ni)) return;
            _inspectedNic=ni;
            var mac=ni.GetPhysicalAddress().GetAddressBytes();
            LblMacAddress.Text="Endereço MAC: "+(mac.Length>0?string.Join(":",BitConverter.ToString(mac).Split('-')):"--");
            LblIpAddress.Text="Endereço IPv4: "+a.Ip;
            var dns=ni.GetIPProperties().DnsAddresses.Where(d=>d.AddressFamily==AddressFamily.InterNetwork).Select(d=>d.ToString()).ToList();
            LblDns.Text="Servidores DNS: "+(dns.Count>0?string.Join(", ",dns):"Nenhum");
            LblSpeed.Text=ni.Speed>0?$"Velocidade da Porta: {ni.Speed/1_000_000} Mbps":"Velocidade da Porta: Desconhecida";
        }

        // ── Net Timer ─────────────────────────────────────────────────────────
        // Usa os contadores cumulativos nativos de cada interface. O delta é
        // calculado separadamente por adaptador e cobre o tráfego total observado.

        void NetTimer_Tick(object? sender, EventArgs e)
        {
            long totBitsDown = 0, totBitsUp = 0;
            foreach (var item in _trafficItems)
                UpdateAdapterStats(item, ref totBitsDown, ref totBitsUp);

            LblDownloadSpeed.Text = FmtBitSpeed(totBitsDown);
            LblUploadSpeed.Text   = FmtBitSpeed(totBitsUp);
        }

        void UpdateAdapterStats(AdapterTrafficItem item, ref long totDown, ref long totUp)
        {
            var now = DateTime.UtcNow;
            double seconds = Math.Max(0.1, (now - item.LastSampleUtc).TotalSeconds);
            item.LastSampleUtc = now;

            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Id.Equals(item.NicId, StringComparison.OrdinalIgnoreCase))
                    ?? NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses
                            .Any(u => u.Address.ToString() == item.Ip));

                if (ni == null || ni.OperationalStatus != OperationalStatus.Up)
                {
                    item.DownSpeed = "offline";
                    item.UpSpeed = "offline";
                    item.BarWidth = 0;
                    item.UsagePercent = "--";
                    return;
                }

                var stats = ni.GetIPStatistics();
                long rawRecv = stats.BytesReceived;
                long rawSent = stats.BytesSent;
                long deltaRecv = item.LastRecv == 0 ? 0 : Math.Max(0, rawRecv - item.LastRecv);
                long deltaSent = item.LastSent == 0 ? 0 : Math.Max(0, rawSent - item.LastSent);
                item.LastRecv = rawRecv;
                item.LastSent = rawSent;

                long bitsDown = (long)(deltaRecv * 8d / seconds);
                long bitsUp = (long)(deltaSent * 8d / seconds);
                item.DownSpeed = FmtBitSpeed(bitsDown);
                item.UpSpeed = FmtBitSpeed(bitsUp);

                if (item.SpeedBps > 0)
                {
                    double pct = Math.Min(1.0, (bitsDown + bitsUp) / (double)item.SpeedBps);
                    item.BarWidth = pct * 180;
                    item.UsagePercent = $"{pct * 100:F1}%";
                }
                else
                {
                    item.BarWidth = 0;
                    item.UsagePercent = "--";
                }

                totDown += bitsDown;
                totUp += bitsUp;
            }
            catch
            {
                item.DownSpeed = "erro";
                item.UpSpeed = "erro";
            }
        }

        /// <summary>Formata bits/s em unidade legível.</summary>
        static string FmtBitSpeed(long bitsPerSec)
        {
            if (bitsPerSec < 0)             return "0 bps";
            if (bitsPerSec < 1_000)         return $"{bitsPerSec} bps";
            if (bitsPerSec < 1_000_000)     return $"{bitsPerSec / 1_000.0:F1} Kbps";
            if (bitsPerSec < 1_000_000_000) return $"{bitsPerSec / 1_000_000.0:F1} Mbps";
            return $"{bitsPerSec / 1_000_000_000.0:F2} Gbps";
        }

        // ── Ping ─────────────────────────────────────────────────────────────
        void BtnStartPing_Click(object sender, RoutedEventArgs e)
        {
            if (_pinging) { _pingTimer?.Stop(); _pinging=false; BtnStartPing.Content="Iniciar teste"; return; }
            _pinging=true; BtnStartPing.Content="Parar teste";
            _pingTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};
            _pingTimer.Tick+=PingTimer_Tick; _pingTimer.Start(); PingTimer_Tick(null,EventArgs.Empty);
        }

        async void PingTimer_Tick(object? sender, EventArgs e)
        {
            if (_pingTickRunning || _pingItems.Count == 0) return;
            _pingTickRunning = true;
            try
            {
                var host = TxtPingHost.Text.Trim();
                if (string.IsNullOrWhiteSpace(host)) host = "1.1.1.1";
                await Task.WhenAll(_pingItems.Select(i => PingOne(i, host)));
                var first = _pingItems[0];
                LblPingCurrent.Text = first.PingText;
                LblPingCurrent.Foreground = first.PingColor;
                if (first.History.Count > 0)
                {
                    LblPingAvg.Text = $"{first.History.Average():F1} ms";
                    if (first.History.Count > 1)
                    {
                        var diffs = Enumerable.Range(1, first.History.Count - 1)
                            .Select(i => (double)Math.Abs(first.History[i] - first.History[i - 1]));
                        LblPingJitter.Text = $"{diffs.Average():F1} ms";
                    }
                }
                LblPingLoss.Text = first.Sent > 0 ? $"{first.Lost * 100.0 / first.Sent:F1}% ({first.Lost}/{first.Sent})" : "--";
            }
            finally { _pingTickRunning = false; }
        }

        async Task PingOne(AdapterPingItem item, string host)
        {
            item.Sent++;
            var result = await BoundPing.SendAsync(item.Ip, host, 2000);
            if (result.Success)
            {
                long ms = result.RoundtripMilliseconds;
                item.History.Add(ms);
                if (item.History.Count > 50) item.History.RemoveAt(0);
                double jitter = item.History.Count > 1
                    ? Enumerable.Range(1, item.History.Count - 1).Select(i => (double)Math.Abs(item.History[i] - item.History[i - 1])).Average()
                    : 0;
                var color = ms < 50 ? Color.FromRgb(0x10,0xB9,0x81) : ms < 120 ? Color.FromRgb(0xF5,0xB0,0x14) : Color.FromRgb(0xEF,0x44,0x44);
                Dispatcher.Invoke(() => { item.PingText = $"{ms} ms"; item.JitterText = $"{jitter:F1} ms"; item.PingColor = new SolidColorBrush(color); });
            }
            else
            {
                item.Lost++;
                Dispatcher.Invoke(() => { item.PingText = result.Error.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ? "Timeout" : "Sem rota"; item.PingColor = new SolidColorBrush(Color.FromRgb(0xEF,0x44,0x44)); });
            }
        }

        // ── Rotas ─────────────────────────────────────────────────────────────
        bool CanEditRoutes()
        {
            var state = _binder?.State ?? BinderState.Inactive;
            if (!_active && state != BinderState.Preparing && state != BinderState.RollingBack) return true;
            MessageBox.Show(
                "Desative o Binder antes de alterar rotas. Isso mantém o diário transacional igual ao estado realmente aplicado.",
                "Rotas bloqueadas durante a ativação",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        void BtnAddCustomRoute_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditRoutes()) return;
            var input=TxtNewRouteIp.Text.Trim();
            if(!CidrUtility.TryNormalizeIPv4(input,out var cidr,out var error) || CidrUtility.IsUnsafeDestination(cidr,out error)){MessageBox.Show(error,"Rota inválida",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            if(_customRoutes.Any(r=>r.Cidr.Equals(cidr,StringComparison.OrdinalIgnoreCase))){MessageBox.Show("Essa rota já existe.","Atenção");return;}
            var desc=string.IsNullOrWhiteSpace(TxtNewRouteDesc.Text)?"Rota personalizada":TxtNewRouteDesc.Text.Trim();
            _customRoutes.Add(new RouteItem{Cidr=cidr,Desc=desc,Source="Manual"}); PopulateRouteList(); TxtNewRouteIp.Clear(); TxtNewRouteDesc.Clear(); SaveRoutes(); Log($"[ROTAS] Adicionada: {cidr}");
        }

        void BtnRemoveCustomRoute_Click(object sender, RoutedEventArgs e)
        { if (!CanEditRoutes()) return; if(sender is Button btn && btn.DataContext is RouteItem item){_customRoutes.Remove(item);PopulateRouteList();SaveRoutes();Log($"[ROTAS] Removida: {item.Cidr}");} }

        void BtnBulkImport_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditRoutes()) return;
            var dlg=new BulkImportDialog{Owner=this}; if(dlg.ShowDialog()!=true) return;
            int added=0,skip=0;
            foreach(var raw in dlg.InputText.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){var line=raw.Trim();if(string.IsNullOrEmpty(line)||line.StartsWith("#"))continue;var parts=line.Split(new[]{' ','\t'},2,StringSplitOptions.RemoveEmptyEntries);if(!CidrUtility.TryNormalizeIPv4(parts[0],out var cidr,out _)||CidrUtility.IsUnsafeDestination(cidr,out _)){skip++;continue;}var desc=parts.Length>1?parts[1].Trim():"Rota personalizada";if(_customRoutes.Any(r=>r.Cidr.Equals(cidr,StringComparison.OrdinalIgnoreCase))){skip++;continue;}_customRoutes.Add(new RouteItem{Cidr=cidr,Desc=desc,Source="Import"});added++;}
            PopulateRouteList();SaveRoutes();Log($"[ROTAS] Import: {added} adicionada(s), {skip} ignorada(s).");
            MessageBox.Show($"{added} adicionada(s).\n{skip} ignorada(s).","Import",MessageBoxButton.OK,MessageBoxImage.Information);
        }

        void RouteEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: RouteItem route } checkBox) return;
            if (!CanEditRoutes())
            {
                route.Enabled = !route.Enabled;
                checkBox.IsChecked = route.Enabled;
                return;
            }
            route.Enabled = checkBox.IsChecked == true;
            PopulateRouteList();
            SaveRoutes();
            Log($"[ROTAS] {route.Cidr} → {(route.Enabled ? "habilitada" : "desabilitada")}");
        }

        void SaveRoutes()
        {
            try{var conf=ReadConfigFile();conf.CustomRoutes=new List<RouteItem>(_customRoutes);conf.WeakAdapterId=(CbWeak.SelectedItem as AdapterInfo)?.Id??conf.WeakAdapterId;conf.StrongAdapterId=(CbStrong.SelectedItem as AdapterInfo)?.Id??conf.StrongAdapterId;WriteConfigFile(conf);}
            catch(Exception ex){Log($"[ERRO] {ex.Message}");}
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ABA JOGOS — gerenciamento de executáveis
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Abre um FolderBrowserDialog, varre o diretório recursivamente por .exe,
        /// exibe a lista para o usuário selecionar quais adicionar.
        /// </summary>
        void BtnSelectGameFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Selecione a pasta do jogo (ex: C:\\Riot Games\\VALORANT)",
                ShowNewFolderButton = false
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var folder = dlg.SelectedPath;
            Log($"[JOGOS] Varrendo pasta: {folder}");

            Task.Run(() =>
            {
                List<string> found;
                try { found = Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).ToList(); }
                catch (Exception ex) { Dispatcher.Invoke(() => MessageBox.Show($"Erro ao varrer pasta:\n{ex.Message}", "Erro")); return; }

                if (found.Count == 0)
                {
                    Dispatcher.Invoke(() => MessageBox.Show("Nenhum .exe encontrado na pasta selecionada.", "Jogos"));
                    return;
                }

                // Abre diálogo de seleção de exes na UI thread
                Dispatcher.Invoke(() =>
                {
                    var selector = new ExeSelectorDialog(found, IOPath.GetFileName(folder)) { Owner = this };
                    if (selector.ShowDialog() != true || selector.SelectedPaths.Count == 0) return;

                    int added = 0;
                    foreach (var path in selector.SelectedPaths)
                    {
                        if (_gameExeVMs.Any(v => v.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                        var name   = IOPath.GetFileNameWithoutExtension(path);
                        var item   = new GameExeItem { Path = path, Name = name, Enabled = true, BindingMode = ApplicationBindingMode.GameRoutesSafe, PreferredAdapter = AdapterPreference.Strong };
                        var vm     = new GameExeViewModel(item);
                        _gameExeVMs.Add(vm);
                        added++;
                    }
                    if (added > 0)
                    {
                        SaveGameExes();
                        Log($"[JOGOS] {added} executável(is) adicionado(s).");
                        // Se o binder estiver ativo, atualiza as regras de firewall imediatamente
                        if (_active && _binder != null)
                            _ = RefreshActiveFirewallRulesAsync();
                    }
                    else
                        Log("[JOGOS] Nenhum exe novo selecionado (todos já estavam na lista).");
                });
            });
        }

        /// <summary>
        /// Adiciona um único .exe via diálogo de arquivo.
        /// </summary>
        void BtnAddExeManual_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Selecione o executável do jogo",
                Filter = "Executáveis (*.exe)|*.exe",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;

            int added = 0;
            foreach (var path in dlg.FileNames)
            {
                if (_gameExeVMs.Any(v => v.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                var name = IOPath.GetFileNameWithoutExtension(path);
                var item = new GameExeItem { Path = path, Name = name, Enabled = true, BindingMode = ApplicationBindingMode.GameRoutesSafe, PreferredAdapter = AdapterPreference.Strong };
                _gameExeVMs.Add(new GameExeViewModel(item));
                added++;
            }
            if (added > 0)
            {
                SaveGameExes();
                Log($"[JOGOS] {added} exe(s) adicionado(s) manualmente.");
                if (_active && _binder != null) _ = RefreshActiveFirewallRulesAsync();
            }
        }

        /// <summary>
        /// Remove o exe selecionado no DataGrid.
        /// </summary>
        void BtnRemoveExe_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is GameExeViewModel vm)
            {
                _gameExeVMs.Remove(vm);
                SaveGameExes();
                Log($"[JOGOS] Removido: {vm.Name}");
                if (_active && _binder != null) _ = RefreshActiveFirewallRulesAsync();
            }
        }

        /// <summary>
        /// Toggle de habilitado/desabilitado para um exe.
        /// </summary>
        void BtnToggleExe_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is GameExeViewModel vm)
            {
                vm.Enabled = !vm.Enabled;
                btn.Content = vm.Enabled ? "✓" : "○";
                SaveGameExes();
                Log($"[JOGOS] {vm.Name} → {(vm.Enabled ? "habilitado" : "desabilitado")}");
                if (_active && _binder != null) _ = RefreshActiveFirewallRulesAsync();
            }
        }

        /// <summary>
        /// Analisa compatibilidade de rota para o exe selecionado.
        /// Limpa e repopula AnalysisContentPanel — nunca substitui elementos do XAML.
        /// </summary>
        void BtnAnalyzeExe_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn && btn.DataContext is GameExeViewModel vm)) return;

            // Mostra painel e indicador de carregamento
            AnalysisBorder.Visibility        = Visibility.Visible;
            AnalysisLoadingDot.Visibility     = Visibility.Visible;
            StartAnalysisPulse();
            LblAnalysisExe.Text               = $"Analisando: {vm.Name}...";
            AnalysisContentPanel.Children.Clear();
            AnalysisContentPanel.Children.Add(new TextBlock
            {
                Text       = "Consultando tabela de rotas e conexões...",
                FontSize   = 12,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                Margin     = new Thickness(0, 2, 0, 2),
            });

            Task.Run(() =>
            {
                var result = NetworkBinder.AnalyzeRouteCompatibility(
                    vm.Path, _customRoutes.ToList());

                Dispatcher.Invoke(() =>
                {
                    // Para o indicador
                    StopAnalysisPulse();
                    AnalysisLoadingDot.Visibility = Visibility.Collapsed;
                    LblAnalysisExe.Text = $"Análise — {vm.Name}";

                    // Cores por status
                    var summaryColor = result.Status switch
                    {
                        CompatibilityStatus.Compatible => Color.FromRgb(0x05,0x96,0x69),
                        CompatibilityStatus.NotRunning => Color.FromRgb(0xD9,0x77,0x06),
                        _                              => Color.FromRgb(0xDC,0x26,0x26),
                    };

                    // Limpa e reconstrói — mesmo painel, sempre o mesmo objeto
                    AnalysisContentPanel.Children.Clear();

                    // ── Linhas do Summary (pode ter \n) ───────────────────
                    foreach (var sLine in result.Summary.Split('\n'))
                    {
                        AnalysisContentPanel.Children.Add(new TextBlock
                        {
                            Text         = sLine,
                            FontSize     = 12,
                            FontWeight   = FontWeights.SemiBold,
                            Foreground   = new SolidColorBrush(summaryColor),
                            TextWrapping = TextWrapping.Wrap,
                            Margin       = new Thickness(0, 0, 0, 2),
                            FontFamily   = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                        });
                    }

                    if (result.Details.Count == 0) return;

                    // Separador
                    AnalysisContentPanel.Children.Add(new Border
                    {
                        Background = (Brush)FindResource("BorderBrush"),
                        Height     = 1,
                        Margin     = new Thickness(0, 8, 0, 8),
                    });

                    // ── Linhas de detalhe com cor por prefixo ─────────────
                    foreach (var line in result.Details)
                    {
                        // Linha vazia → pequeno espaço
                        if (string.IsNullOrEmpty(line))
                        {
                            AnalysisContentPanel.Children.Add(
                                new Border { Height = 4 });
                            continue;
                        }

                        var fg = line.StartsWith("  ✓") ? Color.FromRgb(0x05,0x96,0x69)
                               : line.StartsWith("  ✗") ? Color.FromRgb(0xDC,0x26,0x26)
                               : line.StartsWith("──")  ? Color.FromRgb(0x1E,0x40,0xAF)
                               : line.StartsWith("  [") ? Color.FromRgb(0x1D,0x4E,0xD8)
                               : _isDarkTheme ? Color.FromRgb(0xA7,0xB3,0xC7) : Color.FromRgb(0x33,0x41,0x55);

                        var fw = line.StartsWith("──")
                            ? FontWeights.Bold
                            : FontWeights.Normal;

                        AnalysisContentPanel.Children.Add(new TextBlock
                        {
                            Text         = line,
                            FontSize     = 11,
                            FontWeight   = fw,
                            Foreground   = new SolidColorBrush(fg),
                            TextWrapping = TextWrapping.Wrap,
                            Margin       = new Thickness(0, 1, 0, 1),
                            FontFamily   = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
                        });
                    }

                    // Rola para o topo após atualizar
                    AnalysisScroller.ScrollToTop();
                });
            });
        }

        void StartAnalysisPulse()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                AnalysisLoadingDot.Opacity = 1;
                return;
            }

            AnalysisLoadingDot.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0.32, 1.0, new Duration(TimeSpan.FromMilliseconds(520)))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
        }

        void StopAnalysisPulse()
        {
            AnalysisLoadingDot.BeginAnimation(UIElement.OpacityProperty, null);
            AnalysisLoadingDot.Opacity = 1;
        }

        void BtnCycleBindingMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: GameExeViewModel vm }) return;
            vm.BindingMode = vm.BindingMode switch
            {
                ApplicationBindingMode.GameRoutesSafe => ApplicationBindingMode.ProxyCompatible,
                ApplicationBindingMode.ProxyCompatible => ApplicationBindingMode.ObserveOnly,
                _ => ApplicationBindingMode.GameRoutesSafe
            };
            if (vm.BindingMode == ApplicationBindingMode.GameRoutesSafe)
                vm.PreferredAdapter = AdapterPreference.Strong;
            vm.Refresh();
            SaveGameExes();
            if (_active && _binder != null) _ = RefreshActiveFirewallRulesAsync();
        }

        void BtnCyclePreferredAdapter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: GameExeViewModel vm }) return;
            if (vm.BindingMode == ApplicationBindingMode.GameRoutesSafe)
            {
                vm.PreferredAdapter = AdapterPreference.Strong;
                MessageBox.Show(
                    "O modo Rotas seguras usa o adaptador forte para os destinos configurados. Um vínculo por processo ao adaptador padrão exigiria interceptação e não é usado no modo conservador.",
                    "Saída fixa no modo seguro",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            vm.PreferredAdapter = vm.PreferredAdapter == AdapterPreference.Strong ? AdapterPreference.Weak : AdapterPreference.Strong;
            SaveGameExes();
        }

        async void BtnLaunchExe_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: GameExeViewModel vm }) return;
            if (vm.BindingMode != ApplicationBindingMode.ProxyCompatible)
            {
                MessageBox.Show("Para iniciar um aplicativo por adaptador, altere o modo para 'Proxy TCP'. Jogos UDP devem permanecer em 'Rotas seguras'.", "Modo de vínculo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ApplicationSafety.IsKnownProxyClient(vm.Path))
            {
                var confirmation = MessageBox.Show(
                    "Este aplicativo não está na lista de clientes de proxy conhecidos. Continue somente se ele aceitar SOCKS5/ALL_PROXY e não for um jogo ou componente de anti-cheat. Deseja iniciar mesmo assim?",
                    "Aplicativo não reconhecido",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes) return;
            }

            var adapter = vm.PreferredAdapter == AdapterPreference.Strong
                ? CbStrong.SelectedItem as AdapterInfo
                : CbWeak.SelectedItem as AdapterInfo;
            if (adapter == null) { MessageBox.Show("Selecione os adaptadores primeiro."); return; }
            var result = await _proxyBindingManager.LaunchAsync(vm.Model, adapter);
            Log($"[PROXY] {vm.Name}: {result.Message}");
            if (!result.Success) MessageBox.Show(result.Message, "Falha ao iniciar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        void SaveGameExes()
        {
            try
            {
                var conf = ReadConfigFile();
                conf.GameExes = _gameExeVMs.Select(v => v.Model).ToList();
                conf.WeakAdapterId = (CbWeak.SelectedItem as AdapterInfo)?.Id ?? conf.WeakAdapterId;
                conf.StrongAdapterId = (CbStrong.SelectedItem as AdapterInfo)?.Id ?? conf.StrongAdapterId;
                WriteConfigFile(conf);
            }
            catch (Exception ex) { Log($"[ERRO] SaveGameExes: {ex.Message}"); }
        }

        async Task RefreshActiveFirewallRulesAsync()
        {
            var binder = _binder;
            if (!_active || binder == null) return;
            try
            {
                var snapshot = _gameExeVMs.Select(vm => vm.Model).ToList();
                var result = await binder.RefreshFirewallRulesAsync(snapshot, CancellationToken.None);
                if (!result.Success) Log("[PROTEÇÃO] " + result.Message);
            }
            catch (Exception ex)
            {
                Log("[PROTEÇÃO] Falha inesperada ao atualizar executáveis: " + ex.Message);
            }
        }

        // ── Ativar / Desativar ─────────────────────────────────────────────────
        async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            if (_active || _binder?.State == BinderState.Preparing) return;
            if (CbWeak.SelectedItem is not AdapterInfo weak || CbStrong.SelectedItem is not AdapterInfo strong)
            { MessageBox.Show("Selecione os dois adaptadores.", "Atenção"); return; }
            if (weak.Id == strong.Id)
            { MessageBox.Show("Selecione adaptadores diferentes.", "Atenção"); return; }
            if (!_customRoutes.Any(r => r.Enabled))
            {
                MessageBox.Show(
                    "Ative ao menos uma rota IPv4/CIDR validada na aba Rotas. As sugestões legadas ficam desabilitadas por segurança.",
                    "Nenhuma rota ativa",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var currentConfig = ReadConfigFile();
            currentConfig.WeakAdapterId = weak.Id;
            currentConfig.StrongAdapterId = strong.Id;
            currentConfig.CustomRoutes = new List<RouteItem>(_customRoutes);
            currentConfig.GameExes = _gameExeVMs.Select(v => v.Model).ToList();
            WriteConfigFile(currentConfig);

            _binder?.Dispose();
            _binder = new NetworkBinder(weak, strong, new List<RouteItem>(_customRoutes), _gameExeVMs.Select(v => v.Model).ToList(), Log, OnMonitor);
            _binder.StateChanged += Binder_StateChanged;
            SetUiState(BinderState.Preparing, "Preparando e verificando...");
            var result = await _binder.ActivateAsync();
            if (!result.Success)
            {
                SetUiState(BinderState.Faulted, result.Message);
                MessageBox.Show(result.Message + (result.Details.Count > 0 ? "\n\n" + string.Join("\n", result.Details.Take(6)) : ""), "Falha ao ativar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void Binder_StateChanged(BinderState state, string message) => Dispatcher.Invoke(() => SetUiState(state, message));

        async void BtnDeactivate_Click(object sender, RoutedEventArgs e)
        {
            var result = _binder != null
                ? await _binder.DeactivateAsync()
                : await JournalRecovery.RestorePendingAsync(Log, CancellationToken.None);
            SetUiState(result.Success ? BinderState.Inactive : BinderState.Faulted, result.Message);
            if (!result.Success)
                MessageBox.Show(result.Message, "Restauração incompleta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        void SetUiActive(bool active) => SetUiState(active ? BinderState.Active : BinderState.Inactive, active ? "Ativo" : "Inativo");

        void SetUiState(BinderState state, string detail)
        {
            _currentBinderState = state;
            _currentBinderDetail = detail;
            _active = state is BinderState.Active or BinderState.Degraded;
            var busy = state is BinderState.Preparing or BinderState.RollingBack;
            BtnActivate.IsEnabled = !_active && !busy;
            BtnDeactivate.IsEnabled = (_active || state == BinderState.Faulted) && !busy;
            CbWeak.IsEnabled = !_active && !busy;
            CbStrong.IsEnabled = !_active && !busy;
            ApplyStatusVisuals(state, detail);
            if (!_active && !busy) LblMonitor.Text = "Monitor: inativo";
        }

        void ApplyStatusVisuals(BinderState state, string detail)
        {
            StatusText.Text = state switch
            {
                BinderState.Active => "ATIVO E VERIFICADO",
                BinderState.Preparing => "PREPARANDO",
                BinderState.Degraded => "PROTEÇÃO PARCIAL",
                BinderState.RollingBack => "RESTAURANDO",
                BinderState.Faulted => "AÇÃO NECESSÁRIA",
                _ => "INATIVO"
            };

            var color = state switch
            {
                BinderState.Active => Color.FromRgb(0x35, 0xD0, 0x7F),
                BinderState.Preparing or BinderState.RollingBack => Color.FromRgb(0x6E, 0x8C, 0xFF),
                BinderState.Degraded => Color.FromRgb(0xF6, 0xC8, 0x5F),
                BinderState.Faulted => Color.FromRgb(0xFF, 0x64, 0x7C),
                _ => _isDarkTheme ? Color.FromRgb(0xA7, 0xB3, 0xC7) : Color.FromRgb(0x66, 0x70, 0x85)
            };

            var background = state switch
            {
                BinderState.Active => _isDarkTheme ? Color.FromRgb(0x12, 0x37, 0x28) : Color.FromRgb(0xEC, 0xFD, 0xF3),
                BinderState.Preparing or BinderState.RollingBack => _isDarkTheme ? Color.FromRgb(0x1A, 0x26, 0x4B) : Color.FromRgb(0xEF, 0xF4, 0xFF),
                BinderState.Degraded => _isDarkTheme ? Color.FromRgb(0x3A, 0x2E, 0x14) : Color.FromRgb(0xFF, 0xFA, 0xEB),
                BinderState.Faulted => _isDarkTheme ? Color.FromRgb(0x3C, 0x1D, 0x28) : Color.FromRgb(0xFF, 0xF1, 0xF3),
                _ => _isDarkTheme ? Color.FromRgb(0x12, 0x1A, 0x2B) : Color.FromRgb(0xFF, 0xFF, 0xFF)
            };

            var statusBrush = new SolidColorBrush(color);
            StatusDot.Fill = statusBrush;
            StatusText.Foreground = statusBrush;
            StatusLabel.Foreground = statusBrush;
            StatusCard.Background = new SolidColorBrush(background);
            StatusCard.BorderBrush = statusBrush;
            StatusCard.ToolTip = detail;
        }

        void OnMonitor(bool leak, int count) => Dispatcher.Invoke(() => { LblMonitor.Text = leak ? $"⚠ Discord: {count} conexão(ões) TCP IPv4 no forte" : "✓ Nenhum vazamento TCP IPv4 detectado (UDP/IPv6 não inferidos)"; LblMonitor.Foreground = new SolidColorBrush(leak ? Color.FromRgb(0xEF,0x44,0x44) : Color.FromRgb(0x10,0xB9,0x81)); });

        // ── Kit de Socorro ─────────────────────────────────────────────────────

        async void BtnEmergencyReset_Click(object sender, RoutedEventArgs e)
        {
            const string msg1 = "A recuperação segura vai ler o diário do Nexus e restaurar somente as métricas, rotas e regras registradas pela última transação.";
            const string msg2 = "Rotas de VPN, WSL, Docker e configurações manuais não serão removidas.";
            const string msg3 = "Deseja continuar?";
            var confirm = MessageBox.Show(
                msg1 + "\n\n" + msg2 + "\n\n" + msg3,
                "Recuperação Segura",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            BtnEmergencyReset.IsEnabled = false;
            BtnEmergencyReset.Content = "Restaurando...";
            SetUiState(BinderState.RollingBack, "Executando recuperação segura...");
            Log("[RESET] Iniciando recuperação segura...");

            try
            {
                if (_binder != null && _binder.State is BinderState.Active or BinderState.Degraded or BinderState.Faulted)
                    await _binder.DeactivateAsync();

                var resetLines = await Task.Run(EmergencyReset.Run);
                foreach (var line in resetLines) Log(line);
                var pending = NetworkBinder.HasPendingRecovery;
                SetUiState(pending ? BinderState.Faulted : BinderState.Inactive,
                    pending ? "A restauração ficou incompleta." : "Recuperação concluída.");

                MessageBox.Show(
                    pending
                        ? "Parte da recuperação falhou. O diário foi preservado para uma nova tentativa."
                        : "Recuperação concluída. Somente objetos pertencentes ao Nexus foram restaurados.",
                    "Recuperação Segura",
                    MessageBoxButton.OK,
                    pending ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log("[RESET] ✗ " + ex.Message);
                SetUiState(BinderState.Faulted, ex.Message);
                MessageBox.Show(ex.Message, "Falha na recuperação", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnEmergencyReset.IsEnabled = true;
                BtnEmergencyReset.Content = "Recuperação segura";
            }
        }

        async void BtnFlushDns_Click(object sender, RoutedEventArgs e)
        {
            var result = await RunToolAsync("ipconfig.exe", new[] { "/flushdns" }, TimeSpan.FromSeconds(15));
            Log(result.Success ? "[SOCORRO] Cache DNS limpo." : "[SOCORRO] Falha no flush DNS: " + result.Error);
            MessageBox.Show(
                result.Success ? "Cache DNS limpo!" : result.Error,
                "Kit de Socorro",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        async void BtnResetAdapter_Click(object sender, RoutedEventArgs e)
        {
            if (_active || (_binder != null && _binder.State != BinderState.Inactive) || NetworkBinder.HasPendingRecovery)
            {
                MessageBox.Show(
                    "Desative o Binder e conclua qualquer recuperação antes de reiniciar uma interface.",
                    "Operação bloqueada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            if (_inspectedNic == null) return;
            var name = _inspectedNic.Name;
            if (MessageBox.Show($"Reiniciar '{name}'?", "Aviso", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            Log($"[SOCORRO] Reiniciando '{name}'...");
            var disable = await RunToolAsync(
                "netsh.exe",
                new[] { "interface", "set", "interface", $"name={name}", "admin=disable" },
                TimeSpan.FromSeconds(20));
            if (!disable.Success)
            {
                Log("[SOCORRO] Falha ao desabilitar: " + disable.Error);
                MessageBox.Show(disable.Error, "Falha ao reiniciar", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await Task.Delay(2500);
            var enable = await RunToolAsync(
                "netsh.exe",
                new[] { "interface", "set", "interface", $"name={name}", "admin=enable" },
                TimeSpan.FromSeconds(20));
            RefreshAdapters();
            Log(enable.Success ? "[SOCORRO] Reset concluído." : "[SOCORRO] A interface foi desabilitada, mas não pôde ser reabilitada: " + enable.Error);
            MessageBox.Show(
                enable.Success ? $"'{name}' reiniciado." : enable.Error,
                "Kit de Socorro",
                MessageBoxButton.OK,
                enable.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }

        static async Task<(bool Success, string Error)> RunToolAsync(
            string fileName,
            IEnumerable<string> arguments,
            TimeSpan timeout)
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

            using var process = new Process { StartInfo = psi };
            try
            {
                if (!process.Start()) return (false, "Não foi possível iniciar " + fileName + ".");
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(timeout);
                try { await process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return (false, "A operação excedeu o tempo limite.");
                }

                var output = (await outputTask).Trim();
                var error = (await errorTask).Trim();
                return process.ExitCode == 0
                    ? (true, "")
                    : (false, string.IsNullOrWhiteSpace(error) ? output : error);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ── Log ────────────────────────────────────────────────────────────────
        public void Log(string msg)
        {
            AppLogger.Write(msg);
            if (Dispatcher.CheckAccess())
            {
                LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
                LogScroller.ScrollToBottom();
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
                    LogScroller.ScrollToBottom();
                });
            }
        }
        public void AppendLog(string msg) => Log(msg);
        void BtnCopyLogs_Click(object sender, RoutedEventArgs e) { Clipboard.SetText(LogBox.Text); Log("[GUI] Logs copiados."); }

        // ── Layout responsivo e navbar ─────────────────────────────────────────
        bool _navExpanded = true;

        void FitWindowToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            MaxWidth = Math.Max(MinWidth, area.Width);
            MaxHeight = Math.Max(MinHeight, area.Height);

            // Mantém folga para bordas, barra de tarefas e escalas de 125%/150%.
            Width = Math.Min(1180, Math.Max(MinWidth, area.Width - 24));
            Height = Math.Min(720, Math.Max(MinHeight, area.Height - 24));
        }

        void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsInitialized || RouteEditorGrid == null || ApplicationsHeaderGrid == null) return;
            UpdateResponsiveLayout(e.NewSize.Width, e.NewSize.Height, animateNavigation: _responsiveLayoutInitialized);
        }

        void UpdateResponsiveLayout(double windowWidth, double windowHeight, bool animateNavigation)
        {
            var compactShell = windowWidth < 1240 || windowHeight < 700;

            if (compactShell && _navExpanded)
            {
                _navCollapsedByResponsiveLayout = true;
                SetNavigationExpanded(false, animateNavigation);
            }
            else if (!compactShell && _navCollapsedByResponsiveLayout)
            {
                _navCollapsedByResponsiveLayout = false;
                SetNavigationExpanded(true, animateNavigation);
            }

            var navigationWidth = _navExpanded ? 232d : 64d;
            var contentWidth = Math.Max(0, windowWidth - navigationWidth);
            var compactContent = contentWidth < 980 || windowHeight < 690;
            var narrowContent = contentWidth < 820;

            TopBarRow.Height = new GridLength(compactContent ? 68 : 74);
            TopBarLayout.Margin = compactContent ? new Thickness(16, 8, 16, 8) : new Thickness(20, 11, 20, 10);
            PageHost.Padding = compactContent ? new Thickness(14, 12, 14, 14) : new Thickness(18, 16, 18, 18);
            PageTitle.FontSize = compactContent ? 21 : 24;
            PageSubtitle.FontSize = compactContent ? 10.5 : 11.5;
            AdminPill.Visibility = contentWidth < 760 ? Visibility.Collapsed : Visibility.Visible;

            // Em larguras reduzidas, as ações do estado ficam abaixo da descrição.
            if (narrowContent)
            {
                Grid.SetRow(StatusActions, 1);
                Grid.SetColumn(StatusActions, 0);
                Grid.SetColumnSpan(StatusActions, 2);
                StatusActions.Margin = new Thickness(0, 16, 0, 0);
                StatusActions.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetRow(StatusActions, 0);
                Grid.SetColumn(StatusActions, 1);
                Grid.SetColumnSpan(StatusActions, 1);
                StatusActions.Margin = new Thickness(24, 0, 0, 0);
                StatusActions.HorizontalAlignment = HorizontalAlignment.Right;
            }

            // Editor de rotas: campos na primeira linha e botões na segunda quando necessário.
            if (contentWidth < 940)
            {
                Grid.SetRow(BtnAddRoute, 1);
                Grid.SetColumn(BtnAddRoute, 0);
                Grid.SetColumnSpan(BtnAddRoute, 1);
                BtnAddRoute.Margin = new Thickness(0, 12, 6, 0);
                BtnAddRoute.HorizontalAlignment = HorizontalAlignment.Stretch;

                Grid.SetRow(BtnImportRoutes, 1);
                Grid.SetColumn(BtnImportRoutes, 1);
                Grid.SetColumnSpan(BtnImportRoutes, 1);
                BtnImportRoutes.Margin = new Thickness(6, 12, 0, 0);
                BtnImportRoutes.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else
            {
                Grid.SetRow(BtnAddRoute, 0);
                Grid.SetColumn(BtnAddRoute, 2);
                Grid.SetColumnSpan(BtnAddRoute, 1);
                BtnAddRoute.Margin = new Thickness(0, 20, 10, 0);
                BtnAddRoute.HorizontalAlignment = HorizontalAlignment.Center;

                Grid.SetRow(BtnImportRoutes, 0);
                Grid.SetColumn(BtnImportRoutes, 3);
                Grid.SetColumnSpan(BtnImportRoutes, 1);
                BtnImportRoutes.Margin = new Thickness(0, 20, 0, 0);
                BtnImportRoutes.HorizontalAlignment = HorizontalAlignment.Center;
            }

            // Cabeçalho de aplicativos: evita que os botões comprimam o título.
            if (contentWidth < 940)
            {
                Grid.SetColumnSpan(ApplicationsHeaderText, 3);
                Grid.SetRow(BtnSelectFolder, 1);
                Grid.SetColumn(BtnSelectFolder, 0);
                BtnSelectFolder.HorizontalAlignment = HorizontalAlignment.Left;
                BtnSelectFolder.Margin = new Thickness(0, 14, 10, 0);

                Grid.SetRow(BtnAddExecutable, 1);
                Grid.SetColumn(BtnAddExecutable, 1);
                Grid.SetColumnSpan(BtnAddExecutable, 2);
                BtnAddExecutable.HorizontalAlignment = HorizontalAlignment.Left;
                BtnAddExecutable.Margin = new Thickness(0, 14, 0, 0);
            }
            else
            {
                Grid.SetColumnSpan(ApplicationsHeaderText, 1);
                Grid.SetRow(BtnSelectFolder, 0);
                Grid.SetColumn(BtnSelectFolder, 1);
                BtnSelectFolder.HorizontalAlignment = HorizontalAlignment.Center;
                BtnSelectFolder.Margin = new Thickness(12, 0, 10, 0);

                Grid.SetRow(BtnAddExecutable, 0);
                Grid.SetColumn(BtnAddExecutable, 2);
                Grid.SetColumnSpan(BtnAddExecutable, 1);
                BtnAddExecutable.HorizontalAlignment = HorizontalAlignment.Center;
                BtnAddExecutable.Margin = new Thickness(0);
            }

            // O painel de análise permanece disponível, mas ocupa menos espaço em telas compactas.
            ApplicationsDetailsColumn.Width = new GridLength(contentWidth < 1040 ? 286 : 330);
            ApplicationsDetailsPanel.Margin = new Thickness(contentWidth < 1040 ? 6 : 8, 0, 0, 0);
        }

        void BtnToggleNav_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _navCollapsedByResponsiveLayout = false;
            SetNavigationExpanded(!_navExpanded, animate: true);
        }

        void SetNavigationExpanded(bool expanded, bool animate)
        {
            if (_navExpanded == expanded && NavColumn.Width.Value == (expanded ? 232 : 64)) return;

            _navExpanded = expanded;
            var targetWidth = new GridLength(expanded ? 232 : 64);
            var targetOpacity = expanded ? 1.0 : 0.0;
            TxtMenuToggle.Text = expanded ? "Recolher menu" : "Expandir menu";

            var labels = new UIElement[]
            {
                TxtBrandName, TxtBrandSubtitle, TxtMenuToggle, TxtNavSection,
                TxtMenuPrincipal, TxtMenuRotas, TxtMenuInterfaces, TxtMenuJogos, TxtMenuConfig,
                NavFooterText, NavFooterVersion
            };

            if (!animate || !SystemParameters.ClientAreaAnimation)
            {
                NavColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
                NavColumn.Width = targetWidth;
                foreach (var label in labels)
                {
                    label.BeginAnimation(UIElement.OpacityProperty, null);
                    label.Opacity = targetOpacity;
                }
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            NavColumn.BeginAnimation(
                ColumnDefinition.WidthProperty,
                new GridLengthAnimation
                {
                    From = NavColumn.Width,
                    To = targetWidth,
                    Duration = new Duration(TimeSpan.FromMilliseconds(210)),
                    EasingFunction = ease
                });
            var opacityAnimation = new DoubleAnimation
            {
                To = targetOpacity,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = ease
            };
            foreach (var label in labels)
                label.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        }

        void Menu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            foreach (var item in new[] { MenuPrincipal, MenuRotas, MenuInterfaces, MenuJogos, MenuConfig })
                item.Background = Brushes.Transparent;

            if (sender is not Border selected) return;
            selected.SetResourceReference(Border.BackgroundProperty, "SelectedBrush");

            var (index, title, subtitle) = selected.Name switch
            {
                "MenuPrincipal" => (0, "Visão geral", "Controle suas conexões e acompanhe o estado do perfil ativo."),
                "MenuRotas" => (1, "Destinos de rede", "Gerencie somente os IPs e CIDRs validados para a conexão de jogos."),
                "MenuInterfaces" => (2, "Conexões", "Inspecione latência, perda, DNS e tráfego de cada adaptador."),
                "MenuJogos" => (3, "Aplicativos", "Escolha o modo adequado para jogos, navegadores e clientes compatíveis."),
                "MenuConfig" => (4, "Configurações", "Personalize a aparência, inicialização e comportamento do Nexus."),
                _ => (0, "Visão geral", "Controle suas conexões e acompanhe o estado do perfil ativo.")
            };

            MainTabControl.SelectedIndex = index;
            UpdateNavigationSelection(index);
            PageTitle.Text = title;
            PageSubtitle.Text = subtitle;
            AnimatePageTransition();
        }

        void UpdateNavigationSelection(int selectedIndex)
        {
            var items = new[] { MenuPrincipal, MenuRotas, MenuInterfaces, MenuJogos, MenuConfig };
            var labels = new[] { TxtMenuPrincipal, TxtMenuRotas, TxtMenuInterfaces, TxtMenuJogos, TxtMenuConfig };
            for (var index = 0; index < items.Length; index++)
            {
                if (index == selectedIndex)
                {
                    items[index].SetResourceReference(Border.BackgroundProperty, "SelectedBrush");
                    labels[index].SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    labels[index].FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    items[index].Background = Brushes.Transparent;
                    labels[index].SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    labels[index].FontWeight = FontWeights.Medium;
                }
            }
        }

        void AnimatePageTransition()
        {
            if (!SystemParameters.ClientAreaAnimation)
            {
                PageHost.Opacity = 1;
                PageHost.RenderTransform = Transform.Identity;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var translate = new TranslateTransform(0, 10);
            PageHost.RenderTransform = translate;
            PageHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.35, 1.0, new Duration(TimeSpan.FromMilliseconds(190))) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, new Duration(TimeSpan.FromMilliseconds(210))) { EasingFunction = ease });
        }

        void BtnThemeToggle_Click(object sender, RoutedEventArgs e) => ApplyTheme(_isDarkTheme ? "Light" : "Dark");
        void BtnThemeDark_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark");
        void BtnThemeLight_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light");

        void ApplyTheme(string? theme, bool persist = true)
        {
            _isDarkTheme = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);

            var palette = _isDarkTheme
                ? new Dictionary<string, string>
                {
                    ["AppBackgroundBrush"] = "#0B1020", ["SidebarBrush"] = "#0D1426", ["TopBarBrush"] = "#0B1020",
                    ["SurfaceBrush"] = "#121A2B", ["SurfaceElevatedBrush"] = "#182238", ["SurfaceMutedBrush"] = "#0F172A",
                    ["InputBrush"] = "#0E1729", ["BorderBrush"] = "#24324A", ["BorderStrongBrush"] = "#33445F",
                    ["TextPrimaryBrush"] = "#F4F7FB", ["TextSecondaryBrush"] = "#A7B3C7", ["TextMutedBrush"] = "#73829B",
                    ["HoverBrush"] = "#1C2941", ["SelectedBrush"] = "#123C50", ["AccentSoftBrush"] = "#12353F",
                    ["AccentTextBrush"] = "#071C22", ["SuccessSoftBrush"] = "#123728", ["WarningSoftBrush"] = "#3A2E14",
                    ["DangerSoftBrush"] = "#3C1D28", ["InfoSoftBrush"] = "#1A264B", ["OnAccentBrush"] = "#061719",
                    ["OverlayBrush"] = "#66000000"
                }
                : new Dictionary<string, string>
                {
                    ["AppBackgroundBrush"] = "#F5F7FB", ["SidebarBrush"] = "#FFFFFF", ["TopBarBrush"] = "#FFFFFF",
                    ["SurfaceBrush"] = "#FFFFFF", ["SurfaceElevatedBrush"] = "#F7F9FC", ["SurfaceMutedBrush"] = "#EEF2F7",
                    ["InputBrush"] = "#F8FAFC", ["BorderBrush"] = "#DDE5EF", ["BorderStrongBrush"] = "#C8D3E0",
                    ["TextPrimaryBrush"] = "#101828", ["TextSecondaryBrush"] = "#475467", ["TextMutedBrush"] = "#667085",
                    ["HoverBrush"] = "#F1F5F9", ["SelectedBrush"] = "#E2F8F9", ["AccentSoftBrush"] = "#DDF9FA",
                    ["AccentTextBrush"] = "#05272A", ["SuccessSoftBrush"] = "#EAFBF2", ["WarningSoftBrush"] = "#FFF8E7",
                    ["DangerSoftBrush"] = "#FFF0F3", ["InfoSoftBrush"] = "#EEF2FF", ["OnAccentBrush"] = "#05272A",
                    ["OverlayBrush"] = "#33000000"
                };

            foreach (var entry in palette)
                Application.Current.Resources[entry.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Value));

            ThemeGlyph.Text = _isDarkTheme ? "" : "";
            BtnThemeToggle.ToolTip = _isDarkTheme ? "Ativar modo claro" : "Ativar modo escuro";
            BtnThemeDark.SetResourceReference(Button.BorderBrushProperty, _isDarkTheme ? "AccentBrush" : "BorderBrush");
            BtnThemeLight.SetResourceReference(Button.BorderBrushProperty, _isDarkTheme ? "BorderBrush" : "AccentBrush");
            UpdateNavigationSelection(MainTabControl.SelectedIndex);
            ApplyStatusVisuals(_currentBinderState, _currentBinderDetail);
            PopulateRouteList();

            if (SystemParameters.ClientAreaAnimation)
                RootGrid.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.72, 1.0, new Duration(TimeSpan.FromMilliseconds(180))));

            if (persist)
            {
                try
                {
                    var conf = ReadConfigFile();
                    conf.Theme = _isDarkTheme ? "Dark" : "Light";
                    WriteConfigFile(conf);
                }
                catch (Exception ex) { Log("[TEMA] Falha ao salvar preferência: " + ex.Message); }
            }
        }

        void CbInspectAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if(CbInspectAdapter.SelectedIndex<0||CbInspectAdapter.SelectedIndex>=_adapters.Count) return;
            var ipToNic=new Dictionary<string,NetworkInterface>();
            foreach(var ni in NetworkInterface.GetAllNetworkInterfaces())foreach(var ua in ni.GetIPProperties().UnicastAddresses)if(ua.Address.AddressFamily==AddressFamily.InterNetwork)ipToNic[ua.Address.ToString()]=ni;
            FillAdapterStats(CbInspectAdapter.SelectedIndex,ipToNic);
        }

        // ── Config ─────────────────────────────────────────────────────────────
        string GetConfigPath() => AppPaths.ConfigPath;

        static readonly JsonSerializerOptions ConfigJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        AppConfig ReadConfigFile()
        {
            try
            {
                return File.Exists(GetConfigPath())
                    ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(GetConfigPath()), ConfigJsonOptions) ?? new AppConfig()
                    : new AppConfig();
            }
            catch (Exception ex)
            {
                AppLogger.Write("[CONFIG] Arquivo inválido; usando configuração segura: " + ex.Message);
                return new AppConfig();
            }
        }

        void WriteConfigFile(AppConfig config) =>
            AtomicFile.WriteAllText(GetConfigPath(), JsonSerializer.Serialize(config, ConfigJsonOptions));

        void LoadSettings()
        {
            try
            {
                var path = GetConfigPath();
                if (!File.Exists(path))
                {
                    var legacyPath = IOPath.Combine(AppContext.BaseDirectory, "config.json");
                    if (File.Exists(legacyPath))
                    {
                        Directory.CreateDirectory(AppPaths.UserDataDirectory);
                        File.Copy(legacyPath, path, overwrite: false);
                        Log("[CONFIG] Configuração antiga migrada para o perfil do usuário.");
                    }
                }
                if (!File.Exists(path))
                {
                    ApplyTheme("Dark", persist: false);
                    return;
                }
                var conf = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), ConfigJsonOptions);
                if (conf == null) return;
                ApplyTheme(conf.Theme, persist: false);
                ChkStartWindows.IsChecked = conf.StartWithWindows;
                ChkTray.IsChecked = conf.MinimizeToTray;
                ChkAutoActivate.IsChecked = conf.AutoActivate;
                _savedWeakAdapterId = conf.WeakAdapterId;
                _savedStrongAdapterId = conf.StrongAdapterId;

                if (conf.CustomRoutes?.Count > 0)
                {
                    var validRoutes = new List<RouteItem>();
                    var seenRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var route in conf.CustomRoutes)
                    {
                        if (!CidrUtility.TryNormalizeIPv4(route.Cidr, out var normalized, out var error) ||
                            CidrUtility.IsUnsafeDestination(normalized, out error))
                        {
                            Log($"[CONFIG] Rota ignorada ({route.Cidr}): {error}");
                            continue;
                        }
                        if (!seenRoutes.Add(normalized)) continue;
                        validRoutes.Add(new RouteItem
                        {
                            Cidr = normalized,
                            Desc = string.IsNullOrWhiteSpace(route.Desc) ? "Rota personalizada" : route.Desc.Trim(),
                            Enabled = route.Enabled,
                            Source = string.IsNullOrWhiteSpace(route.Source) ? "Config" : route.Source
                        });
                    }
                    _customRoutes.Clear();
                    foreach (var route in validRoutes) _customRoutes.Add(route);
                }

                if (conf.GameExes?.Count > 0)
                {
                    _gameExeVMs.Clear();
                    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var application in conf.GameExes)
                    {
                        if (string.IsNullOrWhiteSpace(application.Path)) continue;
                        string fullPath;
                        try { fullPath = IOPath.GetFullPath(application.Path); }
                        catch
                        {
                            Log("[CONFIG] Caminho de executável inválido ignorado: " + application.Path);
                            continue;
                        }
                        if (!seenPaths.Add(fullPath)) continue;
                        application.Path = fullPath;
                        if (!Enum.IsDefined(typeof(ApplicationBindingMode), application.BindingMode))
                            application.BindingMode = ApplicationBindingMode.ObserveOnly;
                        if (!Enum.IsDefined(typeof(AdapterPreference), application.PreferredAdapter))
                            application.PreferredAdapter = AdapterPreference.Strong;
                        if (application.BindingMode == ApplicationBindingMode.GameRoutesSafe)
                            application.PreferredAdapter = AdapterPreference.Strong;
                        application.Name = string.IsNullOrWhiteSpace(application.Name)
                            ? IOPath.GetFileNameWithoutExtension(fullPath)
                            : application.Name.Trim();
                        _gameExeVMs.Add(new GameExeViewModel(application));
                    }
                    Log($"[JOGOS] {_gameExeVMs.Count} exe(s) carregado(s) do config.");
                }
            }
            catch (Exception ex) { Log("[CONFIG] Falha ao carregar: " + ex.Message); }
        }

        async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var conf = new AppConfig
            {
                StartWithWindows = ChkStartWindows.IsChecked == true,
                MinimizeToTray   = ChkTray.IsChecked         == true,
                AutoActivate     = ChkAutoActivate.IsChecked == true,
                Theme            = _isDarkTheme ? "Dark" : "Light",
                WeakAdapterId    = (CbWeak.SelectedItem as AdapterInfo)?.Id ?? "",
                StrongAdapterId  = (CbStrong.SelectedItem as AdapterInfo)?.Id ?? "",
                CustomRoutes     = new List<RouteItem>(_customRoutes),
                GameExes         = _gameExeVMs.Select(v => v.Model).ToList()
            };
            WriteConfigFile(conf);
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
                await SetStartupTaskAsync(conf.StartWithWindows, exe);
            Log("[CONFIG] Salvo.");
        }

        async Task SetStartupTaskAsync(bool enable, string exe)
        {
            const string taskName = "NexusNetworkBinder";
            var arguments = enable
                ? new[]
                {
                    "/Create", "/F", "/RL", "HIGHEST", "/SC", "ONLOGON",
                    "/TN", taskName, "/TR", $"\"{exe}\" --hidden", "/DELAY", "0000:15"
                }
                : new[] { "/Delete", "/F", "/TN", taskName };

            var result = await RunToolAsync("schtasks.exe", arguments, TimeSpan.FromSeconds(20));
            Log(result.Success
                ? enable ? "[CONFIG] Tarefa de inicialização criada." : "[CONFIG] Tarefa de inicialização removida."
                : "[ERRO] O Windows não aceitou a alteração da tarefa de inicialização: " + result.Error);

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("NexusNetworkBinder", false);
            }
            catch { }
        }

        // ── Tray ───────────────────────────────────────────────────────────────
        void SetupTrayIcon()
        {
            System.Drawing.Icon ico;
            try{var d=IOPath.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)??"";var f=IOPath.Combine(d,"NexusNetworkBinder.ico");ico=File.Exists(f)?new System.Drawing.Icon(f):System.Drawing.SystemIcons.Shield;}
            catch{ico=System.Drawing.SystemIcons.Shield;}
            _trayIcon=new System.Windows.Forms.NotifyIcon{Icon=ico,Text="Nexus Network Binder v"+NetworkBinder.Version,Visible=true};
            _trayIcon.DoubleClick+=(s,e)=>Dispatcher.Invoke(RestoreWindow);
            var menu=new System.Windows.Forms.ContextMenuStrip();
            var open=new System.Windows.Forms.ToolStripMenuItem("📂 Abrir");open.Font=new System.Drawing.Font(open.Font,System.Drawing.FontStyle.Bold);open.Click+=(s,e)=>Dispatcher.Invoke(RestoreWindow);menu.Items.Add(open);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var cls=new System.Windows.Forms.ToolStripMenuItem("✖ Fechar");cls.Click+=(s,e)=>Dispatcher.Invoke(()=>{_forceClose=true;Close();});menu.Items.Add(cls);
            _trayIcon.ContextMenuStrip=menu;
        }

        void RestoreWindow(){Show();WindowState=WindowState.Normal;ShowInTaskbar=true;Activate();}
        protected override void OnStateChanged(EventArgs e){if(WindowState==WindowState.Minimized&&ChkTray.IsChecked==true){ShowInTaskbar=false;Hide();}base.OnStateChanged(e);}
        protected override void OnClosed(EventArgs e)
        {
            _netTimer?.Stop();
            _pingTimer?.Stop();
            if (_netChangeHandler != null) NetworkChange.NetworkAddressChanged -= _netChangeHandler;
            try { _proxyBindingManager.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _binder?.Dispose(); } catch { }
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
            base.OnClosed(e);
        }
        async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_allowCloseAfterRestore) return;

            if (!_forceClose && ChkTray.IsChecked == true)
            {
                e.Cancel = true;
                ShowInTaskbar = false;
                Hide();
                _trayIcon?.ShowBalloonTip(1500, "Nexus", "Rodando na bandeja.", System.Windows.Forms.ToolTipIcon.Info);
                return;
            }

            var binderNeedsRestore = _binder != null && _binder.State != BinderState.Inactive;
            var needsRestore = binderNeedsRestore || NetworkBinder.HasPendingRecovery;
            if (!needsRestore) return;

            e.Cancel = true;
            if (_closeRestoreInProgress) return;
            if (MessageBox.Show(
                    "O Nexus precisa restaurar as métricas, rotas e regras da transação antes de fechar. Desativar e sair?",
                    "Restaurar rede antes de sair",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                _forceClose = false;
                return;
            }

            _closeRestoreInProgress = true;
            try
            {
                SetUiState(BinderState.RollingBack, "Restaurando a rede antes de fechar...");
                var result = _binder != null
                    ? await _binder.DeactivateAsync(CancellationToken.None)
                    : await JournalRecovery.RestorePendingAsync(Log, CancellationToken.None);
                if (!result.Success)
                {
                    MessageBox.Show(
                        result.Message + "\n\nO aplicativo permanecerá aberto para preservar o diário e permitir nova tentativa.",
                        "Restauração incompleta",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    _forceClose = false;
                    return;
                }

                _allowCloseAfterRestore = true;
                _forceClose = true;
                Dispatcher.BeginInvoke(new Action(Close));
            }
            catch (Exception ex)
            {
                Log("[SAÍDA] Falha ao restaurar antes de fechar: " + ex.Message);
                MessageBox.Show(ex.Message, "Falha ao restaurar", MessageBoxButton.OK, MessageBoxImage.Error);
                _forceClose = false;
            }
            finally
            {
                _closeRestoreInProgress = false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Diálogo de seleção de executáveis da pasta
    // ══════════════════════════════════════════════════════════════════════════
    public class ExeSelectorDialog : Window
    {
        public List<string> SelectedPaths { get; } = new();

        private readonly ListBox _lb;

        public ExeSelectorDialog(List<string> paths, string folderName)
        {
            Title  = $"Selecionar executáveis — {folderName}";
            Width  = 580; Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Background = new SolidColorBrush(Color.FromRgb(0xF7,0xF9,0xFC));

            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = g;

            // Header
            var hdr = new TextBlock
            {
                Text = $"{paths.Count} executável(is) encontrado(s). Selecione os que pertencem ao jogo:",
                Margin = new Thickness(16, 14, 16, 4), FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x0F,0x17,0x2A)),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(hdr, 0); g.Children.Add(hdr);

            // Botões de seleção rápida
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16,4,16,8) };
            var selAll = new Button { Content = "Selecionar Tudo", Padding = new Thickness(10,4,10,4), Margin = new Thickness(0,0,8,0), Background = new SolidColorBrush(Color.FromRgb(0xE2,0xE8,0xF0)), Foreground = new SolidColorBrush(Color.FromRgb(0x0F,0x17,0x2A)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            var selNon = new Button { Content = "Limpar", Padding = new Thickness(10,4,10,4), Background = new SolidColorBrush(Color.FromRgb(0xE2,0xE8,0xF0)), Foreground = new SolidColorBrush(Color.FromRgb(0x0F,0x17,0x2A)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            btnRow.Children.Add(selAll); btnRow.Children.Add(selNon);
            Grid.SetRow(btnRow, 1); g.Children.Add(btnRow);

            // Lista
            _lb = new ListBox
            {
                SelectionMode = SelectionMode.Multiple,
                Margin = new Thickness(16, 0, 16, 8),
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8,0xD8,0xE8))
            };
            foreach (var p in paths)
            {
                var item = new ListBoxItem
                {
                    Content = IOPath.GetFileName(p),
                    Tag = p,
                    ToolTip = p,
                    Padding = new Thickness(8, 4, 8, 4)
                };
                _lb.Items.Add(item);
            }
            Grid.SetRow(_lb, 2); g.Children.Add(_lb);

            selAll.Click += (_, _) => { foreach (ListBoxItem i in _lb.Items) i.IsSelected = true; };
            selNon.Click += (_, _) => { foreach (ListBoxItem i in _lb.Items) i.IsSelected = false; };

            // Botões de confirmação
            var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 0, 16, 14) };
            var bc = new Button { Content = "Cancelar", Width = 90, Height = 32, Margin = new Thickness(0,0,8,0), Background = new SolidColorBrush(Color.FromRgb(0xE5,0xEB,0xF2)), Foreground = new SolidColorBrush(Color.FromRgb(0x3A,0x4A,0x5C)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            var ba = new Button { Content = "✚ Adicionar", Width = 110, Height = 32, Background = new SolidColorBrush(Color.FromRgb(0x10,0xB9,0x81)), Foreground = new SolidColorBrush(Colors.White), BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand };
            bc.Click += (_, _) => { DialogResult = false; Close(); };
            ba.Click += (_, _) =>
            {
                foreach (ListBoxItem item in _lb.SelectedItems)
                    if (item.Tag is string path) SelectedPaths.Add(path);
                DialogResult = true; Close();
            };
            bp.Children.Add(bc); bp.Children.Add(ba);
            Grid.SetRow(bp, 3); g.Children.Add(bp);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Diálogo de import em lote de rotas
    // ══════════════════════════════════════════════════════════════════════════
    public class BulkImportDialog : Window
    {
        public string InputText { get; private set; } = "";
        readonly TextBox _tb;
        public BulkImportDialog()
        {
            Title="Import em Lote";Width=480;Height=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;ResizeMode=ResizeMode.NoResize;Background=new SolidColorBrush(Color.FromRgb(0xF7,0xF9,0xFC));
            var g=new Grid();g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});g.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Content=g;
            var info=new TextBlock{Text="Cole um IP/CIDR por linha. Descrição opcional após espaço.\nLinhas com # são ignoradas.",Margin=new Thickness(16,14,16,8),TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(0x4A,0x5C,0x72)),FontSize=12};Grid.SetRow(info,0);g.Children.Add(info);
            _tb=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new FontFamily("Consolas"),FontSize=12,Margin=new Thickness(16,0,16,8),Padding=new Thickness(8),Background=new SolidColorBrush(Colors.White),BorderBrush=new SolidColorBrush(Color.FromRgb(0xC8,0xD8,0xE8)),BorderThickness=new Thickness(1)};Grid.SetRow(_tb,1);g.Children.Add(_tb);
            var dica=new TextBlock{Text="Ctrl+V para colar.",Margin=new Thickness(16,0,16,8),FontSize=11,FontStyle=FontStyles.Italic,Foreground=new SolidColorBrush(Color.FromRgb(0x8A,0x9A,0xAA))};Grid.SetRow(dica,2);g.Children.Add(dica);
            var bp=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(16,0,16,14)};
            var bc=new Button{Content="Cancelar",Width=90,Height=32,Margin=new Thickness(0,0,8,0),Background=new SolidColorBrush(Color.FromRgb(0xE5,0xEB,0xF2)),Foreground=new SolidColorBrush(Color.FromRgb(0x3A,0x4A,0x5C)),BorderThickness=new Thickness(0),Cursor=System.Windows.Input.Cursors.Hand};
            bc.Click+=(s,e)=>{DialogResult=false;Close();};
            var bi=new Button{Content="✚ Importar",Width=100,Height=32,Background=new SolidColorBrush(Color.FromRgb(0x1A,0x6B,0xD4)),Foreground=new SolidColorBrush(Colors.White),BorderThickness=new Thickness(0),FontWeight=FontWeights.SemiBold,Cursor=System.Windows.Input.Cursors.Hand};
            bi.Click+=(s,e)=>{InputText=_tb.Text;DialogResult=true;Close();};
            bp.Children.Add(bc);bp.Children.Add(bi);Grid.SetRow(bp,3);g.Children.Add(bp);
        }
    }
}
