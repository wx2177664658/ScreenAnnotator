using OpenCvSharp;

// CR-014 最小自测：确认 OpenCv 能写出可播的 MP4（mp4v）
var outPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "sa_selftest_record.mp4");
const int w = 640, h = 360, fps = 24, frames = 48;
using var writer = new VideoWriter(outPath, FourCC.FromString("mp4v"), fps, new Size(w, h));
if (!writer.IsOpened())
{
    Console.Error.WriteLine("FAIL: VideoWriter not opened");
    return 1;
}

for (var i = 0; i < frames; i++)
{
    using var mat = new Mat(h, w, MatType.CV_8UC3, new Scalar(255, 255, 255));
    // 模拟「标注」：红线随帧移动
    var x = 40 + i * 8;
    Cv2.Line(mat, new Point(40, 80), new Point(x, 200), new Scalar(0, 0, 255), 4);
    Cv2.PutText(mat, $"frame {i}", new Point(20, 40), HersheyFonts.HersheySimplex, 1, new Scalar(0, 128, 0), 2);
    writer.Write(mat);
}
writer.Release();

var fi = new FileInfo(outPath);
if (!fi.Exists || fi.Length < 1000)
{
    Console.Error.WriteLine($"FAIL: output missing or too small: {outPath}");
    return 2;
}
Console.WriteLine($"OK: {outPath} ({fi.Length} bytes)");
return 0;
