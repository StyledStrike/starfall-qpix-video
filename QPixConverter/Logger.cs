namespace QPixConverter;

public class Logger(string source)
{
    public enum Tag
    {
        DEBUG,
        INFO,
        WARN,
        SUCCESS,
        ERROR,
    }

    const string RESET_CODE = "\x1b[0m";

    private readonly string _source = source;

    private static string PadNumber(int n)
    {
        if (n > 9) return n.ToString();
        return "0" + n;
    }

    private static string GetTimestamp(string dateSep = "/", string timeSep = ":", string middleSep = " @ ")
    {
        var now = DateTime.Now;

        string[] date = [
            PadNumber(now.Day),
            PadNumber(now.Month),
            PadNumber(now.Year),
        ];

        string[] time = [
            PadNumber(now.Hour),
            PadNumber(now.Minute),
            PadNumber(now.Second),
        ];

        return string.Join(dateSep, date) + middleSep + string.Join(timeSep, time);
    }

    public void Log(Tag tag, params object?[] args)
    {
        var tagColor = "\x1b[37m";

        switch (tag)
        {
            case Tag.DEBUG:
                tagColor = "\x1b[36m";
                break;
            case Tag.INFO:
                tagColor = "\x1b[37m";
                break;
            case Tag.WARN:
                tagColor = "\x1b[33m";
                break;
            case Tag.SUCCESS:
                tagColor = "\x1b[32m";
                break;
            case Tag.ERROR:
                tagColor = "\x1b[31m";
                break;
        }

        var joinedArgs = string.Join(" ", args.Select(arg => arg == null ? "null" : arg.ToString()));

        Console.WriteLine($"{tagColor}[{GetTimestamp()}]{RESET_CODE} [{_source}] {joinedArgs}");
    }

    public void Debug(params object?[] args)
    {
        Log(Tag.DEBUG, args);
    }

    public void Info(params object?[] args)
    {
        Log(Tag.INFO, args);
    }

    public void Warn(params object?[] args)
    {
        Log(Tag.WARN, args);
    }

    public void Error(params object?[] args)
    {
        Log(Tag.ERROR, args);
    }

    public void Success(params object?[] args)
    {
        Log(Tag.SUCCESS, args);
    }

    public static string Highlight(object? arg)
    {
        var argString = arg == null ? "null" : arg.ToString();
        return "\x1b[35m" + argString + RESET_CODE;
    }
}
