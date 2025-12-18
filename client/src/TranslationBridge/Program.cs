using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TranslationBridge.Services;
using TranslationBridge.Configuration;

namespace TranslationBridge;

public class Program
{
    public static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File("logs/translation-bridge-.log", 
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            Log.Information("===========================================");
            Log.Information("3CX Bidirectional Translation Bridge");
            Log.Information("Powered by SeamlessM4T v2");
            Log.Information("===========================================");
            
            var host = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "3CX Translation Bridge";
                })
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    // Configuration
                    services.Configure<BridgeConfig>(
                        context.Configuration.GetSection("TranslationBridge"));
                    
                    // Audio bridge (singleton)
                    services.AddSingleton<AudioBridge>();
                    
                    // Worker (creates its own WebSocket clients)
                    services.AddHostedService<TranslationWorker>();
                })
                .Build();

            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
