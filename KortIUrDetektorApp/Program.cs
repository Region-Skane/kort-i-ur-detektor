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
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
 
        var kortIUrConf = configuration.GetSection("KortIUrConf").Get<KortIUrConf>();

        LogLevel logLevel = configuration.GetValue<LogLevel>("Logging:EventLog:LogLevel:Default");

        KortIUrDetektor kortIUrDetektor = new KortIUrDetektor(kortIUrConf, logLevel);
        kortIUrDetektor.InitDetektor();

        AppDomain.CurrentDomain.ProcessExit += kortIUrDetektor.CurrentDomain_ProcessExit;

        // Start the background task
        Task backgroundTask = Task.Run(() => kortIUrDetektor.BackgroundTaskMethod());

        // Wait for the task to complete or for the user to press Ctrl+C
        await backgroundTask;
    }
}