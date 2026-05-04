using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 2)
{
    Console.Error.WriteLine("Uso: RinhaRefConverter <references.json.gz> <saida.bin>");
    return 1;
}

var inputPath = args[0];
var outputPath = args[1];

await using var output = File.Create(outputPath);

var header = new byte[13];
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x3146_4E52u); // RNF1
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1u);
// count placeholder
BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 0u);
header[12] = 14;

await output.WriteAsync(header);

await using Stream payload = inputPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
    ? new GZipStream(File.OpenRead(inputPath), CompressionMode.Decompress, leaveOpen: false)
    : File.OpenRead(inputPath);

var options = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
};

var count = 0u;
var rowBuffer = new byte[14 * sizeof(float) + 1];

await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<RefRow>(payload, options))
{
    if (item is null || item.Vector.Length != 14)
    {
        throw new InvalidDataException($"Registro inválido no índice aproximado {count}.");
    }

    for (var d = 0; d < 14; d++)
    {
        BinaryPrimitives.WriteSingleLittleEndian(rowBuffer.AsSpan(d * sizeof(float)), item.Vector[d]);
    }

    rowBuffer[^1] = item.IsFraud ? (byte)1 : (byte)0;
    await output.WriteAsync(rowBuffer);
    count++;

    if (count % 250_000 == 0)
    {
        Console.WriteLine($"Convertidos: {count:N0}");
    }
}

BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), count);
output.Position = 0;
await output.WriteAsync(header);

Console.WriteLine($"Total: {count:N0} vetores -> {outputPath}");
return 0;

internal sealed class RefRow
{
    [JsonPropertyName("vector")]
    public required float[] Vector { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonIgnore]
    public bool IsFraud => Label.Equals("fraud", StringComparison.OrdinalIgnoreCase);
}
