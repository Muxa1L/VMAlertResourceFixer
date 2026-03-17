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

                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
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
        Console.WriteLine("  --verbose                Print extra diagnostic output.");
        Console.WriteLine("  -h, --help               Show this help.");
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

    private static void AddCsvValues(ISet<string> set, string raw)
    {
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(item);
        }
    }
}