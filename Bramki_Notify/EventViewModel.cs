using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Bramki_Notify
{
    public sealed class EventItemVm : INotifyPropertyChanged
    {
        public string Title { get; init; } = "";
        public string PersonLine { get; init; } = "";
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string DeniedReason { get; init; } = "";

        private bool _isAttention;
        public bool IsAttention
        {
            get => _isAttention;
            set
            {
                if (_isAttention == value) return;
                _isAttention = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}