using MsfsAiAtc.Audio;
using MsfsAiAtc.Handoff;
using MsfsAiAtc.SimBridge;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MsfsAiAtc.Overlay;

/// <summary>
/// A single line in the conversation log.
/// </summary>
public class ChatEntry
{
    public string  Prefix            { get; set; } = string.Empty;
    public string  Text              { get; set; } = string.Empty;
    public Brush   PrefixColor       { get; set; } = Brushes.White;
    public Brush   TextColor         { get; set; } = Brushes.White;
    public bool    IsPilot           { get; set; } = false;
    public bool    IsSystem          { get; set; } = false;
    /// Hides the Prefix row for system messages (which use just one line)
    public Visibility PrefixVisibility => IsSystem ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Transparent, always-on-top overlay window.
/// Never steals keyboard focus.
/// All UI updates must be dispatched to the UI thread.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly ObservableCollection<ChatEntry> _chatEntries = new();
    private const int MaxLogEntries = 50;

    // State colors
    private static readonly SolidColorBrush _idleColor = new(Color.FromRgb(0x33, 0x4D, 0xA6)); // muted blue
    private static readonly SolidColorBrush _recordingColor = new(Color.FromRgb(0xFF, 0x55, 0x55)); // red
    private static readonly SolidColorBrush _processingColor = new(Color.FromRgb(0xFF, 0xCD, 0x8A)); // amber
    private static readonly SolidColorBrush _speakingColor = new(Color.FromRgb(0x66, 0xBB, 0x8E)); // green

    private static readonly SolidColorBrush _pilotBrush = new(Color.FromRgb(0xFF, 0xCD, 0x8A));
    private static readonly SolidColorBrush _atcBrush = new(Color.FromRgb(0x66, 0xBB, 0x8E));
    private static readonly SolidColorBrush _systemBrush = new(Color.FromRgb(0x88, 0x99, 0xBB));

    public OverlayWindow()
    {
        InitializeComponent();
        ChatLog.ItemsSource = _chatEntries;

        // Prevent focus stealing
        this.Focusable = false;
        this.IsHitTestVisible = true; // must be true for drag

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Prevent this window from appearing in Alt-Tab
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowExStyle(hwnd);
    }

    private static void SetWindowExStyle(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // ─── Public API called from main application ──────────────────────────────

    public void UpdateVoiceState(VoiceState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string label;
            Brush dotColor;
            bool pttEnabled;
            string pttFg;

            switch (state)
            {
                case VoiceState.Recording:
                    label = "RECORDING";
                    dotColor = _recordingColor;
                    pttEnabled = true;
                    pttFg = "#FF5555";
                    break;
                case VoiceState.Processing:
                    label = "PROCESSING";
                    dotColor = _processingColor;
                    pttEnabled = false;
                    pttFg = "#555577";
                    break;
                case VoiceState.Speaking:
                    label = "SPEAKING";
                    dotColor = _speakingColor;
                    pttEnabled = false;
                    pttFg = "#555577";
                    break;
                default: // Idle
                    label = "IDLE";
                    dotColor = _idleColor;
                    pttEnabled = true;
                    pttFg = "#4DA6FF";
                    break;
            }

            StateLabel.Text = label;
            StateLabel.Foreground = dotColor;
            StateDot.Fill = dotColor;

            // Grey out PTT when not IDLE
            PttLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pttFg));
            PttIndicator.Opacity = pttEnabled ? 1.0 : 0.35;

            // Pulse animation on RECORDING
            if (state == VoiceState.Recording) StartPulse();
            else StopPulse();
        });
    }

    public void UpdateSimState(SimState simState)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var connected = simState.IsConnected;
            SimDot.Fill = connected
                ? new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x8E))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
            SimStatusText.Text = connected
                ? $"SIM: {simState.AltitudeMslFt:F0}ft {simState.GroundSpeedKts:F0}kts"
                : "SIM: Disconnected";

            if (simState.NearestAirportIcao != null)
                AirportLabel.Text = $"· {simState.NearestAirportIcao}";
        });
    }

    public void UpdateControllerPhase(ControllerPhase phase, double freqMhz = 0)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ControllerLabel.Text = phase switch
            {
                ControllerPhase.ClearanceDelivery => "Clearance",
                ControllerPhase.Ground => "Ground",
                ControllerPhase.Tower => "Tower",
                ControllerPhase.Departure => "Departure",
                ControllerPhase.Center => "Center",
                ControllerPhase.Approach => "Approach",
                _ => "ATC"
            };
            FreqLabel.Text = freqMhz > 0 ? $"  {freqMhz:F3}" : string.Empty;
        });
    }

    public void SetPttKeyHint(string keyName)
    {
        Dispatcher.InvokeAsync(() => PttKeyHint.Text = $"[{keyName}]");
    }

    public void AddPilotMessage(string text)
    {
        AddEntry(new ChatEntry
        {
            Prefix      = "YOU",
            Text        = text,
            IsPilot     = true,
            PrefixColor = _pilotBrush,
            TextColor   = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))
        });
    }

    public void AddAtcMessage(string text, string controllerLabel = "ATC")
    {
        AddEntry(new ChatEntry
        {
            Prefix = controllerLabel.ToUpper(),
            Text = text,
            PrefixColor = _atcBrush,
            TextColor = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xF6))
        });
    }

    public void AddSystemMessage(string text)
    {
        AddEntry(new ChatEntry
        {
            Prefix    = "·",
            Text      = text,
            IsSystem  = true,
            PrefixColor = _systemBrush,
            TextColor   = _systemBrush
        });
    }

    private void AddEntry(ChatEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _chatEntries.Add(entry);
            // Trim log
            while (_chatEntries.Count > MaxLogEntries)
                _chatEntries.RemoveAt(0);
            // Auto-scroll to bottom
            ChatScroll.ScrollToBottom();
        });
    }

    // ─── Pulse animation ──────────────────────────────────────────────────────

    private DoubleAnimation? _pulseAnim;

    private void StartPulse()
    {
        _pulseAnim = new DoubleAnimation(1.0, 0.3, TimeSpan.FromSeconds(0.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        StateDot.BeginAnimation(OpacityProperty, _pulseAnim);
    }

    private void StopPulse()
    {
        StateDot.BeginAnimation(OpacityProperty, null);
        StateDot.Opacity = 1.0;
    }

    // ─── Window chrome ────────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        // Hide instead of close — let main app manage lifecycle
        this.Hide();
    }
}
