using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PCSC.Exceptions;
using PCSC.Monitoring;
using PCSC.Utils;
using PCSC;
using System.Management;
using Microsoft.Win32;
using System.Reflection.PortableExecutable;
using System.Collections.Concurrent;
using System.Reflection.Metadata;


namespace KortIUrWork;
class KortIUrDetektor
{
    private static bool _isRunning = true;
    private static readonly object _isRunningLock = new object();
    public static ILogger<KortIUrDetektor> _logger;
    private readonly KortIUrConf config;
    private IDeviceMonitor? _deviceMonitor = null;
    private ISCardMonitor? _monitor = null;
    private static readonly object _monitorLock = new object();
    IntPtr _powerRegistrationHandle;
    IntPtr _pRecipient;
    GCHandle _handleForThisClass;
    private DeviceNotifyCallbackRoutine? _powerCallback;
    private string[]? _readerNames = null;
    private ConcurrentDictionary<string, bool> _readersCardExists;
    private ConcurrentDictionary<string, bool> _newlyAttachedReaders;

    public KortIUrDetektor(KortIUrConf config, LogLevel logLevel)
    {
        var loggerFactory = LoggerFactory.Create(
            builder => builder
                        .AddEventLog(new Microsoft.Extensions.Logging.EventLog.EventLogSettings { SourceName = "Application" })
                        .SetMinimumLevel(logLevel)
        );

        CreateLogger(loggerFactory.CreateLogger<KortIUrDetektor>());

        this.config = config;
        _readersCardExists = new ConcurrentDictionary<string, bool>();
        _newlyAttachedReaders = new ConcurrentDictionary<string, bool>();
        _logger.LogDebug("KortIUrDetektor constructor done");
    }

    private static void CreateLogger(ILogger<KortIUrDetektor> logger)
    {
        _logger = logger;
    }

    public void InitDetektor()
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor initiated at: {time} ", DateTimeOffset.Now);

            RegisterForPowerEventNotifications();

            StartMonitoring();

            _logger.LogInformation("KortIUrDetektor: KortIUrConf: KortUrCommandApp: {kortUrApp}, KortUrCommandAppArgs: {kortUrAppArgs}", config.KortUrCommandApp, config.KortUrCommandAppArgs);
            _logger.LogInformation("KortIUrDetektor: KortIUrConf: KortICommandApp: {kortIApp}, KortICommandAppArgs: {kortIAppArgs}", config.KortICommandApp, config.KortICommandAppArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KortIUrDetektor: Exception in StartAsync(): {Message}", ex.Message);
        }
    }



    public void CurrentDomain_ProcessExit(object sender, EventArgs e)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: ProcessExit triggered. Starting shut down...");

            // Set the flag to stop the background processing
            StopBackgroundProcessing();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KortIUrDetektor: Exception in exit: {Message}", ex.Message);
        }
    }

    private static void StopBackgroundProcessing()
    {
        lock (_isRunningLock)
        {
            _isRunning = false;
        }
    }

    public void BackgroundTaskMethod(CancellationToken cancellationToken = default)
    {
        try
        {
            // Background processing logic goes here
            _logger.LogDebug("KortIUrDetektor: Background processing started.");
            bool isRunning = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_isRunningLock)
                {
                    isRunning = _isRunning;
                    if (!isRunning) break;
                }
                // some work
                cancellationToken.WaitHandle.WaitOne(2000);
            }
            StopMonitoring();
            UnRegisterFromPowerEventNotifications();
            _logger.LogInformation("KortIUrDetektor: Background processing stopped.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("KortIUrDetektor: Background processing was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KortIUrDetektor: Exception in BackgroundTaskMethod(): {Message}", ex.Message);
            StopMonitoring();
            UnRegisterFromPowerEventNotifications();
            throw;
        }
    }


    // ----------- Code for card monitoring -----------

    private void OnMonitorException(object sender, DeviceMonitorExceptionEventArgs args)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: Exception in OnMonitorException: {Ex}", args.Exception);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in OnMonitorException(): {Message}", ex.Message);
        }
    }

    private void OnStatusChanged(object sender, DeviceChangeEventArgs e)
    {
        try
        {
            _logger.LogDebug("KortIUrDetektor: OnStatusChange called");

            bool anyDetached = false;
            foreach (var removed in e.DetachedReaders)
            {
                anyDetached = true;
                _logger.LogInformation("KortIUrDetektor: OnStatusChanged: Reader removed: {removed}", removed);

                if (_readersCardExists.TryGetValue(removed, out bool cardPresent)  && cardPresent)
                {
                    _logger.LogInformation("KortIUrDetektor: OnStatusChanged: There was a card present in the reader which was removed -> CardRemoved event!");
                    RunKortUrCommand();
                }

                _newlyAttachedReaders.TryRemove(removed, out _);
                _readersCardExists.TryRemove(removed, out _);
            }

            bool anyAttached = false;
            foreach (var added in e.AttachedReaders)
            {
                _logger.LogInformation("KortIUrDetektor: OnStatusChanged: New reader attached: {added}", added);
                if (!_newlyAttachedReaders.TryAdd(added, true))
                {
                    _newlyAttachedReaders[added] = true;
                }

                if (!_readersCardExists.TryAdd(added, false))
                {
                    _readersCardExists[added] = false;
                }

                anyAttached = true;
            }

            if (anyAttached || anyDetached)
            {
                _readerNames = e.AllReaders.ToArray();
                RestartCardReaderMonitor(_readerNames);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in OnStatusChanged(): {Message}", ex.Message);
        }
    }

    private void OnInitialized(object sender, DeviceChangeEventArgs e)
    {
        try
        {
            _logger.LogDebug("KortIUrDetektor: OnInitialized called");
            foreach (var name in e.AllReaders)
            {
                _logger.LogDebug("KortIUrDetektor: OnInitialized: Connected reader: {Name}", name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in OnInitialized(): {Message}", ex.Message);
        }
    }

    private static void LogCardReaderNames(IEnumerable<string> readerNames)
    {
        foreach (var reader in readerNames)
        {
            _logger.LogDebug("KortIUrDetektor: Start monitoring reader: {reader}", reader);
        }
    }

    private void InitializeCardReaderMonitor()
    {
        try
        {
            lock (_monitorLock)
            {
                if (_monitor == null)
                {
                    _monitor = MonitorFactory.Instance.Create(SCardScope.System);
                    AttachToAllEvents(_monitor);
                }
                else
                {
                    _logger.LogInformation("KortIUrDetektor: InitializeCardReaderMonitor: Monitor already initialized.");
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning("KortIUrDetektor: Exception in InitializeCardReaderMonitor: {Ex}", exception);
        }
    }

    private void RestartCardReaderMonitor(string[] readerNames)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: RestartCardReaderMonitor: due to attached or detached readers.");
            lock (_monitorLock)
            {
                if (_monitor != null)
                {
                    _monitor?.Cancel();
                    if (IsEmpty(readerNames))
                    {
                        _logger.LogDebug("KortIUrDetektor: RestartCardReaderMonitor: There are currently no readers attached.");
                    }
                    else
                    {
                        LogCardReaderNames(readerNames);
                        _monitor?.Start(readerNames);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning("KortIUrDetektor: Exception in RestartCardReaderMonitor: {Ex}", exception);
        }
    }

    private void ShutdownCardReaderMonitor()
    {
        try
        {
            lock (_monitorLock)
            {
                if (_monitor != null)
                {
                    _monitor.Cancel();
                    DetachFromAllEvents(_monitor);
                    _monitor.Dispose();
                }
                _monitor = null;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning("KortIUrDetektor: Exception in ShutdownCardReaderMonitor: {Ex}", exception);
        }
    }

    private void AttachToAllEvents(ISCardMonitor monitor)
    {
        _logger.LogDebug("KortIUrDetektor: AttachToAllEvents called");
        // Point the callback function(s) to the anonymous defined methods below.
        monitor.CardInserted += (sender, args) => DisplayCardInsertedEvent("CardInserted", args);
        monitor.CardRemoved += (sender, args) => DisplayCardRemovedEvent("CardRemoved", args);
        monitor.Initialized += (sender, args) => DisplayInitializedEvent("Initialized", args);
        monitor.MonitorException += MonitorException;
    }

    private void DetachFromAllEvents(ISCardMonitor monitor)
    {
        _logger.LogDebug("KortIUrDetektor: DetachFromAllEvents called");
        // Point the callback function(s) to the anonymous defined methods below.
        monitor.CardInserted -= (sender, args) => DisplayCardInsertedEvent("CardInserted", args);
        monitor.CardRemoved -= (sender, args) => DisplayCardRemovedEvent("CardRemoved", args);
        monitor.Initialized -= (sender, args) => DisplayInitializedEvent("Initialized", args);
        monitor.MonitorException -= MonitorException;
    }

    private void StartMonitoring()
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: StartMonitoring called");
            // Start card monitoring here.
            _deviceMonitor = DeviceMonitorFactory.Instance.Create(SCardScope.System);
            _deviceMonitor.Initialized += OnInitialized;
            _deviceMonitor.StatusChanged += OnStatusChanged;
            _deviceMonitor.MonitorException += OnMonitorException;

            _deviceMonitor.Start();

            _newlyAttachedReaders.Clear();
            _readersCardExists.Clear();

            InitializeCardReaderMonitor();

            // Retrieve the names of all installed readers.
            _readerNames = GetReaderNames();
            if (IsEmpty(_readerNames))
            {
                _logger.LogDebug("KortIUrDetektor: There are currently no readers attached.");
            }
            else
            {
                _readersCardExists = new ConcurrentDictionary<string, bool>(_readerNames.ToDictionary(name => name, name => false));
                LogCardReaderNames(_readersCardExists.Keys);
                lock (_monitorLock)
                {
                    _monitor?.Start(_readerNames);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in StartMonitoring(): {Message}", ex.Message);
        }
    }
    private void StopMonitoring()
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: StopMonitoring called at: {time}", DateTimeOffset.Now);

            ShutdownCardReaderMonitor();

            if (_deviceMonitor != null)
            {
                _deviceMonitor.Initialized -= OnInitialized;
                _deviceMonitor.StatusChanged -= OnStatusChanged;
                _deviceMonitor.MonitorException -= OnMonitorException;
                _deviceMonitor.Dispose();
                _deviceMonitor = null;
            }
            _readerNames = null;

            _newlyAttachedReaders.Clear();
            _readersCardExists.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in StopMonitoring(): {Message}", ex.Message);
        }
    }

    private void DisplayCardInsertedEvent(string eventName, CardStatusEventArgs args)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: DisplayCardInsertedEvent: Event: {EventName} , for reader: {ReaderName} , state: {State}", eventName, args.ReaderName, args.State);

            RunKortICommand();
            _readersCardExists?.TryUpdate(args.ReaderName, true, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in DisplayCardInsertedEvent(): {Message}", ex.Message);
        }
    }

    private void DisplayCardRemovedEvent(string eventName, CardStatusEventArgs args)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: DisplayCardRemovedEvent: Event: {EventName} , for reader: {ReaderName} , state: {State}", eventName, args.ReaderName, args.State);

            RunKortUrCommand();
            _readersCardExists?.TryUpdate(args.ReaderName, false, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in DisplayCardRemovedEvent(): {Message}", ex.Message);
        }
    }

    private void DisplayInitializedEvent(string eventName, CardStatusEventArgs args)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: DisplayInitializedEvent: Event: {EventName} , for reader: {ReaderName} , state: {State}", eventName, args.ReaderName, args.State);

            if ((args.State & SCRState.Present) == SCRState.Present)
            {
                if (_newlyAttachedReaders!= null && _newlyAttachedReaders.TryGetValue(args.ReaderName, out bool newlyAttached) && newlyAttached)
                {
                    _logger.LogInformation("KortIUrDetektor: DisplayInitializedEvent: reader initialized with card present in it -> CardInserted event!");
                    RunKortICommand();
                }
                
                _readersCardExists?.TryUpdate(args.ReaderName, true, false);
            }
            else
            {
                _readersCardExists?.TryUpdate(args.ReaderName, false, true);
            }

            _newlyAttachedReaders?.TryUpdate(args.ReaderName, false, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in DisplayInitializedEvent(): {Message}", ex.Message);
        }
    }

    private void MonitorException(object sender, PCSCException ex)
    {
        try
        {
            _logger.LogInformation("KortIUrDetektor: MonitorException: Monitor exited due to an error: {ErrorText}", SCardHelper.StringifyError(ex.SCardError));
            
            _monitor?.Cancel();
        }
        catch (Exception exception)
        {
            _logger.LogInformation("KortIUrDetektor: Exception when canceling _monitor:  {Ex}", exception);
        }
    }

    private bool AnyCardExistsInAnyReader()
    {
        return _readersCardExists != null && _readersCardExists.Values.Any(x => x);
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
                _logger.LogInformation("KortIUrDetektor: RunKortICommand executed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in RunKortICommand(): {Message}", ex.Message);
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
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in RunKortUrCommand(): {Message}", ex.Message);
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

    private void RegisterForPowerEventNotifications()
    {
        try
        {
            _handleForThisClass = GCHandle.Alloc(this);
            _powerRegistrationHandle = new IntPtr();
            DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS();
            _powerCallback = new DeviceNotifyCallbackRoutine(DeviceNotifyCallback);
            recipient.Callback = _powerCallback;
            recipient.Context = GCHandle.ToIntPtr(_handleForThisClass);

            _pRecipient = Marshal.AllocHGlobal(Marshal.SizeOf(recipient));
            Marshal.StructureToPtr(recipient, _pRecipient, false);

            uint result = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, ref recipient, ref _powerRegistrationHandle);

            if (result != 0)
                _logger.LogInformation("KortIUrDetektor: Error registering for power notifications: {err}", Marshal.GetLastWin32Error().ToString());
            else
                _logger.LogInformation("KortIUrDetektor: Successfully Registered for power notifications!");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in RegisterForPowerEventNotifications(): {Message}", ex.Message);
        }
    }

    private void UnRegisterFromPowerEventNotifications()
    {
        try
        {
            if (_powerRegistrationHandle != IntPtr.Zero)
            {
                uint result = PowerUnregisterSuspendResumeNotification(ref _powerRegistrationHandle);

                if (_handleForThisClass.IsAllocated)
                {
                    _handleForThisClass.Free();
                }

                Marshal.FreeHGlobal(_pRecipient);

                if (result != 0)
                    _logger.LogInformation("KortIUrDetektor: Error unregistering from power notifications, result: {result}", result.ToString());
                else
                    _logger.LogInformation("KortIUrDetektor: Successfully Unregistered from power notifications!");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in UnRegisterFromPowerEventNotifications(): {Message}", ex.Message);
        }
        finally
        {
            _powerCallback = null;
            _powerRegistrationHandle = IntPtr.Zero;
            _pRecipient = IntPtr.Zero;
            _handleForThisClass = default;
        }
    }

    private static int DeviceNotifyCallback(IntPtr context, int type, IntPtr setting)
    {
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(context);

            if (handle.Target == null)
            {
                return 0;
            }

            KortIUrDetektor instance = (KortIUrDetektor)handle.Target;

            _logger.LogInformation("KortIUrDetektor: got device notify power event of type: {Type}", type.ToString());

            switch (type)
            {
                case PBT_APMRESUMEAUTOMATIC:
                    _logger.LogInformation("KortIUrDetektor: Operation is resuming automatically from a low-power state.");
                    instance.StartMonitoring();
                    break;
                case PBT_APMSUSPEND:
                    _logger.LogInformation("KortIUrDetektor: System is suspending operation.");
                    if (instance.AnyCardExistsInAnyReader())
                    {
                        _logger.LogInformation("KortIUrDetektor: A card is present in a reader, running KortUrCommand");
                        instance.RunKortUrCommand();
                    }
                    instance.StopMonitoring();
                    break;
                default:
                    _logger.LogInformation("KortIUrDetektor: no action will be taken on this device notify power event (of type: {Type})", type.ToString());
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KortIUrDetektor: Exception in DeviceNotifyCallback(): {Message}", ex.Message);
        }

        return 0;
    }

    [DllImport("Powrprof.dll", SetLastError = true)]
    static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS receipient, ref IntPtr registrationHandle);
    [DllImport("Powrprof.dll", SetLastError = true)]
    static extern uint PowerUnregisterSuspendResumeNotification(ref IntPtr registrationHandle);

    private const int WM_POWERBROADCAST = 536; // (0x218)
    //private const int PBT_APMPOWERSTATUSCHANGE = 10; // (0xA) - Power status has changed.
    private const int PBT_APMRESUMEAUTOMATIC = 18; // (0x12) - Operation is resuming automatically from a low-power state.This message is sent every time the system resumes.
    //private const int PBT_APMRESUMESUSPEND = 7; // (0x7) - Operation is resuming from a low-power state.This message is sent after PBT_APMRESUMEAUTOMATIC if the resume is triggered by user input, such as pressing a key.
    private const int PBT_APMSUSPEND = 4; // (0x4) - System is suspending operation.
    //private const int PBT_POWERSETTINGCHANGE = 32787; // (0x8013) - A power setting change event has been received.
    private const int DEVICE_NOTIFY_CALLBACK = 2;

    /// <summary>
    /// OS callback delegate definition
    /// </summary>
    /// <param name="context">The context for the callback</param>
    /// <param name="type">The type of the callback...for power notifcation it's a PBT_ message</param>
    /// <param name="setting">A structure related to the notification, depends on type parameter</param>
    /// <returns></returns>
    delegate int DeviceNotifyCallbackRoutine(IntPtr context, int type, IntPtr setting);

    /// <summary>
    /// A callback definition
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }
}