using System;
using System.ComponentModel;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Bramki_Notify
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        public MainViewModel ViewModel => _vm;

        public MainWindow()
        {
            InitializeComponent();

            SetupAttentionPulse();

            _vm = new MainViewModel();
            _vm.AccessDeniedRaised += (_, item) => NotifyAccessDenied(item);

            DataContext = _vm;

            Activated += (_, __) => _vm.AcknowledgeAllDeniedAfter(TimeSpan.FromSeconds(3));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            var app = (App)Application.Current;

            if (!app.IsExitRequested)
            {
                e.Cancel = true;
                app.HideMainToTray();
                return;
            }

            base.OnClosing(e);
        }

        private void NotifyAccessDenied(EventItemVm item)
        {
            SystemSounds.Exclamation.Play();

            var app = (App)Application.Current;

            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

            BringToFrontWithoutActivating();

            // If user is already focused on the window, let it flash briefly then stop.
            // If not focused, it will keep flashing until Activated + 3s.
            if (IsActive)
                _vm.ClearAttentionForItemAfter(item, TimeSpan.FromSeconds(3));
        }

        public void BringToFrontWithoutActivating()
        {
            Topmost = true;
            Topmost = false;
            FlashWindow();
        }

        private void FlashWindow()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            FLASHWINFO fw = new FLASHWINFO
            {
                cbSize = Convert.ToUInt32(Marshal.SizeOf(typeof(FLASHWINFO))),
                hwnd = helper.Handle,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0
            };
            FlashWindowEx(ref fw);
        }

        private const uint FLASHW_TRAY = 0x00000002;
        private const uint FLASHW_TIMERNOFG = 0x0000000C;

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        private void SetupAttentionPulse()
        {
            // Create an unfrozen brush instance
            var brush = new SolidColorBrush(Colors.White);

            // Replace the placeholder resource with the modifiable shared instance
            Resources["AttentionPulseBrush"] = brush;

            var anim = new ColorAnimation
            {
                From = Colors.White,
                To = Color.FromRgb(0xFF, 0xD6, 0xD6),   // #FFFFD6D6
                Duration = TimeSpan.FromSeconds(0.8),   // half-cycle
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            // Animate the brush (smooth breathing, globally synchronized)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
    }
}