using QPixLib;

namespace QPixConverter;

public class ConvertJobOptions
{
    public required string InputFilePath { get; set; }
    public required string OutputFilePath { get; set; }
    public required int MaxWidth { get; set; }
    public required int MaxHeight { get; set; }
    public required int MaxFPS { get; set; }
    public required QPixFormat Format { get; set; }
    public required bool EnableAudio { get; set; }
    public string? Title { get; set; }

    // TODO: Add crop options
}

public class ConvertJobProgress
{
    public required uint ProcessedFrameCount { get; set; }
    public required uint TotalFrameCount { get; set; }
    public required long VideoSizeBytes { get; set; }
    public required long TotalSizeBytes { get; set; }
}

public enum ConvertJobState
{
    IDLE,
    CONVERTING,
    COMPLETED,
    ERRORED
}

public class ConvertJob
{
    private readonly Logger _logger = new("ConvertJob");

    public delegate void StateChangedEventHandler(ConvertJob sender, ConvertJobState state);
    public delegate void ProgressChangedEventHandler(ConvertJob sender, ConvertJobProgress progress);
    public delegate void ErrorEventHandler(ConvertJob sender, string message);

    public event StateChangedEventHandler? OnStateChanged;
    public event ErrorEventHandler? OnError;

    public ConvertJobOptions Options { get; private set; }
    public ConvertJobState State { get; private set; }
    public ConvertJobProgress Progress { get; private set; }

    private CancellationTokenSource? _cts;
    private Task<VideoMetadata>? _videoMetadataTask;
    private Task? _videoStreamTask;
    private Task<byte[]>? _audioDataTask;

    public ConvertJob(ConvertJobOptions options)
    {
        Options = options;
        State = ConvertJobState.IDLE;
        Progress = new()
        {
            TotalFrameCount = 0,
            ProcessedFrameCount = 0,
            VideoSizeBytes = 0,
            TotalSizeBytes = 0
        };
    }

    public async Task Start()
    {
        if (State == ConvertJobState.CONVERTING)
            throw new InvalidOperationException("Task is running already");

        State = ConvertJobState.CONVERTING;

        Progress.TotalFrameCount = 0;
        Progress.ProcessedFrameCount = 0;
        Progress.VideoSizeBytes = 0;
        Progress.TotalSizeBytes = 0;

        OnStateChanged?.Invoke(this, State);

        _cts = new CancellationTokenSource();

        try
        {
            // Get source video file metadata
            _videoMetadataTask = FFmpeg.GetVideoMetadataAsync(Options.InputFilePath);
            await _videoMetadataTask.WaitAsync(_cts.Token);
            var sourceVideoMetadata = _videoMetadataTask.Result;

            // Limit source video resolution/framerate
            var (newWidth, newHeight) = Utils.FitDimensions(sourceVideoMetadata.Width, sourceVideoMetadata.Height, Options.MaxWidth, Options.MaxHeight);
            var videoOptions = new VideoOptions
            {
                FPS = Math.Min(sourceVideoMetadata.FrameRate, Options.MaxFPS),
                Width = newWidth,
                Height = newHeight
            };

            // Calculate the expected output frame count based on
            // the FPS and how many frames the original video has.
            Progress.TotalFrameCount = (uint)Math.Floor((sourceVideoMetadata.FrameCount / sourceVideoMetadata.FrameRate) * videoOptions.FPS);

            _logger.Info($"Expected output metadata: {videoOptions.Width}x{videoOptions.Height} @ {videoOptions.FPS} ({Progress.TotalFrameCount} frames)");

            // Run the encoder
            using var encoder = new QPixEncoder((uint)videoOptions.Width, (uint)videoOptions.Height, (uint)videoOptions.FPS, Options.Format);
            var videoStream = new FFmpegRawVideoStream(Options.InputFilePath, videoOptions);
            var tempFrameData = new QPixRawColor[encoder.GetFramePixelCount()];

            videoStream.SetFrameCallback((rgbBuffer, width, height) =>
            {
                for (int pixelIndex = 0, byteOffset = 0; pixelIndex < tempFrameData.Length; pixelIndex++, byteOffset += 3)
                {
                    tempFrameData[pixelIndex].r = rgbBuffer[byteOffset] / 255f;
                    tempFrameData[pixelIndex].g = rgbBuffer[byteOffset + 1] / 255f;
                    tempFrameData[pixelIndex].b = rgbBuffer[byteOffset + 2] / 255f;
                }

                encoder.WriteFrame(tempFrameData);

                Progress.VideoSizeBytes = encoder.GetVideoSizeBytes();
                Progress.ProcessedFrameCount = encoder.GetFrameCount();
            });

            _videoStreamTask = videoStream.StartAsync();
            await _videoStreamTask.WaitAsync(_cts.Token);

            // Print the final metadata
            var stats = encoder.GetStats();

            _logger.Debug(encoder.ToString());
            _logger.Debug($"framesTotal: {stats.totalFrames} mappingFrames: {stats.mappingFrames} deltaFramesCount: {stats.deltaFramesCount} fullFramesCount: {stats.fullFramesCount} skippedFramesCount: {stats.skippedFramesCount}");

            // Add audio data
            if (Options.EnableAudio)
            {
                try
                {
                    _audioDataTask = FFmpeg.ConvertToOGGAudioAsync(Options.InputFilePath);
                    await _audioDataTask.WaitAsync(_cts.Token);
                    encoder.SetAudioData(_audioDataTask.Result);
                }
                catch(Exception e)
                {
                    _logger.Warn($"Ignoring audio as it could not be converted: {e.Message}");
                }
            }

            // Get the final encoder result data
            if (Options.Title != null)
                encoder.SetTitle(Options.Title);

            var qpixData = encoder.GetFinalBufferCopy();
            _logger.Debug($"qpixData.Length: {qpixData.Length}");

            using var file = File.Open(Options.OutputFilePath, FileMode.Create);
            file.Write(qpixData);
            file.Close();

            Progress.TotalSizeBytes = qpixData.Length;
            _logger.Success($"Written {Utils.HumanFileSize(Progress.TotalSizeBytes)} to: {Options.OutputFilePath}");

            // This job is done
            State = ConvertJobState.COMPLETED;
            OnStateChanged?.Invoke(this, State);
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Task cancelled.");

            State = ConvertJobState.IDLE;
            OnStateChanged?.Invoke(this, State);
        }
        catch (Exception ex)
        {
            _logger.Error(ex.Message);

            State = ConvertJobState.ERRORED;
            OnStateChanged?.Invoke(this, State);
            OnError?.Invoke(this, ex.Message);
        }
        finally
        {
            if (_videoMetadataTask != null && (_videoMetadataTask.IsCompleted || _videoMetadataTask.IsCanceled))
                _videoMetadataTask.Dispose();

            if (_videoStreamTask != null && (_videoStreamTask.IsCompleted || _videoStreamTask.IsCanceled))
                _videoStreamTask.Dispose();

            if (_audioDataTask != null && (_audioDataTask.IsCompleted || _audioDataTask.IsCanceled))
                _audioDataTask.Dispose();

            _videoMetadataTask = null;
            _videoStreamTask = null;
            _audioDataTask = null;

            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Stop()
    {
        if (State != ConvertJobState.CONVERTING)
        {
            _logger.Warn("Task is stopped already");
            return;
        }

        _logger.Info("Stopping task...");
        _cts?.Cancel();
    }
}
