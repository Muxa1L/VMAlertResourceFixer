using Newtonsoft.Json;

namespace VMAlertResourceFixer.Models;

internal sealed class VmAlertListModel
{
    public List<VmAlertModel> Items { get; set; } = [];
}

internal sealed class VmAlertModel
{
    public string ApiVersion { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public VmAlertMetadataModel Metadata { get; set; } = new();

    public VmAlertSpecModel Spec { get; set; } = new();
}

internal sealed class VmAlertMetadataModel
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("namespace")]
    public string NamespaceProperty { get; set; } = string.Empty;
}

internal sealed class VmAlertSpecModel
{
    [JsonProperty("resources")]
    public ResourceRequirementsModel? Resources { get; set; }
}

internal sealed class ResourceRequirementsModel
{
    [JsonProperty("requests")]
    public Dictionary<string, string>? Requests { get; set; }

    [JsonProperty("limits")]
    public Dictionary<string, string>? Limits { get; set; }
}