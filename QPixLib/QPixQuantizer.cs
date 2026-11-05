namespace QPixLib;

public enum QPixFormat
{
    GRAYSCALE,
    RGB888,
    PACKED_RGB332,
    PACKED_RG44_B4,
}

public struct QPixRawColor
{
    public float r;
    public float g;
    public float b;
}

public abstract class QPixQuantizer
{
    public abstract string FormatName { get; }

    // How many bytes each quantized pixel uses in this format.
    public abstract uint PixelSizeBytes { get; }

    // The `output`'s length will be the same as `PixelSizeBytes`. 
    public abstract void Quantize(ref QPixRawColor inColor, ref byte[] outBytes);
}

/*
    Grayscale, each shade is stored in 1 byte as a grayscale value (0-255)
*/
class GrayscaleQuantizer : QPixQuantizer
{
    public override string FormatName => "GRAYSCALE";
    public override uint PixelSizeBytes => 1;

    public override void Quantize(ref QPixRawColor inColor, ref byte[] outBytes)
    {
        // Convert pixel color to grayscale using the luminosity method
        var grayscale = 0.2989f * inColor.r + 0.587f * inColor.g + 0.114f * inColor.b;

        // One byte per pixel
        outBytes[0] = (byte)MathF.Floor(255f - (grayscale * 254f));
    }
}

/*
    RGB color, stored in three bytes as `RRRRRRRR`, `GGGGGGGG` and `BBBBBBBB`
*/
class RGB888Quantizer : QPixQuantizer
{
    public override string FormatName => "RGB888";
    public override uint PixelSizeBytes => 3;

    public override void Quantize(ref QPixRawColor inColor, ref byte[] outBytes)
    {
        // 3 bytes per pixel
        outBytes[0] = (byte)MathF.Floor(inColor.r * 255f);
        outBytes[1] = (byte)MathF.Floor(inColor.g * 255f);
        outBytes[2] = (byte)MathF.Floor(inColor.b * 255f);
    }
}

/*
    RGB color, stored in a byte as `RRRGGGBB` (3 bits for red, 3 bits for green, 2 bits for blue)
*/
class PackedRGB332Quantizer : QPixQuantizer
{
    public override string FormatName => "PACKED_RGB332";
    public override uint PixelSizeBytes => 1;

    public override void Quantize(ref QPixRawColor inColor, ref byte[] outBytes)
    {
        var rByte = (byte)MathF.Floor(inColor.r * 7f);
        var gByte = (byte)MathF.Floor(inColor.g * 7f);

        // Blue should be * 3, but it's slightly higher to make it brigher, to compensate
        // for the lower bits used to store blue. `Floor` should still keep it on the 0-3 (2 bits) range.
        var bByte = (byte)MathF.Floor(inColor.b * 3.2f);

        // One byte per pixel
        outBytes[0] = (byte)(bByte | (gByte << 2) | (rByte << 5));
    }
}

/*
    RGB color, stored in two bytes as `RRRRGGGG` and `0000BBBB` (4 bits for red, 4 bits for green, 4 bits for blue)
*/
class PackedRG44_B4Quantizer : QPixQuantizer
{
    public override string FormatName => "PACKED_RG44_B4";
    public override uint PixelSizeBytes => 2;

    public override void Quantize(ref QPixRawColor inColor, ref byte[] outBytes)
    {
        var rByte = (byte)MathF.Floor(inColor.r * 15f);
        var gByte = (byte)MathF.Floor(inColor.g * 15f);
        var bByte = (byte)MathF.Floor(inColor.b * 15f);

        // 2 bytes per pixel
        outBytes[0] = (byte)(gByte | (rByte << 4));
        outBytes[1] = bByte;
    }
}
