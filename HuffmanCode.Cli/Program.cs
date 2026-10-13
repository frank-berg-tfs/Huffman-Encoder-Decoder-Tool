using System.Diagnostics;
using HuffmanCode;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    PrintUsage();
    return 0;
}
if (args.Length is < 2 or > 3 || args[0] is not ("encode" or "decode"))
{
    PrintUsage();
    return 2;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
ConsoleProgress progress = new();
try
{
    string input = Path.GetFullPath(args[1]);
    bool encode = args[0] == "encode";
    string output;
    if (args.Length == 3)
    {
        output = Path.GetFullPath(args[2]);
        if (encode && !output.EndsWith(".hff", StringComparison.OrdinalIgnoreCase))
            output += ".hff";
    }
    else if (encode)
    {
        output = input + ".hff";
    }
    else
    {
        if (!input.EndsWith(".hff", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Please specify an output path when decoding a file without the .hff suffix.");
        output = input[..^4];
    }

    if (encode)
        HuffmanFileCodec.EncodeFile(input, output, progress.Report, cancellation.Token);
    else
        HuffmanFileCodec.DecodeFile(input, output, progress.Report, cancellation.Token);
    progress.Finish();
    Console.WriteLine($"{(encode ? "Encoded" : "Decoded")}: {output}");
    if (encode)
    {
        long original = new FileInfo(input).Length;
        long encoded = new FileInfo(output).Length;
        Console.WriteLine($"Original: {original:N0} bytes; encoded: {encoded:N0} bytes.");
        if (original == 0)
            Console.WriteLine("Size percentage: n/a (empty input).");
        else
        {
            double percentage = 100.0 * encoded / original;
            Console.WriteLine($"Encoded size: {percentage:F2}% of the original; savings: {100 - percentage:F2}%.");
        }
    }
    return 0;
}
catch (OperationCanceledException)
{
    progress.Finish();
    Console.Error.WriteLine("Cancelled. Incomplete output removed.");
    return 130;
}
catch (Exception error)
{
    progress.Finish();
    Console.Error.WriteLine($"Error: {error.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage: huffman encode <input> [output.hff]");
    Console.WriteLine("       huffman decode <input.hff> [output]");
    Console.WriteLine("Encoding appends .hff; decoding removes it unless an output is specified.");
    Console.WriteLine("Existing output files are never overwritten. Ctrl+C cancels the operation.");
}

internal sealed class ConsoleProgress
{
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private string? stage;
    private long lastUpdate;
    private bool lineOpen;

    public void Report(FileProgress progress)
    {
        // Keep small operations quiet and throttle updates for large files.
        if (progress.TotalBytes < 1024 * 1024)
            return;
        bool changed = stage != progress.Stage;
        bool done = progress.ProcessedBytes == progress.TotalBytes;
        if (!changed && !done && timer.ElapsedMilliseconds - lastUpdate < 200)
            return;
        double percentage = 100.0 * progress.ProcessedBytes / progress.TotalBytes;
        string message = $"{progress.Stage}: {percentage,6:F1}% ({progress.ProcessedBytes:N0}/{progress.TotalBytes:N0} bytes)";
        if (Console.IsErrorRedirected)
        {
            if (changed || done)
                Console.Error.WriteLine(message);
        }
        else
        {
            if (changed)
                Finish();
            Console.Error.Write($"\r{message}   ");
            lineOpen = true;
            if (done)
                Finish();
        }
        stage = progress.Stage;
        lastUpdate = timer.ElapsedMilliseconds;
    }

    public void Finish()
    {
        if (lineOpen)
            Console.Error.WriteLine();
        lineOpen = false;
    }
}
