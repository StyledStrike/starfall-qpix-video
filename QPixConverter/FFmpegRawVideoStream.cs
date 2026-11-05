using System.Diagnostics;

namespace QPixConverter;

public delegate void RawFrameCallback(byte[] rgbBuffer, int width, int height);

public class FFmpegRawVideoStream(string filePath, VideoOptions options)
{
    private readonly Logger _logger = new("FFmpegRawVideoStream");
    private readonly string _filePath = filePath;
    private readonly VideoOptions _options = options;

    private Process? _process;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    private RawFrameCallback? _onRawFrame;

    public void SetFrameCallback(RawFrameCallback callback)
    {
        _onRawFrame = callback;
    }

    private List<string> BuildArguments(string? audioOutBackend = null, string? audioOutDevice = null)
    {
        var args = new List<string>();
        var isAudioDeviceSet = !string.IsNullOrEmpty(audioOutBackend) && !string.IsNullOrEmpty(audioOutDevice);

        if (isAudioDeviceSet)
            args.Add("-re"); // Real-time decoding

        if (_options.TimeTrimStart.HasValue)
        {
            args.Add("-ss");
            args.Add(_options.TimeTrimStart.Value.ToString());
        }

        if (_options.TimeTrimEnd.HasValue)
        {
            args.Add("-to");
            args.Add(_options.TimeTrimEnd.Value.ToString());
        }

        args.AddRange(["-i", _filePath, "-map", "0:v"]);

        if (_options.FPS > 0)
        {
            args.Add("-r");
            args.Add(Math.Floor(_options.FPS).ToString());
        }

        var vfOptions = new List<string>();

        if (_options.Crop != null)
        {
            var crop = _options.Crop;
            vfOptions.Add($"crop=out_w=in_w-{crop.Right}:out_h=in_h-{crop.Bottom}:x={crop.Left}:y={crop.Top}");
        }

        vfOptions.Add($"scale={_options.Width}:{_options.Height}");

        if (vfOptions.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", vfOptions));
        }

        args.AddRange([
            "-f", "rawvideo",
            "-pix_fmt", "rgb24", // 24-bit RGB (3 bytes per pixel)
            "pipe:1" // Direct to stdout
        ]);

        if (isAudioDeviceSet)
        {
            args.AddRange([
                "-map", "0:a?", // '?' makes audio optional, if video has no audio track
                "-f", audioOutBackend ?? "",
                audioOutDevice ?? ""
            ]);
        }

        return args;
    }

    public async Task StartAsync(string? audioOutBackend = null, string? audioOutDevice = null)
    {
        if (_isRunning)
        {
            _logger.Warn("Stream is running already!");
            return;
        }

        var ffmpegArgs = BuildArguments(audioOutBackend, audioOutDevice);
        _logger.Info("Arguments: " + Logger.Highlight(string.Join(" ", ffmpegArgs)));

        _cts = new CancellationTokenSource();
        
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in ffmpegArgs)
            psi.ArgumentList.Add(arg);

        _process = new Process { StartInfo = psi };
        _process.Start();
        _isRunning = true;

        int width = _options.Width;
        int height = _options.Height;
        int frameSizeBytes = width * height * 3;

        try
        {
            using var stdout = _process.StandardOutput.BaseStream;
            var buffer = new byte[8192];
            var frameBuffer = new List<byte>();
            int bytesRead;

            while ((bytesRead = await stdout.ReadAsync(buffer, 0, buffer.Length, _cts.Token)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                    frameBuffer.Add(buffer[i]);

                while (frameBuffer.Count >= frameSizeBytes)
                {
                    byte[] frameData = [.. frameBuffer.GetRange(0, frameSizeBytes)];
                    frameBuffer.RemoveRange(0, frameSizeBytes);

                    _onRawFrame?.Invoke(frameData, width, height);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stream is stopped manually
        }
        catch (Exception ex)
        {
            _logger.Error($"Stream error: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            _isRunning = false;

            _process?.Dispose();
            _process = null;

            _cts?.Dispose();
            _cts = null;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            _logger.Warn("Stream is not running!");
            return;
        }

        _logger.Info("Stopping FFMPEG stream...");
        _isRunning = false;

        _cts?.Cancel();

        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill();
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Process may have exited naturally
            }
        }

        _logger.Info("FFMPEG stream stopped.");
    }
}
