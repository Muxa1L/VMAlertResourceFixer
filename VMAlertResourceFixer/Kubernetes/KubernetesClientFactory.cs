using k8s;
using VMAlertResourceFixer.Options;

namespace VMAlertResourceFixer.Kubernetes;

internal static class KubernetesClientFactory
{
    public static IKubernetes Create(AppOptions options)
    {
        KubernetesClientConfiguration config;

        if (!string.IsNullOrWhiteSpace(options.KubeConfigPath) || !string.IsNullOrWhiteSpace(options.Context))
        {
            config = KubernetesClientConfiguration.BuildConfigFromConfigFile(
                kubeconfigPath: options.KubeConfigPath,
                currentContext: options.Context);
        }
        else if (KubernetesClientConfiguration.IsInCluster())
        {
            config = KubernetesClientConfiguration.InClusterConfig();
        }
        else
        {
            config = KubernetesClientConfiguration.BuildDefaultConfig();
        }

        return new k8s.Kubernetes(config);
    }
}