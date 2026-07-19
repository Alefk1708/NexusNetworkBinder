using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace NexusNetworkBinder
{
    public partial class App : Application
    {
        private Mutex? _singleInstanceMutex;
        public static bool StartHidden { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\NexusNetworkBinder.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "O Nexus Network Binder já está em execução. Verifique a bandeja do sistema.",
                    "Nexus Network Binder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            foreach (var arg in e.Args)
            {
                if (arg.Equals("--hidden", StringComparison.OrdinalIgnoreCase))
                {
                    StartHidden = true;
                    break;
                }
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
            DispatcherUnhandledException += (_, args) =>
            {
                AppLogger.Write("[FATAL/UI] " + args.Exception);
                MessageBox.Show(args.Exception.Message, "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLogger.Write("[FATAL/TASK] " + args.Exception);
                args.SetObserved();
            };
            base.OnStartup(e);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;

            if (StartHidden)
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.ShowInTaskbar = false;
            }

            // Show() é necessário para disparar Loaded e inicializar configuração,
            // timers, bandeja e autoativação. Em modo oculto a janela é escondida
            // imediatamente após a criação do handle, sem permanecer na barra.
            mainWindow.Show();
            if (StartHidden)
            {
                mainWindow.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(mainWindow.Hide));
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
