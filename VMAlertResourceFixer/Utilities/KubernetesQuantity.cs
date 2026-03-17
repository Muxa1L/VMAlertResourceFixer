using System.Globalization;
using System.Text.RegularExpressions;

namespace VMAlertResourceFixer.Utilities;

internal static partial class KubernetesQuantity
{
    private static readonly Dictionary<string, decimal> DecimalMultipliers = new(StringComparer.Ordinal)
    {
        [""] = 1m,
        ["n"] = 0.000000001m,
        ["u"] = 0.000001m,
        ["m"] = 0.001m,
        ["k"] = 1_000m,
        ["M"] = 1_000_000m,
        ["G"] = 1_000_000_000m,
        ["T"] = 1_000_000_000_000m,
        ["P"] = 1_000_000_000_000_000m,
        ["E"] = 1_000_000_000_000_000_000m
    };

    private static readonly Dictionary<string, decimal> BinaryMultipliers = new(StringComparer.Ordinal)
    {
        ["Ki"] = 1_024m,
        ["Mi"] = 1_048_576m,
        ["Gi"] = 1_073_741_824m,
        ["Ti"] = 1_099_511_627_776m,
        ["Pi"] = 1_125_899_906_842_624m,
        ["Ei"] = 1_152_921_504_606_846_976m
    };

    public static int ParseCpuToMillicores(string quantity)
    {
        var cores = Parse(quantity);
        return (int)Math.Ceiling(cores * 1000m);
    }

    public static long ParseMemoryToBytes(string quantity)
    {
        return (long)Math.Ceiling(Parse(quantity));
    }

    public static string FormatCpuMillicores(int millicores)
    {
        return $"{millicores}m";
    }

    public static string FormatMemoryMiB(int memoryMiB)
    {
        return $"{memoryMiB}Mi";
    }

    private static decimal Parse(string quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
        {
            throw new ArgumentException("Kubernetes quantity cannot be empty.", nameof(quantity));
        }

        var match = QuantityRegex().Match(quantity.Trim());
        if (!match.Success)
        {
            throw new FormatException($"Unsupported Kubernetes quantity '{quantity}'.");
        }

        var numericPart = decimal.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        var suffix = match.Groups[2].Value;

        if (BinaryMultipliers.TryGetValue(suffix, out var binaryMultiplier))
        {
            return numericPart * binaryMultiplier;
        }

        if (DecimalMultipliers.TryGetValue(suffix, out var decimalMultiplier))
        {
            return numericPart * decimalMultiplier;
        }

        throw new FormatException($"Unsupported Kubernetes quantity suffix '{suffix}' in '{quantity}'.");
    }

    [GeneratedRegex("^([+-]?(?:\\d+\\.?\\d*|\\d*\\.?\\d+))([a-zA-Z]{0,2})$", RegexOptions.Compiled)]
    private static partial Regex QuantityRegex();
}