using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using TeslaScrape;
using System.Net;

var builder = Host.CreateApplicationBuilder(args);

// Configuration ekle
builder.Services.Configure<TeslaWatcherSettings>(
    builder.Configuration.GetSection("TeslaWatcher"));

// Services ekle
// HttpClient ile GZIP decompression ekle
builder.Services.AddHttpClient<Worker>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });
builder.Services.AddHostedService<Worker>();

// Logging ekle
builder.Services.AddLogging(config =>
{
    config.AddConsole();
    config.AddDebug();
});

var host = builder.Build();
await host.RunAsync();
