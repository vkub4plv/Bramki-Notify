using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace Bramki_Notify
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private const int MaxRegular = 50;
        private const int MaxDenied = 50;

        private string _connectionStatus = "Brak połączenia";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set
            {
                if (_connectionStatus == value) return;
                _connectionStatus = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<EventItemVm> RegularEvents { get; } = new();
        public ObservableCollection<EventItemVm> DeniedEvents { get; } = new();

        public event EventHandler<EventItemVm>? AccessDeniedRaised;

        private CancellationTokenSource? _ackCts;

        private readonly DispatcherTimer _nowTimer;

        private DateTime _now = DateTime.Now;
        public DateTime Now
        {
            get => _now;
            private set
            {
                if (_now == value) return;
                _now = value;
                OnPropertyChanged();
            }
        }

        public MainViewModel()
        {
            _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _nowTimer.Tick += (_, __) => Now = DateTime.Now;
            _nowTimer.Start();
        }

        public void AddRegular(EventItemVm item)
        {
            RegularEvents.Insert(0, item);
            Trim(RegularEvents, MaxRegular);
        }

        public void AddDenied(EventItemVm item)
        {
            item.IsAttention = true;

            DeniedEvents.Insert(0, item);
            Trim(DeniedEvents, MaxDenied);

            AccessDeniedRaised?.Invoke(this, item);
        }

        public void AcknowledgeAllDeniedAfter(TimeSpan delay)
        {
            _ackCts?.Cancel();
            _ackCts = new CancellationTokenSource();
            var ct = _ackCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, ct);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var e in DeniedEvents.Where(x => x.IsAttention))
                            e.IsAttention = false;
                    });
                }
                catch (TaskCanceledException) { }
            }, ct);
        }

        public void ClearAttentionForItemAfter(EventItemVm item, TimeSpan delay)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                await Application.Current.Dispatcher.InvokeAsync(() => item.IsAttention = false);
            });
        }

        private static void Trim<T>(ObservableCollection<T> col, int max)
        {
            while (col.Count > max)
                col.RemoveAt(col.Count - 1);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}