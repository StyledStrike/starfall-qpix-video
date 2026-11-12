using System.Text.Json;
using System.Diagnostics;

namespace QPixConverter;

public class CropOptions
{
	public int Left { get; set; }
	public int Right { get; set; }
	public int Top { get; set; }
	public int Bottom { get; set; }
}

public class VideoOptions
{
	public double FPS { get; set; }
	public int Width { get; set; }
	public int Height { get; set; }
	public double? TimeTrimStart { get; set; }
	public double? TimeTrimEnd { get; set; }
	public CropOptions? Crop { get; set; }
}

public class AudioOptions
{
	public int? Quality { get; set; }
	public double? TimeTrimStart { get; set; }
	public double? TimeTrimEnd { get; set; }
}

public class VideoMetadata
{
	public int Width { get; set; }
	public int Height { get; set; }
	public int FrameCount { get; set; }
	public double FrameRate { get; set; }
}

public static class FFmpeg
{
	private static readonly Logger _logger = new("FFmpeg");

	private static async Task<(bool success, string stdout, string stderr)> RunCommandAsync(string fileName, IEnumerable<string> args)
	{
		var psi = new ProcessStartInfo
		{
			FileName = fileName,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		foreach (var arg in args)
			psi.ArgumentList.Add(arg);

		using var process = new Process { StartInfo = psi };
		process.Start();

		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();

		await process.WaitForExitAsync();

		return (process.ExitCode == 0, await stdoutTask, await stderrTask);
	}

	private static async Task<(bool success, byte[] stdoutBytes, string stderr)> RunCommandRawAsync(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var stream = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(stream);
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(copyTask, process.WaitForExitAsync());

        return (process.ExitCode == 0, stream.ToArray(), await stderrTask);
    }

	private static async Task<int> GetPacketCountAsync(string filePath)
	{
		var args = new[]
		{
			"-v", "error",
			"-select_streams", "v:0",
			"-count_packets",
			"-show_entries", "stream=nb_read_packets",
			"-of", "default=nokey=1:noprint_wrappers=1",
			filePath
		};

		var (success, stdout, stderr) = await RunCommandAsync("ffprobe", args);

		if (!success)
		{
			_logger.Warn($"Could not count packets from {filePath}: {stderr}");
			return 0;
		}

		return int.TryParse(stdout.Trim(), out int count) ? count : 0;
	}

	public static async Task<byte[]> ConvertToOGGAudioAsync(string sourcePath, AudioOptions? options = null)
    {
        options ??= new AudioOptions();

        var args = new List<string> { "-i", sourcePath };

        if (options.TimeTrimStart != null)
        {
            args.Add("-ss");
            args.Add(options.TimeTrimStart.Value.ToString());
        }

        if (options.TimeTrimEnd != null)
        {
            args.Add("-to");
            args.Add(options.TimeTrimEnd.Value.ToString());
        }

		args.AddRange(["-vn", "-acodec", "libvorbis"]);

        if (options.Quality != null)
        {
            args.Add("-q:a");
            args.Add(Math.Floor((double)options.Quality.Value).ToString());
        }

        args.AddRange(["-f", "ogg", "pipe:1"]);

        var (success, stdoutBytes, stderr) = await RunCommandRawAsync("ffmpeg", [.. args]);

        if (!success)
            throw new Exception($"ffmpeg exited with error: {stderr}");

        return stdoutBytes;
    }

	public static async Task<VideoMetadata> GetVideoMetadataAsync(string filePath)
	{
		var args = new[]
		{
			"-v", "error",
			"-select_streams", "v:0",
			"-show_entries", "stream=width,height,r_frame_rate,nb_frames:stream_tags=rotate",
			"-of", "json",
			filePath
		};

		var (success, stdout, stderr) = await RunCommandAsync("ffprobe", args);

		if (!success)
			throw new Exception($"ffprobe failed to read file: {stderr}");

		using var doc = JsonDocument.Parse(stdout);
		var root = doc.RootElement;

		if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
			throw new Exception("No video stream found in the specified file.");

		var stream = streams[0];

		// Try to get resolution from the container
		int width = stream.TryGetProperty("width", out var wElem) ? wElem.GetInt32() : 0;
		int height = stream.TryGetProperty("height", out var hElem) ? hElem.GetInt32() : 0;

		// Try to get framerate and frame count from the container
		double frameRate = 0;
		int frameCount = 0;

		if (stream.TryGetProperty("nb_frames", out var nbElem) && int.TryParse(nbElem.GetString(), out int nbFrames))
        {
            frameCount = nbFrames;
            _logger.Debug("Got frame count  from nb_frames");
        }

		if (stream.TryGetProperty("r_frame_rate", out var rElem))
        {
            var parts = rElem.GetString()?.Split('/');

            if (parts?.Length == 2 &&
                double.TryParse(parts[0], out double num) &&
                double.TryParse(parts[1], out double den) &&
                den != 0)
            {
                frameRate = num / den;
                _logger.Debug("Got framerate metadata from r_frame_rate");
            }
        }

		// Try to get duration metadata from the container
		double duration = 0;

		if (root.TryGetProperty("format", out var formatElem) &&
			formatElem.TryGetProperty("duration", out var durElem) &&
			double.TryParse(durElem.GetString(), out double parsedDuration))
		{
			duration = parsedDuration;
		}

        // If nb_frames is missing, estimate the frame count with the duration * frame rate
		if (frameCount == 0 && duration > 0 && frameRate > 0)
        {
            frameCount = (int)Math.Round(duration * frameRate);
            _logger.Debug("Got frame count from duration * frameRate");
        }
    
        // If nb_frames, duration and frame rate is missing, use packet count (less accurate)
        if (frameCount == 0)
        {
            frameCount = await GetPacketCountAsync(filePath);
            _logger.Debug("Got frame count from GetPacketCountAsync");
        }

        return new VideoMetadata
        {
            Width = width,
            Height = height,
            FrameCount = frameCount,
            FrameRate = frameRate
        };
	}
}
