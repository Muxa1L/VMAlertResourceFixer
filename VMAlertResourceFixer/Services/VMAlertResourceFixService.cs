using System.Globalization;
using System.Net;
using k8s;
using k8s.Models;
using Newtonsoft.Json.Linq;
using VMAlertResourceFixer.Models;
using VMAlertResourceFixer.Options;
using VMAlertResourceFixer.Utilities;

namespace VMAlertResourceFixer.Services;

internal sealed class VMAlertResourceFixService
{
    private const string VmOperatorGroup = "operator.victoriametrics.com";
    private const string VmOperatorVersion = "v1beta1";
    private const string VmAlertPlural = "vmalerts";
    private const string MetricsGroup = "metrics.k8s.io";
    private const string MetricsVersion = "v1beta1";
    private const string PodsPlural = "pods";

    private readonly IKubernetes _kubernetes;
    private readonly AppOptions _options;

    public VMAlertResourceFixService(IKubernetes kubernetes, AppOptions options)
    {
        _kubernetes = kubernetes;
        _options = options;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine(_options.Apply
            ? "Applying VMAlert resource request recommendations."
            : "Running in dry-run mode. No VMAlert objects will be modified.");

        var vmAlerts = await GetVmAlertsAsync(cancellationToken);
        if (_options.Names.Count > 0)
        {
            vmAlerts = vmAlerts
                .Where(item => _options.Names.Contains(item.Metadata.Name))
                .ToList();
        }

        if (vmAlerts.Count == 0)
        {
            Console.WriteLine("No VMAlert resources matched the supplied filters.");
            return 0;
        }

        var metricsCache = new Dictionary<string, Dictionary<string, PodMetricsModel>>(StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        var skipped = 0;

        foreach (var vmAlert in vmAlerts.OrderBy(item => item.Metadata.NamespaceProperty).ThenBy(item => item.Metadata.Name))
        {
            var ns = vmAlert.Metadata.NamespaceProperty;
            var name = vmAlert.Metadata.Name;

            try
            {
                var deploymentName = $"vmalert-{name}";
                var deployment = await _kubernetes.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, ns, cancellationToken: cancellationToken);

                if (!metricsCache.TryGetValue(ns, out var namespaceMetrics))
                {
                    namespaceMetrics = await GetPodMetricsByNameAsync(ns, cancellationToken);
                    metricsCache[ns] = namespaceMetrics;
                }

                var pods = await GetDeploymentPodsAsync(ns, deployment, cancellationToken);
                var recommendation = BuildRecommendation(pods, namespaceMetrics);
                if (recommendation is null)
                {
                    Console.WriteLine($"SKIP  {ns}/{name}  No running pod metrics were found.");
                    skipped++;
                    continue;
                }

                var currentCpu = GetCurrentRequest(vmAlert.Spec.Resources?.Requests, "cpu");
                var currentMemory = GetCurrentRequest(vmAlert.Spec.Resources?.Requests, "memory");
                var requiresUpdate = !string.Equals(currentCpu, recommendation.CpuRequest, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(currentMemory, recommendation.MemoryRequest, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine(
                    $"{(_options.Apply && requiresUpdate ? "PATCH" : "INFO ")}  {ns}/{name}  " +
                    $"peakCpu={recommendation.PeakCpuMillicores}m peakMemory={FormatBytesAsMi(recommendation.PeakMemoryBytes)} " +
                    $"recommendedCpu={recommendation.CpuRequest} recommendedMemory={recommendation.MemoryRequest} samples={recommendation.SampleCount}");

                if (_options.Verbose)
                {
                    Console.WriteLine($"      currentCpu={currentCpu ?? "<unset>"} currentMemory={currentMemory ?? "<unset>"}");
                }

                if (!requiresUpdate)
                {
                    continue;
                }

                if (_options.Apply)
                {
                    await PatchVmAlertRequestsAsync(ns, name, recommendation, cancellationToken);
                }

                changed++;
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response is { StatusCode: HttpStatusCode.NotFound })
            {
                Console.WriteLine($"SKIP  {ns}/{name}  Deployment vmalert-{name} was not found.");
                skipped++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Processed: {vmAlerts.Count}, changed: {changed}, skipped: {skipped}, mode: {(_options.Apply ? "apply" : "dry-run")}");
        return 0;
    }

    private async Task<List<VmAlertModel>> GetVmAlertsAsync(CancellationToken cancellationToken)
    {
        var items = new List<VmAlertModel>();

        if (_options.Namespaces.Count == 0)
        {
            var raw = await _kubernetes.CustomObjects.ListClusterCustomObjectAsync(
                VmOperatorGroup,
                VmOperatorVersion,
                VmAlertPlural,
                cancellationToken: cancellationToken);

            items.AddRange(ToVmAlertList(raw).Items);
            return items;
        }

        foreach (var ns in _options.Namespaces.OrderBy(value => value))
        {
            var raw = await _kubernetes.CustomObjects.ListNamespacedCustomObjectAsync(
                VmOperatorGroup,
                VmOperatorVersion,
                ns,
                VmAlertPlural,
                cancellationToken: cancellationToken);

            items.AddRange(ToVmAlertList(raw).Items);
        }

        return items;
    }

    private async Task<Dictionary<string, PodMetricsModel>> GetPodMetricsByNameAsync(string ns, CancellationToken cancellationToken)
    {
        var raw = await _kubernetes.CustomObjects.ListNamespacedCustomObjectAsync(
            MetricsGroup,
            MetricsVersion,
            ns,
            PodsPlural,
            cancellationToken: cancellationToken);

        var metrics = ToPodMetricsList(raw);
        return metrics.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Metadata.Name))
            .ToDictionary(item => item.Metadata.Name!, item => item, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<V1Pod>> GetDeploymentPodsAsync(string ns, V1Deployment deployment, CancellationToken cancellationToken)
    {
        var selector = BuildLabelSelector(deployment.Spec?.Selector);
        if (string.IsNullOrWhiteSpace(selector))
        {
            return [];
        }

        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
            ns,
            labelSelector: selector,
            cancellationToken: cancellationToken);

        return pods.Items
            .Where(IsRunningPod)
            .ToList();
    }

    private ResourceRecommendation? BuildRecommendation(
        IReadOnlyList<V1Pod> pods,
        IReadOnlyDictionary<string, PodMetricsModel> namespaceMetrics)
    {
        var samples = new List<(int CpuMillicores, long MemoryBytes)>();

        foreach (var pod in pods)
        {
            if (string.IsNullOrWhiteSpace(pod.Metadata?.Name) || !namespaceMetrics.TryGetValue(pod.Metadata.Name, out var metrics))
            {
                continue;
            }

            var cpuMillicores = 0;
            long memoryBytes = 0;

            foreach (var container in metrics.Containers)
            {
                if (container.Usage.TryGetValue("cpu", out var cpuRaw))
                {
                    cpuMillicores += KubernetesQuantity.ParseCpuToMillicores(cpuRaw);
                }

                if (container.Usage.TryGetValue("memory", out var memoryRaw))
                {
                    memoryBytes += KubernetesQuantity.ParseMemoryToBytes(memoryRaw);
                }
            }

            if (cpuMillicores > 0 || memoryBytes > 0)
            {
                samples.Add((cpuMillicores, memoryBytes));
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        var peakCpuMillicores = samples.Max(item => item.CpuMillicores);
        var peakMemoryBytes = samples.Max(item => item.MemoryBytes);
        var recommendedCpuMillicores = RoundUp(
            Math.Max(_options.MinCpuMillicores, (int)Math.Ceiling(peakCpuMillicores * _options.CpuHeadroomFactor)),
            _options.CpuStepMillicores);
        var recommendedMemoryMiB = RoundUp(
            Math.Max(_options.MinMemoryMiB, (int)Math.Ceiling(BytesToMiB(peakMemoryBytes) * _options.MemoryHeadroomFactor)),
            _options.MemoryStepMiB);

        return new ResourceRecommendation(
            KubernetesQuantity.FormatCpuMillicores(recommendedCpuMillicores),
            KubernetesQuantity.FormatMemoryMiB(recommendedMemoryMiB),
            peakCpuMillicores,
            peakMemoryBytes,
            recommendedCpuMillicores,
            recommendedMemoryMiB,
            samples.Count);
    }

    private async Task PatchVmAlertRequestsAsync(
        string ns,
        string name,
        ResourceRecommendation recommendation,
        CancellationToken cancellationToken)
    {
        var patchDocument = new JObject
        {
            ["spec"] = new JObject
            {
                ["resources"] = new JObject
                {
                    ["requests"] = new JObject
                    {
                        ["cpu"] = recommendation.CpuRequest,
                        ["memory"] = recommendation.MemoryRequest
                    }
                }
            }
        };

        var patch = new V1Patch(patchDocument.ToString(), V1Patch.PatchType.MergePatch);
        await _kubernetes.CustomObjects.PatchNamespacedCustomObjectAsync(
            patch,
            VmOperatorGroup,
            VmOperatorVersion,
            ns,
            VmAlertPlural,
            name,
            cancellationToken: cancellationToken);
    }

    private static VmAlertListModel ToVmAlertList(object raw)
    {
        return JObject.FromObject(raw).ToObject<VmAlertListModel>() ?? new VmAlertListModel();
    }

    private static PodMetricsListModel ToPodMetricsList(object raw)
    {
        return JObject.FromObject(raw).ToObject<PodMetricsListModel>() ?? new PodMetricsListModel();
    }

    private static bool IsRunningPod(V1Pod pod)
    {
        return string.Equals(pod.Status?.Phase, "Running", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentRequest(IReadOnlyDictionary<string, string>? requests, string key)
    {
        if (requests is null)
        {
            return null;
        }

        return requests.TryGetValue(key, out var value) ? value : null;
    }

    private static int RoundUp(int value, int step)
    {
        if (step <= 1)
        {
            return value;
        }

        var remainder = value % step;
        return remainder == 0 ? value : value + step - remainder;
    }

    private static double BytesToMiB(long bytes)
    {
        return bytes / 1024d / 1024d;
    }

    private static string FormatBytesAsMi(long bytes)
    {
        return $"{BytesToMiB(bytes).ToString("0.##", CultureInfo.InvariantCulture)}Mi";
    }

    private static string BuildLabelSelector(V1LabelSelector? selector)
    {
        if (selector?.MatchLabels is null || selector.MatchLabels.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(",", selector.MatchLabels.Select(item => $"{item.Key}={item.Value}"));
    }
}