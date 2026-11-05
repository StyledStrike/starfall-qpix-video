using QPixLib;

namespace QPixConverter;

public static class Utils
{
    public static int RoundUpToPowerOfTwo(double num)
    {
        num = Math.Floor(num);
        if (num <= 0) return 0;

        var exponent = Math.Ceiling(Math.Log2(num));
        return (int)Math.Pow(2d, exponent);
    }

    public static int RoundUpToMultipleOfFour(double num)
    {
        return (int)Math.Ceiling(num / 4d) * 4;
    }

    public static (int width, int height) FitDimensions(int originalWidth, int originalHeight, int maxWidth, int maxHeight)
    {
        double widthRatio = (double)maxWidth / originalWidth;
        double heightRatio = (double)maxHeight / originalHeight;
        double scaleFactor = Math.Min(widthRatio, heightRatio);

        int newWidth = (int)Math.Round(originalWidth * scaleFactor);
        int newHeight = (int)Math.Round(originalHeight * scaleFactor);

        return (newWidth, newHeight);
    }

    private static readonly string[] SI_NAMES = ["B", "kB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB"];
    private static readonly string[] IEC_NAMES = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB"];

    /*
        Format bytes as human-readable text.

        Source - https://stackoverflow.com/a/4975942
        Posted by deepee1, modified by community.
        Retrieved 2026-10-31, License - CC BY-SA 4.0
    */
    public static string HumanFileSize(long bytes, bool useSI = false, int decimalPlaces = 2)
    {
        var thresh = useSI ? 1000L : 1024L;

        if (bytes < thresh)
            return bytes + " B";

        var suffixes = useSI ? SI_NAMES : IEC_NAMES;
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, thresh)));
        double num = Math.Round(bytes / Math.Pow(thresh, place), decimalPlaces);

        return num.ToString() + " " + suffixes[place];
    }
}
