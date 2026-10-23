using Microsoft.Extensions.Hosting;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Service;

if (args.Contains("--version"))
{
    Console.WriteLine($"Private Gateway Service {ServiceVersion.Current} (IPC v{ContractMapping.ApiVersion})");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
using var host = builder.Build();
await host.RunAsync();
return 0;
