using System.Text;
using System.Text.Json;

namespace QPixLib;

public struct QPixEncoderStats
{
    public int totalFrames;
    public int mappingFrames;

    public int deltaFramesCount;
    public int fullFramesCount;
    public int skippedFramesCount;
}

public class QPixEncoder : IDisposable
{
    public const uint QPIX_VERSION = 1;
    public const uint QPIX_MAX_VIDEO_DATA_SIZE = 1024 * 1024 * 1024; // 1 GiB

    // Resolution must be limited, since pixel indexes are stored as UInt16.
    // 255 * 255 = 65025 is within the UInt16 range (0 - 65535).
    public const uint QPIX_MAX_RESOLUTION = 255;

    // The writer generates a map that links frame indexes to byte offsets,
    // so that the video player can more quickly seek towards a specific frame.
    // This variable sets how frequently these "mapping frames" should be added.
    private const uint MAPPING_INTERVAL = 20;

    // If only delta frames are written consecutively, the writer will force a full frame
    // after this amount of seconds has passed (in video time) since the last full frame.
    private const double FORCED_FULL_FRAME_INTERVAL = 5.0;

    private static readonly Dictionary<QPixFormat, QPixQuantizer> _formatQuantizers = new() {
        {QPixFormat.GRAYSCALE, new GrayscaleQuantizer()},
        {QPixFormat.RGB888, new RGB888Quantizer()},
        {QPixFormat.PACKED_RGB332, new PackedRGB332Quantizer()},
        {QPixFormat.PACKED_RG44_B4, new PackedRG44_B4Quantizer()},
    };

    private QPixMetadata _metadata;
    private readonly QPixQuantizer _quantizer;

    private readonly uint _framePixelCount;
    private readonly uint _pixelSizeBytes;
    private readonly uint _fullFrameSizeBytes;

    private readonly bool[] _changedPixels;
    private readonly byte[][] _lastQuantizedFrame;
    private readonly byte[][] _currentQuantizedFrame;

    private List<long> _mappingFrames = [];
    private double _lastFullFrameTime = 0.0;

    private readonly MemoryStream _stream;
    private QPixEncoderStats _stats;
    private byte[]? _audioData;
    private bool _disposedValue;

    public QPixEncoder(uint width, uint height, uint fps, QPixFormat format)
        : this(width, height, fps, _formatQuantizers[format])
    {
    }

    public QPixEncoder(uint width, uint height, uint fps, QPixQuantizer quantizer)
    {
        if (fps < 1 || fps > 127) throw new ArgumentOutOfRangeException("fps", "FPS must be >= 1 and <= 127");

        if (width < 1 || width > QPIX_MAX_RESOLUTION)
            throw new ArgumentOutOfRangeException("width", $"Width must be >= 1 and <= {QPIX_MAX_RESOLUTION}");

        if (height < 1 || height > QPIX_MAX_RESOLUTION)
            throw new ArgumentOutOfRangeException("height", $"Height must be >= 1 and <= {QPIX_MAX_RESOLUTION}");

        _metadata = new QPixMetadata
        {
            Version = QPIX_VERSION,
            Width = width,
            Height = height,
            FPS = fps,
            ColorFormat = quantizer.FormatName,
            MappingInterval = MAPPING_INTERVAL
        };

        _quantizer = quantizer;
        _framePixelCount = width * height;

        // Store how big one pixel is (in bytes) with the provided quantizer
        _pixelSizeBytes = quantizer.PixelSizeBytes;

        // Store how big a full frame of sequential pixels is (in bytes)
        _fullFrameSizeBytes = _pixelSizeBytes * _framePixelCount;

        // Prepare arrays for pixel quantization and frame comparisons
        _changedPixels = new bool[_framePixelCount];
        _lastQuantizedFrame = new byte[_framePixelCount][];
        _currentQuantizedFrame = new byte[_framePixelCount][];

        for (int pixelIndex = 0; pixelIndex < _framePixelCount; pixelIndex++)
        {
            _lastQuantizedFrame[pixelIndex] = new byte[_pixelSizeBytes];
            _currentQuantizedFrame[pixelIndex] = new byte[_pixelSizeBytes];
        }
    
        // Create the video data buffer, starting with 32 MB of data.
        // Not all of that will be used for short/small videos,
        // and MemoryStream can grow the buffer for longer/larger videos.
        _stream = new MemoryStream(1024 * 1024 * 32);

        // Statistics tracker
        _stats = new QPixEncoderStats();

        ClearFrames();
    }

    protected virtual void Dispose(bool itIsSafeToAlsoFreeManagedObjects)
    {
        if (!_disposedValue)
        {
            if (itIsSafeToAlsoFreeManagedObjects)
                _stream.Dispose();

            _disposedValue = true;
        }
    }

    public void Dispose() => Dispose(true);

    public override string ToString()
    {
        return $"QPixVideo <{_metadata.Width}x{_metadata.Height}, {_metadata.FrameCount} frames @ {_metadata.FPS} FPS, color format: {_metadata.ColorFormat})>";
    }

    public void ClearFrames()
    {
        _stream.Seek(0, SeekOrigin.Begin);

        _metadata.FrameCount = 0;
        _mappingFrames = [];
        _lastFullFrameTime = 0.0;

        for (int pixelIndex = 0; pixelIndex < _framePixelCount; pixelIndex++)
        {
            _changedPixels[pixelIndex] = false;

            for (int byteIndex = 0; byteIndex < _pixelSizeBytes; byteIndex++)
                _lastQuantizedFrame[pixelIndex][byteIndex] = 0;
        }

        _stats.totalFrames = 0;
        _stats.mappingFrames = 0;

        _stats.deltaFramesCount = 0;
        _stats.fullFramesCount = 0;
        _stats.skippedFramesCount = 0;
    }

    public QPixEncoderStats GetStats() => _stats;
    public uint GetWidth() => _metadata.Width;
    public uint GetHeight() => _metadata.Height;
    public uint GetFPS() => _metadata.FPS;
    public uint GetFrameCount() => _metadata.FrameCount;
    public uint GetFramePixelCount() => _framePixelCount;
    public long GetVideoSizeBytes() => _stream.Position;

    public void SetTitle(string title) => _metadata.Title = title;
    public void SetAudioData(byte[] audioData) => _audioData = audioData;

    public void WriteFrame(QPixRawColor[] pixels)
    {
        if (pixels.Length != _framePixelCount)
            throw new ArgumentOutOfRangeException("pixels", "The amount of pixels provided does not match the video resolution");
        
        var pointer = _stream.Position;

        if (pointer >= QPIX_MAX_VIDEO_DATA_SIZE)
            throw new OverflowException("Video data has reached the size limit");
        
        // Check if this frame should be mapped.
        // Creates a entry every MAPPING_INTERVAL frames.
        if (_metadata.FrameCount % _metadata.MappingInterval == 0)
        {
            _mappingFrames.Add(pointer);
            _stats.mappingFrames++;
        }

        // Quantize all pixels
        uint changedPixelCount = 0;

        for (int pixelIndex = 0; pixelIndex < _framePixelCount; pixelIndex++)
        {
            // Reset the changed pixel flag
            _changedPixels[pixelIndex] = false;
    
            var lastBytes = _lastQuantizedFrame[pixelIndex];
            var newBytes = _currentQuantizedFrame[pixelIndex];

            _quantizer.Quantize(ref pixels[pixelIndex], ref newBytes);

            // Check if any of the new bytes have changed
            // compared to the bytes from the last frame.
            if (!newBytes.SequenceEqual(lastBytes))
            {
                // Store the new frame pixel bytes to compare next frame
                for (int byteIndex = 0; byteIndex < _pixelSizeBytes; byteIndex++)
                    lastBytes[byteIndex] = newBytes[byteIndex];

                // Mark this pixel as changed since last frame
                _changedPixels[pixelIndex] = true;
                changedPixelCount++;
            }
        }

        double videoTime = (1.0 / _metadata.FPS) * _metadata.FrameCount;

        // Calculate how big a delta frame is, in bytes
        uint deltaFrameSizeBytes = (2 + _pixelSizeBytes) * changedPixelCount;

        // If writing all quantized bytes in sequence saves space
        // compared to writing only the changed pixels (which uses more memory
        // due to their index), write the full frame pixels in sequence now.
        //
        // Additionally, always write a full frame if too much time has passed
        // since the last time a full frame was written.
        if (deltaFrameSizeBytes > _fullFrameSizeBytes || videoTime > _lastFullFrameTime + FORCED_FULL_FRAME_INTERVAL)
        {
            _stats.fullFramesCount++;
            _lastFullFrameTime = videoTime;

            // Write the frame header
            _stream.WriteByte((byte)QPixFrameMode.FULL);

            for (uint pixelIndex = 0; pixelIndex < _framePixelCount; pixelIndex++)
            {
                // Write pixel quantized color bytes
                var bytes = _currentQuantizedFrame[pixelIndex];

                for (uint b = 0; b < _pixelSizeBytes; b++)
                    _stream.WriteByte(bytes[b]);
            }
        }
        else if (changedPixelCount > 0)
        {
            _stats.deltaFramesCount++;

            // Write the frame header
            _stream.WriteByte((byte)QPixFrameMode.DELTA);

            // Write changed pixel count
            BigEndianUtils.WriteUInt32(_stream, changedPixelCount);

            for (ushort pixelIndex = 0; pixelIndex < _framePixelCount; pixelIndex++)
            {
                if (_changedPixels[pixelIndex])
                {
                    // Write pixel index
                    BigEndianUtils.WriteUInt16(_stream, pixelIndex);

                    // Write pixel color
                    var bytes = _currentQuantizedFrame[pixelIndex];

                    for (int b = 0; b < _pixelSizeBytes; b++)
                        _stream.WriteByte(bytes[b]);
                }
            }
        }
        else
        {
            _stats.skippedFramesCount++;

            // Write the frame header
            _stream.WriteByte((byte)QPixFrameMode.SKIP);
        }

        // This HAS to come after the mapping frame check
        // and the logic to calculate `videoTime`.
        _metadata.FrameCount++;
        _stats.totalFrames++;
    }

    public byte[] GetFinalBufferCopy()
    {
        // Serialize the metadata as JSON
        var metadataJson = JsonSerializer.Serialize(_metadata, QPixMetadataContext.Default.QPixMetadata);
        var metadataBuffer = Encoding.ASCII.GetBytes(metadataJson);
        var metadataSizeBytes = (uint)metadataBuffer.Length;

        var videoSizeBytes = (uint)GetVideoSizeBytes();
        var audioSizeBytes = _audioData == null ? 0 : (uint)_audioData.Length;

        // Calculate the keyframes data size
        // frame byte offset (UInt32) * keyframe count
        var mappingFramesSizeBytes = 4u * (uint)_mappingFrames.Count;

        uint totalFinalSizeBytes =
            4u +                            // Magic code
            4u + metadataSizeBytes +        // metadata length + metadata
            4u + mappingFramesSizeBytes +   // mapping frames data length + mapping frames data
            4u + audioSizeBytes +           // audio data length + audio data
            videoSizeBytes;                 // video data

        using var finalStream = new MemoryStream((int)totalFinalSizeBytes);

        // Write the magic code (4 bytes)
        finalStream.WriteByte(81); // Q
        finalStream.WriteByte(80); // P
        finalStream.WriteByte(73); // I
        finalStream.WriteByte(88); // X

        // Write the metadata length (4 bytes)
        BigEndianUtils.WriteUInt32(finalStream, metadataSizeBytes);

        // Write the metadata
        finalStream.Write(metadataBuffer);

        // Write the mapping frames data length (4 bytes)
        BigEndianUtils.WriteUInt32(finalStream, mappingFramesSizeBytes);

        // Write mapping frames data
        for (int i = 0; i < _mappingFrames.Count; i++)
            BigEndianUtils.WriteUInt32(finalStream, (uint)_mappingFrames[i]);

        // Write the audio data length (4 bytes)
        BigEndianUtils.WriteUInt32(finalStream, audioSizeBytes);

        // Write the audio data (if set)
        if (_audioData != null)
            finalStream.Write(_audioData);

        // Write the video data
        _stream.Position = 0;
        _stream.CopyTo(finalStream);

        return finalStream.GetBuffer();
    }
}
