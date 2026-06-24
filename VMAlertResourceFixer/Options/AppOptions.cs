using System.Globalization;

namespace VMAlertResourceFixer.Options;

internal sealed record AppOptions
{
    public bool ShowHelp { get; private init; }

    public bool Apply { get; private init; }

    public bool Verbose { get; private init; }

    public string? KubeConfigPath { get; private init; }

    public string? Context { get; private init; }

    public HashSet<string> Namespaces { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double CpuHeadroomFactor { get; private init; } = 1.25d;

    public double MemoryHeadroomFactor { get; private init; } = 1.25d;

    public int MinCpuMillicores { get; private init; } = 50;

    public int MinMemoryMiB { get; private init; } = 64;

    public int CpuStepMillicores { get; private init; } = 25;

    public int MemoryStepMiB { get; private init; } = 16;

    public TimeSpan SamplePeriod { get; private init; } = TimeSpan.FromMinutes(2);

    public TimeSpan SampleInterval { get; private init; } = TimeSpan.FromSeconds(30);

    public int Parallelism { get; private init; } = 8;

    public static AppOptions Parse(string[] args)
    {
        var options = new AppOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "-h":
                case "--help":
                    options = options with { ShowHelp = true };
                    break;

                case "--apply":
                    options = options with { Apply = true };
                    break;

                case "--dry-run":
                    options = options with { Apply = false };
                    break;

                case "--verbose":
                    options = options with { Verbose = true };
                    break;

                case "--namespace":
                    AddCsvValues(options.Namespaces, ReadNext(args, ref index, arg));
                    break;

                case "--name":
                    AddCsvValues(options.Names, ReadNext(args, ref index, arg));
                    break;

                case "--kubeconfig":
                    options = options with { KubeConfigPath = ReadNext(args, ref index, arg) };
                    break;

                case "--context":
                    options = options with { Context = ReadNext(args, ref index, arg) };
                    break;

                case "--cpu-headroom":
                    options = options with { CpuHeadroomFactor = ReadPositiveDouble(args, ref index, arg) };
                    break;

                case "--memory-headroom":
                    options = options with { MemoryHeadroomFactor = ReadPositiveDouble(args, ref index, arg) };
                    break;

                case "--min-cpu-m":
                    options = options with { MinCpuMillicores = ReadPositiveInt(args, ref index, arg) };
                    break;

                case "--min-memory-mi":
                    options = options with { MinMemoryMiB = ReadPositiveInt(args, ref index, arg) };
                    break;

                case "--cpu-step-m":
                    options = options with { CpuStepMillicores = ReadPositiveInt(args, ref index, arg) };
                    break;

                case "--memory-step-mi":
                    options = options with { MemoryStepMiB = ReadPositiveInt(args, ref index, arg) };
                    break;

                case "--sample-period":
                    options = options with { SamplePeriod = ReadPositiveDuration(args, ref index, arg) };
                    break;

                case "--sample-interval":
                    options = options with { SampleInterval = ReadPositiveDuration(args, ref index, arg) };
                    break;

                case "--parallelism":
                    options = options with { Parallelism = ReadPositiveInt(args, ref index, arg) };
                    break;

                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (options.SampleInterval > options.SamplePeriod)
        {
            options = options with { SampleInterval = options.SamplePeriod };
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("VMAlertResourceFixer");
        Console.WriteLine("Updates VMAlert CRD resource requests from current pod usage exposed by metrics-server.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project VMAlertResourceFixer -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --apply                  Persist changes to VMAlert resources. Default is dry-run.");
        Console.WriteLine("  --dry-run                Print recommendations without patching the cluster.");
        Console.WriteLine("  --namespace <list>       Comma-separated namespace filter.");
        Console.WriteLine("  --name <list>            Comma-separated VMAlert name filter.");
        Console.WriteLine("  --kubeconfig <path>      Optional kubeconfig path.");
        Console.WriteLine("  --context <name>         Optional kubeconfig context.");
        Console.WriteLine("  --cpu-headroom <factor>  CPU multiplier. Default: 1.25");
        Console.WriteLine("  --memory-headroom <f>    Memory multiplier. Default: 1.25");
        Console.WriteLine("  --min-cpu-m <value>      Minimum CPU request in millicores. Default: 50");
        Console.WriteLine("  --min-memory-mi <value>  Minimum memory request in MiB. Default: 64");
        Console.WriteLine("  --cpu-step-m <value>     CPU rounding step in millicores. Default: 25");
        Console.WriteLine("  --memory-step-mi <value> Memory rounding step in MiB. Default: 16");
        Console.WriteLine("  --sample-period <value>  Metrics sampling window. Default: 2m");
        Console.WriteLine("  --sample-interval <v>    Metrics sampling interval. Default: 30s");
        Console.WriteLine("  --parallelism <value>    Max concurrent Kubernetes lookups. Default: 8");
        Console.WriteLine("  --verbose                Print extra diagnostic output.");
        Console.WriteLine("  -h, --help               Show this help.");
        Console.WriteLine();
        Console.WriteLine("Duration values accept plain seconds or suffixes like ms, s, m, h.");
    }

    private static string ReadNext(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for '{optionName}'.");
        }

        index++;
        return args[index];
    }

    private static int ReadPositiveInt(string[] args, ref int index, string optionName)
    {
        var raw = ReadNext(args, ref index, optionName);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new ArgumentException($"Value for '{optionName}' must be a positive integer.");
        }

        return value;
    }

    private static double ReadPositiveDouble(string[] args, ref int index, string optionName)
    {
        var raw = ReadNext(args, ref index, optionName);
        if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new ArgumentException($"Value for '{optionName}' must be a positive number.");
        }

        return value;
    }

    private static TimeSpan ReadPositiveDuration(string[] args, ref int index, string optionName)
    {
        var raw = ReadNext(args, ref index, optionName);
        if (TryParseDuration(raw, out var value) && value > TimeSpan.Zero)
        {
            return value;
        }

        throw new ArgumentException($"Value for '{optionName}' must be a positive duration like '30s', '2m', or '300'.");
    }

    private static bool TryParseDuration(string raw, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseDurationValue(raw[..^2], TimeSpan.FromMilliseconds, out duration);
        }

        if (raw.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseDurationValue(raw[..^1], TimeSpan.FromSeconds, out duration);
        }

        if (raw.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseDurationValue(raw[..^1], TimeSpan.FromMinutes, out duration);
        }

        if (raw.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseDurationValue(raw[..^1], TimeSpan.FromHours, out duration);
        }

        return TryParseDurationValue(raw, TimeSpan.FromSeconds, out duration);
    }

    private static bool TryParseDurationValue(string raw, Func<double, TimeSpan> factory, out TimeSpan duration)
    {
        if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            duration = factory(value);
            return true;
        }

        duration = default;
        return false;
    }

    private static void AddCsvValues(ISet<string> set, string raw)
    {
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(item);
        }
    }
}