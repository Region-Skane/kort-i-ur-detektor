using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PCSC.Exceptions;
using PCSC.Monitoring;
using PCSC.Utils;
using PCSC;

namespace KortIUrWork;
class KortIUrDetektor
{
    private static bool isRunning = true;
    public static ILogger<KortIUrDetektor> _logger;
    private readonly KortIUrConf config;
    private IDeviceMonitor? deviceMonitor = null;
    private ISCardMonitor? _monitor = null;
    private string[]? _readerNames = null;
    private bool[]? _cardExistInReader = { false };
    private string _lastAttachedReader = String.Empty;

    public KortIUrDetektor(KortIUrConf config, LogLevel logLevel)
    {
        var loggerFactory = LoggerFactory.Create(
            builder => builder
                        .AddEventLog(new Microsoft.Extensions.Logging.EventLog.EventLogSettings { SourceName = "KortIUrDetektor" })
                        .SetMinimumLevel(logLevel)
        );

        _logger = loggerFactory.CreateLogger<KortIUrDetektor>();
        this.config = config;
        _logger.LogDebug("KortIUrDetektor constructor done");
    }

    public void InitDetektor()
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor initiated at: {time} ", DateTimeOffset.Now);

            deviceMonitor = DeviceMonitorFactory.Instance.Create(SCardScope.System);

            // Start card monitoring here.
            deviceMonitor.Initialized += OnInitialized;
            deviceMonitor.StatusChanged += OnStatusChanged;
            deviceMonitor.MonitorException += OnMonitorException;

            deviceMonitor.Start();

            // Retrieve the names of all installed readers.
            _readerNames = GetReaderNames();
            if (IsEmpty(_readerNames))
            {
                _logger.LogDebug("There are currently no readers attached.");
            }
            else
            {
                InitializeCardReaderMonitor();
                ShowUserInfo(_readerNames);
                _cardExistInReader = new bool[_readerNames.Length];
                _monitor?.Start(_readerNames);
            }

            _logger.LogInformation("KortIUrConf: KortUrCommandApp: {kortUrApp}, KortUrCommandAppArgs: {kortUrAppArgs}", config.KortUrCommandApp, config.KortUrCommandAppArgs);
            _logger.LogInformation("KortIUrConf: KortICommandApp: {kortIApp}, KortICommandAppArgs: {kortIAppArgs}", config.KortICommandApp, config.KortICommandAppArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in StartAsync(): {Message}", ex.Message);
        }
    }



    public void CurrentDomain_ProcessExit(object sender, EventArgs e)
    {
        // Handle the Cancel event
        _logger.LogDebug("Cancel triggered. Starting shut down...");

        // Set the flag to stop the background processing
        isRunning = false;

        // Shut down stuff and clean up

        try
        {
            var stopWatch = Stopwatch.StartNew();

            _logger.LogInformation("KortIUrDetektor: Stop called at: {time}", DateTimeOffset.Now);

            ShutdownCardReaderMonitor();

            if (deviceMonitor != null)
            {
                deviceMonitor.Initialized -= OnInitialized;
                deviceMonitor.StatusChanged -= OnStatusChanged;
                deviceMonitor.MonitorException -= OnMonitorException;
            }

            _logger.LogDebug("KortIUrDetektor took {ms} ms to stop.", stopWatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception in exit: {Message}", ex.Message);
        }
    }

    public void BackgroundTaskMethod()
    {
        // Background processing logic goes here
        _logger.LogDebug("Background processing started.");
        while (isRunning)
        {
            // some work
            Thread.Sleep(2000);
        }

        _logger.LogDebug("Background processing stopped.");
    }


    // ----------- Code for card monitoring -----------

    private void OnMonitorException(object sender, DeviceMonitorExceptionEventArgs args)
    {
        _logger.LogInformation($"Exception in OnMonitorException: {args.Exception}");
    }

    private void OnStatusChanged(object sender, DeviceChangeEventArgs e)
    {
        _logger.LogDebug("OnStatusChange called");
        try
        {
            bool anyDetached = false;
            foreach (var removed in e.DetachedReaders)
            {
                anyDetached = true;
                var index = 0;
                if (_readerNames != null) index = Array.IndexOf(_readerNames, removed);
                if (index >= 0)
                {
                    if ((_cardExistInReader != null && _cardExistInReader.Length > 0) && _cardExistInReader[index])
                    {
                        _logger.LogInformation("OnStatusChanged: There was a card present in the reader which was removed -> CardRemoved event!");
                        RunKortUrCommand();
                        _cardExistInReader[index] = false;
                    }
                }
            }

            bool anyAttached = false;
            _lastAttachedReader = String.Empty;
            foreach (var added in e.AttachedReaders)
            {
                _logger.LogDebug($"OnStatusChanged: New reader attached: {added}");
                _lastAttachedReader = added;
                anyAttached = true;
            }

            if (anyAttached || anyDetached)
            {
                _monitor?.Cancel();
                _readerNames = GetReaderNames();
                if (IsEmpty(_readerNames))
                {
                    _cardExistInReader = new bool[0];
                    ShutdownCardReaderMonitor();
                }
                else
                {
                    _cardExistInReader = new bool[_readerNames.Length];
                    if (_monitor == null)
                    {
                        InitializeCardReaderMonitor();
                    }
                    _monitor?.Start(_readerNames);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in OnStatusChanged(): {Message}", ex.Message);
        }
    }

    private void OnInitialized(object sender, DeviceChangeEventArgs e)
    {
        _logger.LogDebug("OnInitialized called");
        foreach (var name in e.AllReaders)
        {
            _logger.LogDebug("OnInitialized: Connected reader: {Name}", name);
        }
    }

    private void ShowUserInfo(IEnumerable<string> readerNames)
    {
        foreach (var reader in readerNames)
        {
            _logger.LogDebug($"Start monitoring reader: {reader}");
        }
    }

    private void InitializeCardReaderMonitor()
    {
        try
        {
            _monitor = MonitorFactory.Instance.Create(SCardScope.System);
            AttachToAllEvents(_monitor);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Exception in InitializeCardReaderMonitor: {Ex}", exception);
        }
    }

    private void ShutdownCardReaderMonitor()
    {
        try
        {
            if (_monitor != null)
            {
                _monitor.Cancel();
                DetachFromAllEvents(_monitor);
                _monitor.Dispose();
            }
            _monitor = null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Exception in ShutdownCardReaderMonitor: {Ex}", exception);
        }
    }

    private void AttachToAllEvents(ISCardMonitor monitor)
    {
        _logger.LogDebug("AttachToAllEvents called");
        // Point the callback function(s) to the anonymous defined methods below.
        monitor.CardInserted += (sender, args) => DisplayCardInsertedEvent("CardInserted", args);
        monitor.CardRemoved += (sender, args) => DisplayCardRemovedEvent("CardRemoved", args);
        monitor.Initialized += (sender, args) => DisplayInitializedEvent("Initialized", args);
        monitor.MonitorException += MonitorException;
    }

    private void DetachFromAllEvents(ISCardMonitor monitor)
    {
        _logger.LogDebug("DetachFromAllEvents called");
        // Point the callback function(s) to the anonymous defined methods below.
        monitor.CardInserted -= (sender, args) => DisplayCardInsertedEvent("CardInserted", args);
        monitor.CardRemoved -= (sender, args) => DisplayCardRemovedEvent("CardRemoved", args);
        monitor.Initialized -= (sender, args) => DisplayInitializedEvent("Initialized", args);
        monitor.MonitorException -= MonitorException;
    }

    private void DisplayCardInsertedEvent(string eventName, CardStatusEventArgs args)
    {
        _logger.LogInformation("DisplayCardInsertedEvent: Event: {EventName} , for reader: {ReaderName} , state: {State}", eventName, args.ReaderName, args.State);
        try
        {
            RunKortICommand();
            if (_readerNames != null && _cardExistInReader != null)
            {
                var index = Array.IndexOf(_readerNames, args.ReaderName);
                if (index >= 0 && _cardExistInReader.Length > index)
                {
                    _cardExistInReader[index] = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in DisplayCardInsertedEvent(): {Message}", ex.Message);
        }
    }

    private void DisplayCardRemovedEvent(string eventName, CardStatusEventArgs args)
    {
        _logger.LogInformation("DisplayCardRemovedEvent: Event: {EventName} , for reader: {ReaderName} , state: {State}", eventName, args.ReaderName, args.State);
        try
        {
            RunKortUrCommand();
            if (_readerNames != null && _cardExistInReader != null)
            {
                var index = Array.IndexOf(_readerNames, args.ReaderName);
                if (index >= 0 && _cardExistInReader.Length > index)
                {
                    _cardExistInReader[index] = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in DisplayCardRemovedEvent(): {Message}", ex.Message);
        }
    }

    private void DisplayInitializedEvent(string eventName, CardStatusEventArgs args)
    {
        _logger.LogInformation("DisplayInitializedEvent: Event: {EventName} , for reader: {ReaderName} , state: {State}", eventName, args.ReaderName, args.State);

        try
        {
            if (((args.State & SCRState.Present) == SCRState.Present) && _lastAttachedReader == args.ReaderName)
            {
                _logger.LogInformation("DisplayInitializedEvent: reader initialized with card present in it -> CardInserted event!");
                RunKortICommand();
                if (_readerNames != null && _cardExistInReader != null)
                {
                    var index = Array.IndexOf(_readerNames, args.ReaderName);
                    if (index >= 0 && _cardExistInReader.Length > index)
                    {
                        _cardExistInReader[index] = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in DisplayInitializedEvent(): {Message}", ex.Message);
        }
    }

    private void MonitorException(object sender, PCSCException ex)
    {
        _logger.LogInformation("MonitorException: Monitor exited due to an error: {ErrorText}", SCardHelper.StringifyError(ex.SCardError));
        try
        {
            _monitor?.Cancel();
            _monitor = null;
        }
        catch (Exception exception)
        {
            _logger.LogInformation("Exception when canceling _monitor:  {Ex}", exception);
        }
    }

    private static string[] GetReaderNames()
    {
        using var context = ContextFactory.Instance.Establish(SCardScope.System);
        return context.GetReaders();
    }

    private static bool IsEmpty(ICollection<string> readerNames) => readerNames == null || readerNames.Count < 1;

    private void RunKortICommand()
    {
        try
        {
            if (!string.IsNullOrEmpty(config.KortICommandApp))
            {
                RunWindowsCmdCommand(config.KortICommandApp, config.KortICommandAppArgs);
                _logger.LogInformation("RunKortICommand executed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in RunKortICommand(): {Message}", ex.Message);
        }
    }

    private void RunKortUrCommand()
    {
        try
        {
            if (!string.IsNullOrEmpty(config.KortUrCommandApp))
            {
                RunWindowsCmdCommand(config.KortUrCommandApp, config.KortUrCommandAppArgs);
                _logger.LogInformation("RunKortUrCommand executed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception in RunKortUrCommand(): {Message}", ex.Message);
        }
    }

    public void RunWindowsCmdCommand(string commandApp, string commandAppArgs, string? directory = null)
    {
        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = commandApp,
                UseShellExecute = false,
                // (default values are fine) RedirectStandardOutput = true,
                // (default values are fine) RedirectStandardError = true,
                // (default values are fine) RedirectStandardInput = true,
                Arguments = commandAppArgs,
                CreateNoWindow = true,
                // (default values are fine) WorkingDirectory = directory ?? string.Empty,
            }
        };
        process.Start();
    }
}