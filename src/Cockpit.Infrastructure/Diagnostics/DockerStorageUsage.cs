using System.Globalization;
using System.Text.Json;

namespace Cockpit.Infrastructure.Diagnostics;

internal sealed record DockerStorageUsage(long UsedBytes, string Description)
{
    public static DockerStorageUsage Parse(string output)
    {
        List<JsonElement> rows = [];
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                rows.AddRange(doc.RootElement.EnumerateArray().Select(row => row.Clone()));
            }
            else
            {
                rows.Add(doc.RootElement.Clone());
            }
        }

        if (rows.Count != 4 || rows.Any(row => !row.TryGetProperty("Type", out _) || !row.TryGetProperty("Reclaimable", out _))
            || !rows.Select(row => row.GetProperty("Type").GetString()).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "Build Cache", "Containers", "Images", "Local Volumes" }, StringComparer.Ordinal))
        {
            throw new FormatException("Docker did not return all four storage categories.");
        }

        var total = rows.Sum(row => Bytes(row.GetProperty("Size").GetString()!));
        return new DockerStorageUsage(total, string.Join("; ", rows.Select(row => $"{row.GetProperty("Type").GetString()}: {row.GetProperty("Size").GetString()}, reclaimable {row.GetProperty("Reclaimable").GetString()}")));
    }

    internal static long Bytes(string value)
    {
        var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var split = 0;
        while (split < token.Length && (char.IsDigit(token[split]) || token[split] == '.'))
        {
            split++;
        }

        var unit = token[split..];
        var multiplier = unit switch
        {
            "B" => 1d,
            "kB" or "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            "KiB" => 1024d,
            "MiB" => 1048576d,
            "GiB" => 1073741824d,
            "TiB" => 1099511627776d,
            _ => throw new FormatException("Unknown Docker storage unit: " + unit),
        };
        var number = double.Parse(token[..split], CultureInfo.InvariantCulture);
        if (!double.IsFinite(number) || number < 0 || number * multiplier > long.MaxValue)
        {
            throw new FormatException("Invalid Docker storage size.");
        }

        return checked((long)(number * multiplier));
    }
}
