using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using TeslaScrape;

var builder = Host.CreateDefaultBuilder(args); // CreateApplicationBuilder → CreateDefaultBuilder

builder.ConfigureServices(services =>
{
    //services.Configure<TeslaWatcherSettings>(
    //    builder.Build().Services.GetRequiredService<IConfiguration>().GetSection("TeslaWatcherSettings"));

    services.AddHttpClient<Worker>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        });

    services.AddHostedService<Worker>();
});

var host = builder.Build();
await host.RunAsync();