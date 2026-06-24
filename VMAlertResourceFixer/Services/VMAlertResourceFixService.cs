using System.Globalization;
using System.Net;
using System.Collections.Concurrent;
using k8s;
using k8s.Models;
using Newtonsoft.Json.Linq;
using VMAlertResourceFixer.Models;
using VMAlertResourceFixer.Options;
using VMAlertResourceFixer.Utilities;

namespace VMAlertResourceFixer.Services;

internal sealed record PodUsageAggregate(int PeakCpuMillicores, long PeakMemoryBytes, int ObservationCount);
internal sealed record VmAlertProcessingResult(IReadOnlyList<string> LogLines, bool Changed, bool Skipped);

internal sealed class VMAlertResourceFixService
{
    private const string VmOperatorGroup = "operator.victoriametrics.com";
    private const string VmOperatorVersion = "v1beta1";
    private const string VmAlertPlural = "vmalerts";
    private const string MetricsGroup = "metrics.k8s.io";
    private const string MetricsVersion = "v1beta1";
    private const string PodsPlural = "pods";
    private const int ListPageSize = 250;

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
        if (_options.Verbose)
        {
            Console.WriteLine($"Discovered {vmAlerts.Count} VMAlert resources before applying name filters.");
        }

        if (_options.Names.Count > 0)
        {
            vmAlerts = vmAlerts
                .Where(item => _options.Names.Contains(item.Metadata.Name))
                .ToList();

            if (_options.Verbose)
            {
                Console.WriteLine($"Retained {vmAlerts.Count} VMAlert resources after applying name filters.");
            }
        }

        if (vmAlerts.Count == 0)
        {
            Console.WriteLine($"No VMAlert resources matched the supplied filters. {DescribeFilters()}");
            return 0;
        }

        var namespaces = vmAlerts
            .Select(item => item.Metadata.NamespaceProperty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .ToList();

        Console.WriteLine(
            $"Collecting pod metrics samples for {FormatDuration(_options.SamplePeriod)} every {FormatDuration(_options.SampleInterval)} across {namespaces.Count} namespace(s) with parallelism {_options.Parallelism}.");

        var metricsCache = await CollectMaxPodMetricsByNamespaceAsync(namespaces, cancellationToken);
        var orderedAlerts = vmAlerts.OrderBy(item => item.Metadata.NamespaceProperty).ThenBy(item => item.Metadata.Name).ToList();
        var results = await RunBoundedAsync(
            orderedAlerts,
            _options.Parallelism,
            async (vmAlert, token) => await ProcessVmAlertAsync(vmAlert, metricsCache, token),
            cancellationToken);

        var changed = 0;
        var skipped = 0;

        foreach (var result in results)
        {
            foreach (var line in result.LogLines)
            {
                Console.WriteLine(line);
            }

            if (result.Changed)
            {
                changed++;
            }

            if (result.Skipped)
            {
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
            items.AddRange(await ListAllVmAlertsAsync(cancellationToken));
            return items;
        }

        foreach (var ns in _options.Namespaces.OrderBy(value => value))
        {
            items.AddRange(await ListVmAlertsInNamespaceAsync(ns, cancellationToken));
        }

        return items;
    }

    private async Task<Dictionary<string, Dictionary<string, PodUsageAggregate>>> CollectMaxPodMetricsByNamespaceAsync(
        IReadOnlyCollection<string> namespaces,
        CancellationToken cancellationToken)
    {
        var collected = namespaces.ToDictionary(
            ns => ns,
            _ => new Dictionary<string, PodUsageAggregate>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var snapshotCount = CalculateSnapshotCount(_options.SamplePeriod, _options.SampleInterval);

        for (var sampleIndex = 0; sampleIndex < snapshotCount; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = await RunBoundedAsync(
                namespaces,
                _options.Parallelism,
                async (ns, token) => (Namespace: ns, Snapshot: await GetPodMetricsByNameAsync(ns, token)),
                cancellationToken);

            foreach (var snapshot in snapshots)
            {
                MergePodMetricSnapshot(collected[snapshot.Namespace], snapshot.Snapshot);
            }

            if (sampleIndex == snapshotCount - 1)
            {
                break;
            }

            if (_options.Verbose)
            {
                Console.WriteLine($"Collected metrics snapshot {sampleIndex + 1} of {snapshotCount}; waiting {FormatDuration(_options.SampleInterval)} for the next sample.");
            }

            await Task.Delay(_options.SampleInterval, cancellationToken);
        }

        if (_options.Verbose)
        {
            Console.WriteLine($"Collected {snapshotCount} metrics snapshot(s) across {namespaces.Count} namespace(s).");
        }

        return collected;
    }

    private async Task<VmAlertProcessingResult> ProcessVmAlertAsync(
        VmAlertModel vmAlert,
        IReadOnlyDictionary<string, Dictionary<string, PodUsageAggregate>> metricsCache,
        CancellationToken cancellationToken)
    {
        var ns = vmAlert.Metadata.NamespaceProperty;
        var name = vmAlert.Metadata.Name;
        var logLines = new List<string>();

        try
        {
            var deploymentName = $"vmalert-{name}";
            var deployment = await _kubernetes.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, ns, cancellationToken: cancellationToken);

            metricsCache.TryGetValue(ns, out var namespaceMetrics);
            namespaceMetrics ??= new Dictionary<string, PodUsageAggregate>(StringComparer.OrdinalIgnoreCase);

            var pods = await GetDeploymentPodsAsync(ns, deployment, cancellationToken);
            var recommendation = BuildRecommendation(pods, namespaceMetrics);
            if (recommendation is null)
            {
                logLines.Add($"SKIP  {ns}/{name}  No running pod metrics were found.");
                return new VmAlertProcessingResult(logLines, Changed: false, Skipped: true);
            }

            var currentCpu = GetCurrentRequest(vmAlert.Spec.Resources?.Requests, "cpu");
            var currentMemory = GetCurrentRequest(vmAlert.Spec.Resources?.Requests, "memory");
            var requiresUpdate = !string.Equals(currentCpu, recommendation.CpuRequest, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(currentMemory, recommendation.MemoryRequest, StringComparison.OrdinalIgnoreCase);

            logLines.Add(
                $"{(_options.Apply && requiresUpdate ? "PATCH" : "INFO ")}  {ns}/{name}  " +
                $"peakCpu={recommendation.PeakCpuMillicores}m peakMemory={FormatBytesAsMi(recommendation.PeakMemoryBytes)} " +
                $"recommendedCpu={recommendation.CpuRequest} recommendedMemory={recommendation.MemoryRequest} samples={recommendation.SampleCount}");

            if (_options.Verbose)
            {
                logLines.Add($"      currentCpu={currentCpu ?? "<unset>"} currentMemory={currentMemory ?? "<unset>"}");
            }

            if (!requiresUpdate)
            {
                return new VmAlertProcessingResult(logLines, Changed: false, Skipped: false);
            }

            if (_options.Apply)
            {
                await PatchVmAlertRequestsAsync(ns, name, recommendation, cancellationToken);
            }

            return new VmAlertProcessingResult(logLines, Changed: true, Skipped: false);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response is { StatusCode: HttpStatusCode.NotFound })
        {
            logLines.Add($"SKIP  {ns}/{name}  Deployment vmalert-{name} was not found.");
            return new VmAlertProcessingResult(logLines, Changed: false, Skipped: true);
        }
    }

    private async Task<Dictionary<string, PodMetricsModel>> GetPodMetricsByNameAsync(string ns, CancellationToken cancellationToken)
    {
        var items = new List<PodMetricsModel>();
        string? continueParameter = null;

        do
        {
            var page = await _kubernetes.CustomObjects.ListNamespacedCustomObjectAsync<PodMetricsListModel>(
                MetricsGroup,
                MetricsVersion,
                ns,
                PodsPlural,
                continueParameter: continueParameter,
                limit: ListPageSize,
                cancellationToken: cancellationToken);

            if (page?.Items is { Count: > 0 })
            {
                items.AddRange(page.Items.Where(item => !string.IsNullOrWhiteSpace(item.Metadata?.Name)));
            }

            continueParameter = page?.Metadata?.ContinueToken;
        }
        while (!string.IsNullOrWhiteSpace(continueParameter));

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Metadata.Name))
            .ToDictionary(item => item.Metadata.Name!, item => item, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<List<VmAlertModel>> ListAllVmAlertsAsync(CancellationToken cancellationToken)
    {
        var items = new List<VmAlertModel>();
        string? continueParameter = null;

        do
        {
            var page = await _kubernetes.CustomObjects.ListCustomObjectForAllNamespacesAsync<VmAlertListModel>(
                VmOperatorGroup,
                VmOperatorVersion,
                VmAlertPlural,
                continueParameter: continueParameter,
                limit: ListPageSize,
                cancellationToken: cancellationToken);

            if (page?.Items is { Count: > 0 })
            {
                items.AddRange(page.Items.Where(HasVmAlertIdentity));
            }

            continueParameter = page?.Metadata?.ContinueToken;
        }
        while (!string.IsNullOrWhiteSpace(continueParameter));

        return items;
    }

    private async Task<List<VmAlertModel>> ListVmAlertsInNamespaceAsync(string ns, CancellationToken cancellationToken)
    {
        var items = new List<VmAlertModel>();
        string? continueParameter = null;

        do
        {
            var page = await _kubernetes.CustomObjects.ListNamespacedCustomObjectAsync<VmAlertListModel>(
                VmOperatorGroup,
                VmOperatorVersion,
                ns,
                VmAlertPlural,
                continueParameter: continueParameter,
                limit: ListPageSize,
                cancellationToken: cancellationToken);

            if (page?.Items is { Count: > 0 })
            {
                items.AddRange(page.Items.Where(HasVmAlertIdentity));
            }

            continueParameter = page?.Metadata?.ContinueToken;
        }
        while (!string.IsNullOrWhiteSpace(continueParameter));

        return items;
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
        IReadOnlyDictionary<string, PodUsageAggregate> namespaceMetrics)
    {
        var samples = new List<(int CpuMillicores, long MemoryBytes, int ObservationCount)>();

        foreach (var pod in pods)
        {
            if (string.IsNullOrWhiteSpace(pod.Metadata?.Name) || !namespaceMetrics.TryGetValue(pod.Metadata.Name, out var metrics))
            {
                continue;
            }

            if (metrics.PeakCpuMillicores > 0 || metrics.PeakMemoryBytes > 0)
            {
                samples.Add((metrics.PeakCpuMillicores, metrics.PeakMemoryBytes, metrics.ObservationCount));
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
                samples.Sum(item => item.ObservationCount));
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

    private string DescribeFilters()
    {
        var namespaceFilter = _options.Namespaces.Count == 0
            ? "all namespaces"
            : $"namespaces={string.Join(",", _options.Namespaces.OrderBy(value => value))}";
        var nameFilter = _options.Names.Count == 0
            ? "all names"
            : $"names={string.Join(",", _options.Names.OrderBy(value => value))}";

        return $"Scope: {namespaceFilter}; {nameFilter}.";
    }

    private static bool HasVmAlertIdentity(VmAlertModel item)
    {
        return !string.IsNullOrWhiteSpace(item.Metadata.Name)
            && !string.IsNullOrWhiteSpace(item.Metadata.NamespaceProperty);
    }

    private static bool IsRunningPod(V1Pod pod)
    {
        return string.Equals(pod.Status?.Phase, "Running", StringComparison.OrdinalIgnoreCase);
    }

    private static void MergePodMetricSnapshot(
        IDictionary<string, PodUsageAggregate> aggregateByPod,
        IReadOnlyDictionary<string, PodMetricsModel> snapshot)
    {
        foreach (var (podName, metrics) in snapshot)
        {
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

            if (aggregateByPod.TryGetValue(podName, out var existing))
            {
                aggregateByPod[podName] = new PodUsageAggregate(
                    Math.Max(existing.PeakCpuMillicores, cpuMillicores),
                    Math.Max(existing.PeakMemoryBytes, memoryBytes),
                    existing.ObservationCount + 1);
            }
            else
            {
                aggregateByPod[podName] = new PodUsageAggregate(cpuMillicores, memoryBytes, 1);
            }
        }
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

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return value.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        }

        return value.ToString("mm\\:ss", CultureInfo.InvariantCulture);
    }

    private static int CalculateSnapshotCount(TimeSpan period, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Floor(period.Ticks / (double)interval.Ticks) + 1);
    }

    private static async Task<IReadOnlyList<TResult>> RunBoundedAsync<TSource, TResult>(
        IReadOnlyCollection<TSource> items,
        int parallelism,
        Func<TSource, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var results = new TResult[items.Count];
        using var semaphore = new SemaphoreSlim(Math.Max(1, parallelism));
        var tasks = items.Select((item, index) => RunBoundedItemAsync(item, index, results, semaphore, operation, cancellationToken)).ToArray();

        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task RunBoundedItemAsync<TSource, TResult>(
        TSource item,
        int index,
        TResult[] results,
        SemaphoreSlim semaphore,
        Func<TSource, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            results[index] = await operation(item, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
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