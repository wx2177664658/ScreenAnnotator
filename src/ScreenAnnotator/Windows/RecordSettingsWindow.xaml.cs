using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ScreenAnnotator.Windows;

public partial class RecordSettingsWindow : Window
{
    public string OutputPath { get; private set; } = "";
    public bool CaptureMicrophone { get; private set; }
    public bool CaptureSystemAudio { get; private set; }

    public RecordSettingsWindow(Window? owner)
    {
        Owner = owner;
        InitializeComponent();
        PathBox.Text = SuggestDefaultPath();
    }

    private static string SuggestDefaultPath()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return System.IO.Path.Combine(dir, $"录屏_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            FileName = System.IO.Path.GetFileName(PathBox.Text),
            InitialDirectory = Path.GetDirectoryName(PathBox.Text) is { Length: > 0 } d && Directory.Exists(d)
                ? d
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FileName;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, "请选择保存路径。", "录屏", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            path += ".mp4";

        var mic = ChkMic.IsChecked == true;
        var sys = ChkSystem.IsChecked == true;
        if ((mic || sys) && ScreenAnnotator.Services.ScreenRecordService.FindFfmpeg() == null)
        {
            var r = MessageBox.Show(this,
                "录制声音需要 FFmpeg（程序目录下 ffmpeg\\ffmpeg.exe）。\n\n是否改为仅录画面继续？",
                "录屏", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            mic = false;
            sys = false;
        }

        OutputPath = path;
        CaptureMicrophone = mic;
        CaptureSystemAudio = sys;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
