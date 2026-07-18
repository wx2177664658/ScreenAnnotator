using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenCvSharp;
using ScreenAnnotator.Models;
using Drawing = System.Drawing;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using Rect = System.Windows.Rect;

namespace ScreenAnnotator.Services;

public sealed class ScreenRecordOptions
{
    public string OutputPath { get; set; } = "";
    public bool CaptureMicrophone { get; set; }
    public bool CaptureSystemAudio { get; set; }
    public Rect ScreenBoundsDevicePx { get; set; }
    public double CanvasWidthDip { get; set; }
    public double CanvasHeightDip { get; set; }
    public bool OpaqueWhiteBackground { get; set; }
    public int Fps { get; set; } = 24;
}

/// <summary>
/// CR-014：合成「选中屏 + 标注」写 MP4；可选麦/系统声（有 FFmpeg 时混入）。
/// </summary>
public sealed class ScreenRecordService : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _videoTask;
    private VideoWriter? _writer;
    private string? _tempVideoPath;
    private string? _tempAudioPath;
    private WaveFileWriter? _audioWriter;
    private IWaveIn? _micCapture;
    private WasapiLoopbackCapture? _loopCapture;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _loopBuffer;
    private MixingSampleProvider? _mixer;
    private ISampleProvider? _mixedProvider;
    private System.Threading.Timer? _audioPump;
    private DateTime _startedUtc;
    private bool _disposed;
    private string? _lastWarning;
    private Exception? _fatalError;
    private bool _discardOutput;

    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedUtc : TimeSpan.Zero;
    public string? LastWarning => _lastWarning;

    public event Action? FrameCaptured;
    public event Action<string>? Warning;

    public static string? FindFfmpeg()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(baseDir, "ffmpeg.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenAnnotator", "ffmpeg", "ffmpeg.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "tools", "ffmpeg", "ffmpeg.exe"))
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(2000);
            var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line)) return line;
        }
        catch { /* ignore */ }

        return null;
    }

    public void Start(
        ScreenRecordOptions options,
        Func<IReadOnlyList<AnnotationObject>> getObjects,
        DispatcherInvoker invoke)
    {
        if (IsRecording) throw new InvalidOperationException("已在录制中。");
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("未指定输出路径。");

        var needAudio = options.CaptureMicrophone || options.CaptureSystemAudio;
        var ffmpeg = FindFfmpeg();
        if (needAudio && ffmpeg == null)
            throw new InvalidOperationException(
                "录制声音需要 FFmpeg。请将 ffmpeg.exe 放到程序目录的 ffmpeg\\ 文件夹后重试；也可关闭麦克风与系统声音，仅录画面。");

        _lastWarning = null;
        _fatalError = null;
        var w = Math.Max(2, (int)Math.Ceiling(options.ScreenBoundsDevicePx.Width));
        var h = Math.Max(2, (int)Math.Ceiling(options.ScreenBoundsDevicePx.Height));
        if (w % 2 != 0) w--;
        if (h % 2 != 0) h--;
        w = Math.Max(2, w);
        h = Math.Max(2, h);

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        _tempVideoPath = Path.Combine(Path.GetTempPath(), $"sa_rec_{Guid.NewGuid():N}.mp4");
        _tempAudioPath = needAudio
            ? Path.Combine(Path.GetTempPath(), $"sa_rec_{Guid.NewGuid():N}.wav")
            : null;

        // mp4v：无 FFmpeg 时也可直接作为 MP4 画面文件；有 FFmpeg 时再转 H.264 / 混音
        _writer = new VideoWriter(_tempVideoPath, FourCC.FromString("mp4v"), options.Fps, new OpenCvSharp.Size(w, h));
        if (!_writer.IsOpened())
        {
            // 回退 MJPG/AVI
            _tempVideoPath = Path.Combine(Path.GetTempPath(), $"sa_rec_{Guid.NewGuid():N}.avi");
            _writer = new VideoWriter(_tempVideoPath, FourCC.FromString("MJPG"), options.Fps, new OpenCvSharp.Size(w, h));
        }
        if (!_writer.IsOpened())
        {
            _writer.Dispose();
            _writer = null;
            throw new InvalidOperationException("无法创建视频编码器。");
        }

        if (needAudio)
            StartAudio(options);

        _cts = new CancellationTokenSource();
        _startedUtc = DateTime.UtcNow;
        IsRecording = true;
        var token = _cts.Token;
        var left = (int)options.ScreenBoundsDevicePx.Left;
        var top = (int)options.ScreenBoundsDevicePx.Top;
        var canvasW = options.CanvasWidthDip;
        var canvasH = options.CanvasHeightDip;
        var whiteBg = options.OpaqueWhiteBackground;
        var fps = options.Fps;
        var outputPath = options.OutputPath;

        _videoTask = Task.Run(() => RecordLoop(
            left, top, w, h, canvasW, canvasH, whiteBg, fps, getObjects, invoke, outputPath, ffmpeg, token), token);
    }

    private void StartAudio(ScreenRecordOptions options)
    {
        var inputs = new List<ISampleProvider>();

        if (options.CaptureMicrophone)
        {
            try
            {
                var mic = new WasapiCapture();
                _micBuffer = new BufferedWaveProvider(mic.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(3)
                };
                mic.DataAvailable += (_, e) =>
                {
                    try { _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
                    catch { /* ignore */ }
                };
                mic.StartRecording();
                _micCapture = mic;
                inputs.Add(NormalizeSampleProvider(new WaveToSampleProvider(_micBuffer)));
            }
            catch (Exception ex)
            {
                EmitWarning($"麦克风不可用：{ex.Message}（继续录画面）");
            }
        }

        if (options.CaptureSystemAudio)
        {
            try
            {
                var loop = new WasapiLoopbackCapture();
                _loopBuffer = new BufferedWaveProvider(loop.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(3)
                };
                loop.DataAvailable += (_, e) =>
                {
                    try { _loopBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
                    catch { /* ignore */ }
                };
                loop.StartRecording();
                _loopCapture = loop;
                inputs.Add(NormalizeSampleProvider(new WaveToSampleProvider(_loopBuffer)));
            }
            catch (Exception ex)
            {
                EmitWarning($"系统声音不可用：{ex.Message}（继续录画面）");
            }
        }

        if (inputs.Count == 0)
        {
            _tempAudioPath = null;
            return;
        }

        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2))
        {
            ReadFully = true
        };
        foreach (var i in inputs)
            _mixer.AddMixerInput(i);
        _mixedProvider = _mixer;
        _audioWriter = new WaveFileWriter(_tempAudioPath!, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        _audioPump = new System.Threading.Timer(_ => PumpAudio(), null, 0, 20);
    }

    private static ISampleProvider NormalizeSampleProvider(ISampleProvider src)
    {
        if (src.WaveFormat.Channels == 1)
            src = new MonoToStereoSampleProvider(src);
        if (src.WaveFormat.SampleRate != 44100)
            src = new WdlResamplingSampleProvider(src, 44100);
        return src;
    }

    private void PumpAudio()
    {
        try
        {
            if (_mixedProvider == null || _audioWriter == null) return;
            var buffer = new float[44100 / 50 * 2];
            var read = _mixedProvider.Read(buffer, 0, buffer.Length);
            if (read > 0)
                _audioWriter.WriteSamples(buffer, 0, read);
        }
        catch { /* ignore */ }
    }

    private void RecordLoop(
        int left, int top, int w, int h,
        double canvasW, double canvasH, bool whiteBg, int fps,
        Func<IReadOnlyList<AnnotationObject>> getObjects,
        DispatcherInvoker invoke,
        string outputPath, string? ffmpeg, CancellationToken token)
    {
        var frameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps));
        var next = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                next += frameInterval;
                Mat? mat = null;
                invoke(() =>
                {
                    var objects = getObjects();
                    mat = CaptureComposedFrame(left, top, w, h, canvasW, canvasH, whiteBg, objects);
                });

                if (mat != null)
                {
                    using (mat)
                    {
                        lock (_gate)
                            _writer?.Write(mat);
                    }
                    FrameCaptured?.Invoke();
                }

                var delay = next - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    Thread.Sleep(delay);
                else
                    next = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            EmitWarning($"录制出错：{ex.Message}");
        }
        finally
        {
            FinalizeFiles(outputPath, ffmpeg);
        }
    }

    private static Mat CaptureComposedFrame(
        int left, int top, int w, int h,
        double canvasW, double canvasH, bool whiteBg,
        IReadOnlyList<AnnotationObject> objects)
    {
        using var bmp = new Drawing.Bitmap(w, h, DrawingPixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(whiteBg ? Drawing.Color.White : Drawing.Color.Black);
            if (!whiteBg)
                g.CopyFromScreen(left, top, 0, 0, new Drawing.Size(w, h), Drawing.CopyPixelOperation.SourceCopy);
        }

        // 标注按 DIP 画布渲染，再缩放到设备像素（与截屏合成一致且适配高 DPI）
        var dipW = Math.Max(1, (int)Math.Ceiling(canvasW));
        var dipH = Math.Max(1, (int)Math.Ceiling(canvasH));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var obj in objects)
                obj.Draw(dc);
        }
        var rtb = new RenderTargetBitmap(dipW, dipH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var stride = dipW * 4;
        var pixels = new byte[stride * dipH];
        rtb.CopyPixels(pixels, stride, 0);

        using (var overlay = new Drawing.Bitmap(dipW, dipH, DrawingPixelFormat.Format32bppArgb))
        {
            var bd = overlay.LockBits(new Drawing.Rectangle(0, 0, dipW, dipH),
                ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppArgb);
            try { Marshal.Copy(pixels, 0, bd.Scan0, pixels.Length); }
            finally { overlay.UnlockBits(bd); }

            using var g2 = Drawing.Graphics.FromImage(bmp);
            g2.DrawImage(overlay, new Drawing.Rectangle(0, 0, w, h));
        }

        using var bgr = BitmapToMat(bmp);
        var mat = new Mat();
        Cv2.CvtColor(bgr, mat, ColorConversionCodes.BGRA2BGR);
        return mat;
    }

    private static Mat BitmapToMat(Drawing.Bitmap bmp)
    {
        var rect = new Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var mat = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC4, bd.Scan0, bd.Stride);
            return mat.Clone();
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    public void Stop(bool discardOutput = false)
    {
        if (!IsRecording) return;
        _discardOutput = discardOutput;
        IsRecording = false;
        try { _cts?.Cancel(); } catch { }
        try { _videoTask?.Wait(20000); } catch { }
        CleanupAudioDevices();
    }

    private void FinalizeFiles(string outputPath, string? ffmpeg)
    {
        lock (_gate)
        {
            try { _writer?.Release(); } catch { }
            _writer?.Dispose();
            _writer = null;
        }

        CleanupAudioDevices();
        try { _audioWriter?.Flush(); } catch { }
        try { _audioWriter?.Dispose(); } catch { }
        _audioWriter = null;

        try
        {
            if (_discardOutput)
                return;

            if (_tempVideoPath == null || !File.Exists(_tempVideoPath))
                throw new InvalidOperationException("未生成视频文件。");

            var hasAudio = _tempAudioPath != null && File.Exists(_tempAudioPath) &&
                           new FileInfo(_tempAudioPath).Length > 44;

            if (ffmpeg != null)
            {
                string args;
                if (hasAudio)
                {
                    args =
                        $"-y -i \"{_tempVideoPath}\" -i \"{_tempAudioPath}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -b:a 192k -shortest \"{outputPath}\"";
                }
                else
                {
                    args =
                        $"-y -i \"{_tempVideoPath}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -an \"{outputPath}\"";
                }

                if (!RunFfmpeg(ffmpeg, args) || !File.Exists(outputPath))
                {
                    // 回退：无音轨时直接无法用 avi 当 mp4；尽量再试 copy
                    if (!hasAudio)
                    {
                        var fallback =
                            $"-y -i \"{_tempVideoPath}\" -c:v copy \"{outputPath}\"";
                        if (!RunFfmpeg(ffmpeg, fallback) || !File.Exists(outputPath))
                            throw new InvalidOperationException("FFmpeg 转码失败。");
                    }
                    else
                    {
                        var videoOnly =
                            $"-y -i \"{_tempVideoPath}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -an \"{outputPath}\"";
                        if (!RunFfmpeg(ffmpeg, videoOnly) || !File.Exists(outputPath))
                            throw new InvalidOperationException("FFmpeg 转码失败。");
                        EmitWarning("音视频合成失败，已保存无音轨画面。");
                    }
                }
            }
            else
            {
                // 无 FFmpeg：纯画面直接拷贝（mp4v）；若是 avi 再尝试转写
                if (_tempVideoPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    File.Copy(_tempVideoPath, outputPath, true);
                else if (!TryRewriteAsMp4v(outputPath))
                {
                    var dest = Path.ChangeExtension(outputPath, ".avi");
                    File.Copy(_tempVideoPath, dest, true);
                    EmitWarning($"未找到 FFmpeg，已保存为 AVI：{dest}");
                    try { File.Copy(_tempVideoPath, outputPath, true); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            EmitWarning($"保存录屏失败：{ex.Message}");
        }
        finally
        {
            try { if (_tempVideoPath != null) File.Delete(_tempVideoPath); } catch { }
            try { if (_tempAudioPath != null) File.Delete(_tempAudioPath); } catch { }
            _tempVideoPath = null;
            _tempAudioPath = null;
        }
    }

    private bool TryRewriteAsMp4v(string outputPath)
    {
        try
        {
            if (_tempVideoPath == null || !File.Exists(_tempVideoPath)) return false;
            using var capture = new VideoCapture(_tempVideoPath);
            if (!capture.IsOpened()) return false;
            var w = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            var h = (int)capture.Get(VideoCaptureProperties.FrameHeight);
            var fps = capture.Get(VideoCaptureProperties.Fps);
            if (fps < 1) fps = 24;
            using var writer = new VideoWriter(outputPath, FourCC.FromString("mp4v"), fps, new OpenCvSharp.Size(w, h));
            if (!writer.IsOpened()) return false;
            using var frame = new Mat();
            while (capture.Read(frame))
            {
                if (frame.Empty()) break;
                writer.Write(frame);
            }
            writer.Release();
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunFfmpeg(string ffmpeg, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(180000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void EmitWarning(string msg)
    {
        _lastWarning = msg;
        Warning?.Invoke(msg);
    }

    private void CleanupAudioDevices()
    {
        try { _audioPump?.Dispose(); } catch { }
        _audioPump = null;
        try { _micCapture?.StopRecording(); _micCapture?.Dispose(); } catch { }
        _micCapture = null;
        try { _loopCapture?.StopRecording(); _loopCapture?.Dispose(); } catch { }
        _loopCapture = null;
    }

    public Exception? TakeFatalError()
    {
        var e = _fatalError;
        _fatalError = null;
        return e;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsRecording) Stop();
        CleanupAudioDevices();
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>在 UI 线程执行（录屏合成依赖 WPF 渲染）。</summary>
public delegate void DispatcherInvoker(Action action);
