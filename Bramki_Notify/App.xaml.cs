using System;
using System.Threading;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Bramki_Notify
{
    public partial class App : Application
    {
        private NotifyIcon? _notifyIcon;
        private bool _isExitRequested;

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showEvent;
        private CancellationTokenSource? _singleInstanceCts;
        private CancellationTokenSource? _apiCts;

        private BramkiApiMonitor? _monitor;

        // Set the actual API server address and credentials here
        private const string ApiBaseUrl = "http://API_Server_Address";
        private const string ApiLogin = "API_Login";
        private const string ApiPassword = "API_Password";

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = @"Local\BramkiNotify.SingleInstance";
            const string showEventName = @"Local\BramkiNotify.Show";

            _instanceMutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool isFirstInstance);

            if (!isFirstInstance)
            {
                for (int i = 0; i < 30; i++) // ~3 seconds total
                {
                    try
                    {
                        using var ev = EventWaitHandle.OpenExisting(showEventName);
                        ev.Set();
                        break;
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                        Thread.Sleep(100);
                    }
                    catch
                    {
                        break;
                    }
                }

                Shutdown();
                return;
            }

            // First instance: create the event and start listening for "show" signals
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showEventName, out bool createdNew);
            System.Diagnostics.Debug.WriteLine($"Show event createdNew={createdNew}");
            _singleInstanceCts = new CancellationTokenSource();
            _ = Task.Run(() => ShowSignalLoop(_singleInstanceCts.Token));

            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var main = new MainWindow();
            MainWindow = main;

            // Start minimized-to-tray when autostarted
            bool autostart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

            if (!autostart)
            {
                main.Show();
            }

            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Text = "Bramki - powiadomienia",
                Icon = LoadTrayIcon("Assets/app.ico"),
            };

            var menu = new ContextMenuStrip();

            var startWithWindows = new ToolStripMenuItem("Uruchamiaj z Windows")
            {
                Checked = StartupManager.IsEnabled(),
                CheckOnClick = true
            };
            startWithWindows.CheckedChanged += (_, __) =>
            {
                try { StartupManager.SetEnabled(startWithWindows.Checked); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Startup toggle error: " + ex.Message); }
            };

            menu.Items.Add(startWithWindows);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Otwórz", null, (_, __) => ShowMain());
            menu.Items.Add("Zakończ", null, (_, __) => ExitApp());
            _notifyIcon.ContextMenuStrip = menu;

            _notifyIcon.DoubleClick += (_, __) => ShowMain();

            _apiCts = new CancellationTokenSource();
            _ = StartApiAsync(_apiCts.Token);
        }

        private async Task StartApiAsync(CancellationToken ct)
        {
            // backoff: 2s -> 5s -> 10s -> 20s -> 40s -> 60s (cap)
            var delay = TimeSpan.FromSeconds(2);
            var maxDelay = TimeSpan.FromSeconds(60);

            void SetStatus(string text)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is MainWindow mw)
                        mw.ViewModel.ConnectionStatus = text;
                }));
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetStatus("Łączenie z Roger API...");

                    _monitor = new BramkiApiMonitor(ApiBaseUrl, ApiLogin, ApiPassword);
                    _monitor.Log += msg => System.Diagnostics.Debug.WriteLine(msg);

                    _monitor.EventReceived += apiEvent =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (MainWindow is not MainWindow mw) return;

                            var item = new EventItemVm
                            {
                                Title = apiEvent.Title,
                                PersonLine = apiEvent.PersonLine,
                                Timestamp = apiEvent.Timestamp,
                                DeniedReason = apiEvent.DeniedReason ?? ""
                            };

                            if (apiEvent.Kind == ApiEventKind.Denied)
                                mw.ViewModel.AddDenied(item);
                            else
                                mw.ViewModel.AddRegular(item);
                        }));
                    };

                    await _monitor.ConnectAsync();
                    await _monitor.StartMonitoringAsync();

                    SetStatus("Połączono z Roger API. Monitorowanie zdarzeń.");
                    delay = TimeSpan.FromSeconds(2); // reset backoff

                    var finished = await Task.WhenAny(_monitor.MonitoringTask, Task.Delay(Timeout.Infinite, ct));
                    if (finished == _monitor.MonitoringTask)
                        throw new Exception("Monitorowanie zakończone — ponawianie połączenia.");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SetStatus($"Brak połączenia. Ponowna próba za {(int)delay.TotalSeconds}s…");
                    System.Diagnostics.Debug.WriteLine("API connect failed: " + ex.Message);

                    try { if (_monitor != null) await _monitor.DisposeAsync(); } catch { }

                    await Task.Delay(delay, ct);
                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
                }
            }

            SetStatus("Brak połączenia");
        }

        private static Icon LoadTrayIcon(string relativePackPath)
        {
            var uri = new Uri($"pack://application:,,,/{relativePackPath}", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri)
                ?? throw new InvalidOperationException($"Tray icon not found: {relativePackPath}");

            return new Icon(streamInfo.Stream);
        }

        public void HideMainToTray() => MainWindow?.Hide();

        public void ShowMain()
        {
            if (MainWindow == null) return;

            if (!MainWindow.IsVisible) MainWindow.Show();
            if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;

            if (MainWindow is MainWindow mw) mw.BringToFrontWithoutActivating();
        }

        private void ShowSignalLoop(CancellationToken ct)
        {
            if (_showEvent == null) return;

            WaitHandle[] handles = { _showEvent, ct.WaitHandle };

            while (true)
            {
                int signaled = WaitHandle.WaitAny(handles);
                if (signaled == 1) break;

                Dispatcher.BeginInvoke(new Action(() => ShowMain()));
            }
        }

        public void ExitApp()
        {
            _isExitRequested = true;
            _apiCts?.Cancel();
            _singleInstanceCts?.Cancel();
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Task.Run(async () =>
                {
                    try
                    {
                        if (_monitor != null)
                            await _monitor.DisposeAsync();
                    }
                    catch { /* ignore */ }

                    try
                    {
                        if (_notifyIcon != null)
                        {
                            _notifyIcon.Visible = false;
                            _notifyIcon.Dispose();
                        }
                    }
                    catch { /* ignore */ }

                    try { _apiCts?.Cancel(); } catch { }
                    try { _singleInstanceCts?.Cancel(); } catch { }

                    try { _showEvent?.Close(); } catch { }
                    try { _instanceMutex?.ReleaseMutex(); } catch { }
                    try { _instanceMutex?.Dispose(); } catch { }

                }).Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* ignore */ }

            base.OnExit(e);
        }

        public bool IsExitRequested => _isExitRequested;
    }
}