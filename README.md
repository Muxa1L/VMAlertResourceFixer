# VMAlertResourceFixer

`VMAlertResourceFixer` is a .NET 8 console tool that scans VictoriaMetrics `VMAlert` custom resources and updates `spec.resources.requests` from the current resource usage of their running pods.

It reads live pod usage from the Kubernetes local Metrics API served by [metrics-server](https://github.com/kubernetes-sigs/metrics-server), then patches the matching `VMAlert` CRDs in `operator.victoriametrics.com/v1beta1`.

## What it does

- discovers `VMAlert` resources
- resolves the operator-created `Deployment` named `vmalert-<name>`
- reads the current usage of its running pods from `metrics.k8s.io/v1beta1`
- takes the highest current pod usage per `VMAlert`
- applies configurable CPU and memory headroom
- rounds the result to friendly request values
- patches `spec.resources.requests.cpu` and `spec.resources.requests.memory`

By default the tool runs in dry-run mode.

## Defaults

- CPU headroom: `1.25`
- Memory headroom: `1.25`
- Minimum CPU request: `50m`
- Minimum memory request: `64Mi`
- CPU rounding step: `25m`
- Memory rounding step: `16Mi`

## Usage

Dry-run all `VMAlert` resources:

```powershell
dotnet run --project .\VMAlertResourceFixer\VMAlertResourceFixer.csproj --
```

Apply changes for one namespace:

```powershell
dotnet run --project .\VMAlertResourceFixer\VMAlertResourceFixer.csproj -- --apply --namespace monitoring
```

Apply changes for one `VMAlert`:

```powershell
dotnet run --project .\VMAlertResourceFixer\VMAlertResourceFixer.csproj -- --apply --namespace monitoring --name alerts-main
```

Use a custom kubeconfig and more aggressive headroom:

```powershell
dotnet run --project .\VMAlertResourceFixer\VMAlertResourceFixer.csproj -- --apply --kubeconfig C:\Users\me\.kube\config --context dev --cpu-headroom 1.5 --memory-headroom 1.4
```

## Required cluster permissions

The tool needs permission to:

- `get`, `list`, `patch` `vmalerts.operator.victoriametrics.com`
- `get`, `list` `deployments.apps`
- `get`, `list` `pods`
- `get`, `list` `pods.metrics.k8s.io`

A sample manifest is provided in [deploy/rbac.yaml](deploy/rbac.yaml).

## Container image

Build the container image from [VMAlertResourceFixer/Dockerfile](VMAlertResourceFixer/Dockerfile):

```powershell
docker build -t vmalert-resource-fixer:local .\VMAlertResourceFixer
```

A sample scheduled deployment is provided in [deploy/cronjob.yaml](deploy/cronjob.yaml).

## Build

```powershell
dotnet build .\VMAlertResourceFixer.sln
```

## Notes

- The tool only updates resource **requests**.
- It expects the VictoriaMetrics operator deployment naming convention: `vmalert-<name>`.
- It uses current metrics, so results are only as stable as the current workload snapshot.
- `metrics-server` is intended for autoscaling-style current usage data, not long-term capacity planning.