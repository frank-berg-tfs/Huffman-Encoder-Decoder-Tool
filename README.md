# Huffman-Code

## Description
A command-line tool that Huffman-encodes and decodes arbitrary files. The existing text-encoding library remains available.

## Usage

Build the solution with the .NET 10 SDK:

```powershell
dotnet build
```

Encode a file (the output defaults to `<input>.hff`):

```powershell
dotnet run --project HuffmanCode.Cli -- encode .\archive.zip
```

Decode a `.hff` file (the output defaults to the input name without `.hff`):

```powershell
dotnet run --project HuffmanCode.Cli -- decode .\archive.zip.hff
```

An output path can be supplied as the last argument. Encoding appends `.hff` if the supplied path does not already have that suffix. Existing files are never overwritten. For files of at least 1 MiB, progress includes the current encoded size and its ratio to the input processed so far; after encoding, the tool reports the final ratio to the complete original file. Ctrl+C deletes an incomplete output.

The binary format starts with the magic bytes `HFF` and a format version, then stores the original size, byte-frequency table, SHA-256 checksum, and Huffman payload. Decoding checks the header, payload, padding, frequency table, and checksum; on an error it removes the partially written output.

Resources: https://en.wikipedia.org/wiki/Huffman_coding
