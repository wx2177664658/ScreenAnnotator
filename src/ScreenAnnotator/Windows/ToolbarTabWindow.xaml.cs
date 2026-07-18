using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenAnnotator.Windows;

public partial class ToolbarTabWindow : Window
{
    private readonly MainWindow _main;
    private bool _recording;

    public ToolbarTabWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
    }

    public void SetRecordingUi(bool recording, TimeSpan elapsed)
    {
        _recording = recording;
        if (recording)
        {
            Width = 168;
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0xF5, 0xF5, 0xF5));
            LabelText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
            LabelText.Text = $"● 录制中 {elapsed:hh\\:mm\\:ss}";
            ToolTip = "点击停止录制";
        }
        else
        {
            Width = 100;
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xAA, 0xF5, 0xF5, 0xF5));
            LabelText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            LabelText.Text = "展开";
            ToolTip = null;
        }
    }

    private void OnChromeMouseEnter(object sender, MouseEventArgs e)
    {
        Cursor = Cursors.Arrow;
        ForceCursor = true;
        Mouse.OverrideCursor = Cursors.Arrow;
        _main.NotifyPointerOverChrome(true);
    }

    private void OnChromeMouseMove(object sender, MouseEventArgs e)
    {
        Mouse.OverrideCursor = Cursors.Arrow;
        _main.NotifyPointerOverChrome(true);
    }

    private void OnChromeMouseLeave(object sender, MouseEventArgs e)
    {
        _main.SyncChromePointerState();
    }

    private void OnExpandClick(object sender, MouseButtonEventArgs e)
    {
        if (_recording)
            _main.StopScreenRecording();
        else
            _main.OnToolbarTabClicked();
        e.Handled = true;
    }
}
