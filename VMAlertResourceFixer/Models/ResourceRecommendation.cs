namespace VMAlertResourceFixer.Models;

internal sealed record ResourceRecommendation(
    string CpuRequest,
    string MemoryRequest,
    int PeakCpuMillicores,
    long PeakMemoryBytes,
    int RecommendedCpuMillicores,
    int RecommendedMemoryMiB,
    int SampleCount);