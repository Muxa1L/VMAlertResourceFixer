using k8s.Models;

namespace VMAlertResourceFixer.Models;

internal sealed class PodMetricsListModel
{
    public List<PodMetricsModel> Items { get; set; } = [];
}

internal sealed class PodMetricsModel
{
    public V1ObjectMeta Metadata { get; set; } = new();

    public List<ContainerMetricsModel> Containers { get; set; } = [];
}

internal sealed class ContainerMetricsModel
{
    public string Name { get; set; } = string.Empty;

    public Dictionary<string, string> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}