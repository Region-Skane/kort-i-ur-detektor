using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using KortIUrWork;

namespace KortIUrDetektorApp;
class Program
{
    static async Task Main()
    {
        bool normalExit = false;
        while (!normalExit)
        {
            try
            {
                await RunApplication();
                normalExit = true;
                break; // Normal exit - don't restart
            }
            catch (Exception)
            {
                await Task.Delay(10000);
            }
        }
    }

    static async Task RunApplication()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var kortIUrConf = configuration.GetSection("KortIUrConf").Get<KortIUrConf>();
        LogLevel logLevel = configuration.GetValue<LogLevel>("Logging:EventLog:LogLevel:Default");

        KortIUrDetektor kortIUrDetektor = new KortIUrDetektor(kortIUrConf, logLevel);
        kortIUrDetektor.InitDetektor();

        // Handle ProcessExit (Task Manager end task, system shutdown)
        AppDomain.CurrentDomain.ProcessExit += kortIUrDetektor.CurrentDomain_ProcessExit;

        try
        {
            // Pass the cancellation token to the background method
            await Task.Run(() => kortIUrDetektor.BackgroundTaskMethod(cancellationTokenSource.Token), 
                          cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            KortIUrDetektor._logger?.LogError(ex, "KortIUrDetektor Application exception: {Message}", ex.Message);
            throw;
        }
    }
}