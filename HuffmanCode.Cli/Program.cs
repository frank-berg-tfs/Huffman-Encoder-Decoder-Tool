using System.Diagnostics;
using System.Globalization;
using HuffmanCode;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    PrintUsage();
    return 0;
}
if (args.Length < 2 || args[0] is not ("encode" or "decode"))
{
    PrintUsage();
    return 2;
}

bool encode = args[0] == "encode";
int workerCount = HuffmanFileCodec.DefaultWorkerCount;
decimal blockSizeMiB = (decimal)HuffmanFileCodec.DefaultBlockSize / (1024 * 1024);
bool parallelOptionsProvided = false;
string? outputArgument = null;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] is "--workers" or "--block-size")
    {
        parallelOptionsProvided = true;
        string option = args[i];
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Missing value after {option}.");
            PrintUsage();
            return 2;
        }
        string valueText = args[++i];
        if (option == "--workers" && int.TryParse(valueText, NumberStyles.None,
                CultureInfo.InvariantCulture, out int value))
            workerCount = value;
        else if (option == "--block-size" && decimal.TryParse(valueText, NumberStyles.AllowDecimalPoint,
                     CultureInfo.InvariantCulture, out decimal size))
            blockSizeMiB = size;
        else
        {
            Console.Error.WriteLine($"Invalid value for {option}: {valueText}");
            PrintUsage();
            return 2;
        }
    }
    else if (outputArgument is null && !args[i].StartsWith("--", StringComparison.Ordinal))
    {
        outputArgument = args[i];
    }
    else
    {
        Console.Error.WriteLine($"Unexpected argument: {args[i]}");
        PrintUsage();
        return 2;
    }
}
if (!encode && parallelOptionsProvided)
{
    Console.Error.WriteLine("--workers and --block-size apply only to encoding.");
    return 2;
}
if (workerCount < 1 || workerCount > 64 || blockSizeMiB <= 0 || blockSizeMiB > 16)
{
    Console.Error.WriteLine("Workers must be 1–64 and block size must be greater than 0 and at most 16 MiB.");
    return 2;
}
int blockSize = (int)decimal.Round(blockSizeMiB * 1024 * 1024, 0, MidpointRounding.AwayFromZero);
if (blockSize < 1)
{
    Console.Error.WriteLine("Block size must be at least 1 byte.");
    return 2;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
ConsoleProgress progress = new();
Stopwatch operationTimer = Stopwatch.StartNew();
try
{
    string input = Path.GetFullPath(args[1]);
    string output;
    if (outputArgument is not null)
    {
        output = Path.GetFullPath(outputArgument);
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
        HuffmanFileCodec.EncodeFile(input, output, workerCount, blockSize, progress.Report, cancellation.Token);
    else
        HuffmanFileCodec.DecodeFile(input, output, progress.Report, cancellation.Token);
    operationTimer.Stop();
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
    Console.WriteLine($"Elapsed time: {operationTimer.Elapsed.TotalSeconds:F2} s");
    return 0;
}
catch (OperationCanceledException)
{
    operationTimer.Stop();
    progress.Finish();
    Console.Error.WriteLine("Cancelled. Incomplete output removed.");
    Console.Error.WriteLine($"Elapsed time: {operationTimer.Elapsed.TotalSeconds:F2} s");
    return 130;
}
catch (Exception error)
{
    operationTimer.Stop();
    progress.Finish();
    Console.Error.WriteLine($"Error: {error.Message}");
    Console.Error.WriteLine($"Elapsed time: {operationTimer.Elapsed.TotalSeconds:F2} s");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage: huffman encode <input> [output.hff]");
    Console.WriteLine("       huffman decode <input.hff> [output]");
    Console.WriteLine($"       Encoding options: --workers <count> (default {HuffmanFileCodec.DefaultWorkerCount}), --block-size <MiB> (default {(decimal)HuffmanFileCodec.DefaultBlockSize / (1024 * 1024):0.##})");
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
        string message = $"{progress.Stage}: {percentage:F1}% - {progress.ProcessedBytes:N0} bytes read";
        if (progress.Stage == "Encoding" && progress.CurrentOutputBytes is long outputBytes)
        {
            if (progress.ProcessedBytes > 0)
                message += $", current output ratio: {100.0 * outputBytes / progress.ProcessedBytes:F1}%";
        }
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
