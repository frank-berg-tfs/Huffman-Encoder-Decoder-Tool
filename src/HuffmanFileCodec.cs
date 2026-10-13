using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks.Dataflow;

namespace HuffmanCode;

public readonly record struct FileProgress(string Stage, long ProcessedBytes, long TotalBytes)
{
    /// <summary>Current output size, including the HFF header, when available.</summary>
    public long? CurrentOutputBytes { get; init; }
}

/// <summary>Streaming Huffman encoding for arbitrary binary files.</summary>
public static class HuffmanFileCodec
{
    private const int BufferSize = 64 * 1024;
    public const int DefaultBlockSize = 2 * 1024 * 1024;
    public static int DefaultWorkerCount => Math.Clamp((Environment.ProcessorCount + 1) / 2, 1, 64);
    private const byte MagicByte = 0x48;
    private const byte FormatVersion = 1;

    public static void EncodeFile(string inputPath, string outputPath,
        Action<FileProgress>? progress = null, CancellationToken cancellationToken = default)
        => EncodeFile(inputPath, outputPath, DefaultWorkerCount, DefaultBlockSize,
            progress, cancellationToken);

    public static void EncodeFile(string inputPath, string outputPath, int workerCount, int blockSize,
        Action<FileProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (workerCount < 1 || workerCount > 64)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be between 1 and 64.");
        if (blockSize < 1 || blockSize > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be between 1 byte and 16 MiB.");

        using FileStream input = OpenInput(inputPath);
        long[] frequencies = new long[256];
        byte[] buffer = new byte[BufferSize];
        long length = input.Length;
        long processed = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        progress?.Invoke(new("Analyzing", 0, length));
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
                frequencies[buffer[i]]++;
            hash.AppendData(buffer, 0, read);
            processed += read;
            progress?.Invoke(new("Analyzing", processed, length));
        }
        if (processed != length)
            throw new IOException("The input file changed while it was being read.");
        byte[] checksum = hash.GetHashAndReset();
        Node? root = BuildTree(frequencies);
        byte[][] codes = new byte[256][];
        if (root is not null)
            BuildCodes(root, new List<byte>(), codes);
        input.Position = 0;

        WriteNewFile(outputPath, output =>
        {
            using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(MagicByte);
            writer.Write((byte)'F');
            writer.Write((byte)'F');
            writer.Write(FormatVersion);
            writer.Write(length);
            writer.Write((ushort)frequencies.Count(f => f > 0));
            for (int symbol = 0; symbol < frequencies.Length; symbol++)
            {
                if (frequencies[symbol] == 0)
                    continue;
                writer.Write((byte)symbol);
                writer.Write(frequencies[symbol]);
            }
            writer.Write(checksum);
            long headerSize = output.Position;
            BitWriter bits = new(output);
            processed = 0;
            progress?.Invoke(new FileProgress("Encoding", 0, length) { CurrentOutputBytes = headerSize });
            EncodeBlocksAsync(input, bits, codes, hash, workerCount, blockSize, headerSize, progress, length,
                cancellationToken)
                .GetAwaiter().GetResult();
            bits.Complete();
            if (!CryptographicOperations.FixedTimeEquals(checksum, hash.GetHashAndReset()))
                throw new IOException("The input file changed during encoding.");
            cancellationToken.ThrowIfCancellationRequested();
        });
    }

    private static async Task EncodeBlocksAsync(FileStream input, BitWriter writer, byte[][] codes,
        IncrementalHash hash, int workerCount, int blockSize, long headerSize,
        Action<FileProgress>? progress, long totalBytes,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource pipelineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = pipelineCancellation.Token;
        TransformBlock<byte[], EncodedBlock> encoders = new(
            block => EncodeBlock(block, codes),
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = workerCount * 2,
                CancellationToken = token,
                EnsureOrdered = true,
                MaxDegreeOfParallelism = workerCount
            });

        long processedBytes = 0;
        async Task ProduceBlocksAsync()
        {
            try
            {
                while (true)
                {
                    byte[]? data = await ReadBlockAsync(input, blockSize, token).ConfigureAwait(false);
                    if (data is null)
                        break;
                    hash.AppendData(data);
                    if (!await encoders.SendAsync(data, token).ConfigureAwait(false))
                        throw new InvalidOperationException("The encoder stopped before all input blocks were accepted.");
                }
                encoders.Complete();
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException)
                {
                    try { ((IDataflowBlock)encoders).Fault(error); }
                    catch (InvalidOperationException) { }
                }
                throw;
            }
        }

        Task producer = ProduceBlocksAsync();
        try
        {
            while (await encoders.OutputAvailableAsync(token).ConfigureAwait(false))
            {
                while (encoders.TryReceive(out EncodedBlock? block))
                {
                    writer.Append(block.Payload, block.BitLength);
                    processedBytes += block.InputLength;
                    progress?.Invoke(new FileProgress("Encoding", processedBytes, totalBytes)
                    {
                        CurrentOutputBytes = headerSize + writer.CurrentByteCount
                    });
                }
            }
            await producer.ConfigureAwait(false);
            await encoders.Completion.ConfigureAwait(false);
        }
        catch
        {
            pipelineCancellation.Cancel();
            try { await producer.ConfigureAwait(false); } catch { }
            try { await encoders.Completion.ConfigureAwait(false); } catch { }
            throw;
        }

        if (processedBytes != totalBytes)
            throw new IOException("The input file changed while it was being encoded.");
    }

    private static async Task<byte[]?> ReadBlockAsync(FileStream input, int blockSize, CancellationToken cancellationToken)
    {
        byte[] block = new byte[blockSize];
        int length = 0;
        while (length < block.Length)
        {
            int read = await input.ReadAsync(block.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        if (length == 0)
            return null;
        return length == block.Length ? block : block.AsSpan(0, length).ToArray();
    }

    private static EncodedBlock EncodeBlock(byte[] block, byte[][] codes)
    {
        using MemoryStream payload = new();
        int current = 0;
        int bitCount = 0;
        long totalBits = 0;
        foreach (byte value in block)
        {
            byte[] code = codes[value] ?? throw new IOException("The input file changed during encoding.");
            foreach (byte bit in code)
            {
                current = (current << 1) | bit;
                totalBits++;
                if (++bitCount == 8)
                {
                    payload.WriteByte((byte)current);
                    current = bitCount = 0;
                }
            }
        }
        if (bitCount > 0)
            payload.WriteByte((byte)(current << (8 - bitCount)));
        return new EncodedBlock(block.Length, payload.ToArray(), totalBits);
    }

    private sealed record EncodedBlock(int InputLength, byte[] Payload, long BitLength);

    public static void DecodeFile(string inputPath, string outputPath,
        Action<FileProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using FileStream input = OpenInput(inputPath);
        using BinaryReader reader = new(input, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadByte() != MagicByte || reader.ReadByte() != (byte)'F' || reader.ReadByte() != (byte)'F')
            throw new InvalidDataException("The file does not have an HFF magic signature.");
        if (reader.ReadByte() != FormatVersion)
            throw new InvalidDataException("Unsupported HFF format version.");
        long length = reader.ReadInt64();
        int symbolCount = reader.ReadUInt16();
        if (length < 0 || symbolCount > 256 || (length == 0) != (symbolCount == 0))
            throw new InvalidDataException("Invalid HFF header.");
        long[] frequencies = new long[256];
        long total = 0;
        for (int i = 0; i < symbolCount; i++)
        {
            byte symbol = reader.ReadByte();
            long frequency = reader.ReadInt64();
            if (frequency <= 0 || frequencies[symbol] != 0 || frequency > length - total)
                throw new InvalidDataException("Invalid HFF frequency table.");
            frequencies[symbol] = frequency;
            total += frequency;
        }
        if (total != length)
            throw new InvalidDataException("The HFF frequencies do not match the original size.");
        byte[] checksum = new byte[32];
        input.ReadExactly(checksum);
        Node? root = BuildTree(frequencies);

        WriteNewFile(outputPath, output =>
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            BitReader bits = new(input);
            byte[] buffer = new byte[BufferSize];
            long processed = 0;
            progress?.Invoke(new("Decoding", 0, length));
            while (processed < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, length - processed);
                for (int i = 0; i < count; i++)
                {
                    Node node = root!;
                    while (!node.IsLeaf)
                        node = bits.Read() == 0 ? node.Left! : node.Right!;
                    byte symbol = (byte)node.Symbol;
                    if (--frequencies[symbol] < 0)
                        throw new InvalidDataException("The HFF data does not match its frequency table.");
                    buffer[i] = symbol;
                }
                output.Write(buffer, 0, count);
                hash.AppendData(buffer, 0, count);
                processed += count;
                progress?.Invoke(new("Decoding", processed, length));
            }
            bits.ValidateEnd();
            if (frequencies.Any(frequency => frequency != 0))
                throw new InvalidDataException("The decoded data does not match the HFF frequency table.");
            if (!CryptographicOperations.FixedTimeEquals(checksum, hash.GetHashAndReset()))
                throw new InvalidDataException("HFF checksum mismatch: the file is damaged.");
            cancellationToken.ThrowIfCancellationRequested();
        });
    }

    private static FileStream OpenInput(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);

    private static void WriteNewFile(string path, Action<Stream> write)
    {
        // CreateNew also protects existing files and the input itself, including aliases.
        bool created = false;
        try
        {
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.SequentialScan);
            created = true;
            write(output);
        }
        catch (Exception error)
        {
            if (created)
            {
                try { File.Delete(path); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException($"Operation failed and the incomplete output '{path}' could not be deleted.",
                        error, cleanupError);
                }
            }
            throw;
        }
    }

    private static Node? BuildTree(long[] frequencies)
    {
        // A stable tie breaker makes the tree reproducible across processes and runtimes.
        PriorityQueue<Node, (long Frequency, int Order)> queue = new();
        int order = 0;
        for (int symbol = 0; symbol < frequencies.Length; symbol++)
            if (frequencies[symbol] > 0)
                queue.Enqueue(new Node(symbol), (frequencies[symbol], order++));
        while (queue.Count > 1)
        {
            queue.TryDequeue(out Node? left, out var leftPriority);
            queue.TryDequeue(out Node? right, out var rightPriority);
            queue.Enqueue(new Node(-1, left, right), (checked(leftPriority.Frequency + rightPriority.Frequency), order++));
        }
        return queue.Count == 0 ? null : queue.Dequeue();
    }

    private static void BuildCodes(Node node, List<byte> path, byte[][] codes)
    {
        if (node.IsLeaf)
        {
            codes[node.Symbol] = path.ToArray();
            return;
        }
        path.Add(0);
        BuildCodes(node.Left!, path, codes);
        path[^1] = 1;
        BuildCodes(node.Right!, path, codes);
        path.RemoveAt(path.Count - 1);
    }

    private sealed record Node(int Symbol, Node? Left = null, Node? Right = null)
    {
        public bool IsLeaf => Left is null;
    }

    private sealed class BitWriter
    {
        private readonly Stream stream;
        private readonly byte[] buffer = new byte[BufferSize];
        private int count;
        private int current;
        private int bitCount;
        private long bytesAdded;

        public BitWriter(Stream stream) => this.stream = stream;

        public long CurrentByteCount => bytesAdded + (bitCount > 0 ? 1 : 0);

        public void Append(byte[] packedBits, long bitLength)
        {
            long completeBytes = bitLength / 8;
            for (long i = 0; i < completeBytes; i++)
                AppendByte(packedBits[(int)i]);

            int finalBitCount = (int)(bitLength % 8);
            if (finalBitCount > 0)
            {
                byte finalByte = packedBits[completeBytes];
                for (int i = 7; i >= 8 - finalBitCount; i--)
                    AppendBit((finalByte >> i) & 1);
            }
        }

        private void AppendByte(byte value)
        {
            if (bitCount == 0)
            {
                AddByte(value);
                return;
            }
            AddByte((byte)((current << (8 - bitCount)) | (value >> bitCount)));
            current = value & ((1 << bitCount) - 1);
        }

        private void AppendBit(int bit)
        {
            current = (current << 1) | bit;
            if (++bitCount == 8)
            {
                AddByte((byte)current);
                current = bitCount = 0;
            }
        }

        public void Complete()
        {
            if (bitCount > 0)
                AddByte((byte)(current << (8 - bitCount)));
            stream.Write(buffer, 0, count);
        }

        private void AddByte(byte value)
        {
            bytesAdded++;
            buffer[count++] = value;
            if (count == buffer.Length)
            {
                stream.Write(buffer);
                count = 0;
            }
        }
    }

    private sealed class BitReader
    {
        private readonly Stream stream;
        private readonly byte[] buffer = new byte[BufferSize];
        private int position;
        private int count;
        private int current;
        private int remainingBits;

        public BitReader(Stream stream) => this.stream = stream;

        public int Read()
        {
            if (remainingBits == 0)
            {
                if (position == count)
                {
                    count = stream.Read(buffer);
                    position = 0;
                    if (count == 0)
                        throw new EndOfStreamException("The HFF payload is truncated.");
                }
                current = buffer[position++];
                remainingBits = 8;
            }
            return (current >> --remainingBits) & 1;
        }

        public void ValidateEnd()
        {
            if ((current & ((1 << remainingBits) - 1)) != 0 || position != count || stream.ReadByte() != -1)
                throw new InvalidDataException("Invalid HFF padding or trailing data.");
        }
    }
}
