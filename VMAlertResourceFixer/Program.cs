using VMAlertResourceFixer.Kubernetes;
using VMAlertResourceFixer.Options;
using VMAlertResourceFixer.Services;

try
{
	var options = AppOptions.Parse(args);
	if (options.ShowHelp)
	{
		AppOptions.PrintHelp();
		return 0;
	}

	using var kubernetes = KubernetesClientFactory.Create(options);
	var service = new VMAlertResourceFixService(kubernetes, options);
	var exitCode = await service.RunAsync();
	return exitCode;
}
catch (ArgumentException ex)
{
	Console.Error.WriteLine(ex.Message);
	Console.Error.WriteLine();
	AppOptions.PrintHelp();
	return 2;
}
catch (Exception ex)
{
	Console.Error.WriteLine($"Fatal error: {ex.Message}");
	return 1;
}
