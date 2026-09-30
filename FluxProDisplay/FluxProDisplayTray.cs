using System.Runtime.InteropServices;
using FluxProDisplay.DTOs.AppSettings;
using HidLibrary;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

namespace FluxProDisplay;

public partial class FluxProDisplayTray : Form
{
    private readonly HardwareMonitor _monitor;
    private readonly RootConfig _configuration;
    private ToolStripLabel? _connectionStatusLabel;
    private ToolStripLabel? _cpuTempDebugLabel;
    private ToolStripLabel? _gpuTempDebugLabel;
    private ToolStripMenuItem? _startupToggleMenuItem;
    private const string ElevatedTaskName = "FluxProDisplayElevatedTask";
    
    // app settings
    private readonly bool _debug;
    private readonly int _pollingInterval;
    private readonly int _vendorId;
    private readonly int _productId;

    // A single HID report must never be allowed to block the heartbeat loop:
    // writes run overlapped and are abandoned after this many milliseconds.
    private const int WriteTimeoutMs = 300;

    // Consecutive failed writes tolerated before the device handle is recycled.
    // A single rejected report is usually just transient USB contention, so the
    // same handle is retried first instead of reconnecting.
    private const int MaxConsecutiveWriteFailures = 3;

    // Extra time allowed for a cancelled transfer to report completion before we
    // stop waiting on it. If it still hasn't completed we deliberately leak the
    // OVERLAPPED block rather than hand freed memory back to the kernel.
    private const int CancelWaitMs = 500;

    // How long a device lookup may run before we log that it is blocked.
    private const int AttachStallWarningMs = 5_000;

    // Safety net: if a suspend notification is never followed by a resume (an
    // aborted sleep), writes must not stay parked indefinitely.
    private static readonly TimeSpan MaxSuspendHold = TimeSpan.FromMinutes(3);

    // other UI components for the tab
    private NotifyIcon _appStatusNotifyIcon = null!;
    private ContextMenuStrip _contextMenuStrip = null!;

    private PeriodicTimer? _pollTimer;
    private HidDevice? _device;
    private byte[]? _payload;

    // write health (accessed only from the update loop thread)
    private int _consecutiveWriteFailures;
    private bool _deviceMissingLogged;

    // device attach state (accessed only from the update loop thread). The lookup
    // runs on a worker thread: HidLibrary's enumeration opens each matching device
    // to read its attributes, which can block for a long time on a wedged device.
    private Task<HidDevice?>? _attachTask;
    private DateTime _attachStartedUtc;
    private bool _attachBlockedLogged;

    // last good/displayed temperatures (written by the sensor refresh task and
    // read by the heartbeat loop, so they must be volatile)
    private volatile float _lastCpuTemp;
    private volatile float _lastGpuTemp;

    // last connection state pushed to the UI
    private bool? _lastReportedConnected;

    // set by the power-mode event handler when the system resumes from sleep
    private volatile bool _resumeDetected;

    // set while the system is suspending/suspended. Writes are parked instead of
    // cancelled in this state: cancelling a transfer during a power transition
    // leaves the USB port in a state that can take hours to recover from.
    private volatile bool _suspended;
    private DateTime _suspendedSinceUtc;

    private readonly Icon _iconConnected = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon_connected.ico"));
    private readonly Icon _iconDisconnected = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon_disconnected.ico"));
    
    public FluxProDisplayTray(RootConfig configuration)
    {
        // check if iUnity is running to prevent conflicts before doing anything else
        PreflightChecks.CheckForIUnity();
        
        // check if PawnIO driver is installed.
        PreflightChecks.CheckForPawnIoDriver();
        
        InitializeComponent();
        
        _configuration = configuration;
        _monitor = new HardwareMonitor(configuration.AppSettings);
        
        // Resolve sensors using saved configuration before setting up UI
        _monitor.GetCpuTemperature();
        _monitor.GetGpuTemperature();
        
        Console.WriteLine($"After resolve: CPU={_monitor.GetSelectedCpuSensorName()}, GPU={_monitor.GetSelectedGpuSensorName()}");
        
        // initialize variables from config file for easier changing
        _debug = configuration.AppInfo.Debug;
        _pollingInterval = configuration.AppSettings.PollingInterval;
        _vendorId = configuration.AppSettings.VendorIdInt;
        _productId = configuration.AppSettings.ProductIdInt;
        
        SetUpTrayIcon();

        // proactively drop the stale HID handle when the machine wakes from sleep
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // If the "Start with Windows" task exists, re-register it with the current
        // executable path. This self-heals tasks that point at a stale location
        // after a move/republish, and re-enables tasks that Task Scheduler
        // disabled after failed runs.
        try
        {
            using (var taskService = new TaskService())
            {
                if (taskService.FindTask(ElevatedTaskName) != null)
                {
                    RegisterStartupTask(taskService);
                }
            }
        }
        catch (Exception ex)
        {
            // starting the app must never fail because the task refresh did
            Logger.LogError(new Exception("Failed to refresh the startup task", ex));
        }

        _ = WriteToDisplay().ContinueWith(
            t => Logger.LogError(t.Exception!),
            TaskContinuationOptions.OnlyOnFaulted);
    }
    
    private void SetUpTrayIcon()
    {
        _appStatusNotifyIcon = new NotifyIcon(components);
        _appStatusNotifyIcon.Visible = true;

        _contextMenuStrip = new ContextMenuStrip();

        var appNameLabel = new ToolStripLabel(AppMetadata.Name + " " + AppMetadata.Version);
        appNameLabel.ForeColor = Color.Gray;
        appNameLabel.Enabled = false;
        _contextMenuStrip.Items.Add(appNameLabel);
        
        // debug item that shows current temperature in menu strip
        if (_debug)
        {
            AddDebugMenuItems();
        }

        // connection status label, shown directly under the app name
        _connectionStatusLabel = new ToolStripLabel();
        _connectionStatusLabel.ForeColor = Color.Crimson;
        _connectionStatusLabel.Enabled = true;
        _contextMenuStrip.Items.Add(_connectionStatusLabel);

        // sensor selection menus
        AddSensorSelectionMenus();

        // menu items
        _startupToggleMenuItem = new ToolStripMenuItem();
        _startupToggleMenuItem.Click += StartupToggleMenuItemClicked;

        var quitMenuItem = new ToolStripMenuItem("Quit");
        quitMenuItem.Click += QuitMenuItem_Click!;

        // separator to separate
        _contextMenuStrip.Items.Add(new ToolStripSeparator());
        _contextMenuStrip.Items.Add(_startupToggleMenuItem);
        _contextMenuStrip.Items.Add(quitMenuItem);

        _appStatusNotifyIcon.ContextMenuStrip = _contextMenuStrip;

        UpdateStartupMenuItemText();

        _appStatusNotifyIcon.Icon = _iconDisconnected;
    }

    private void AddDebugMenuItems()
    {
        _contextMenuStrip.Items.Add(new ToolStripSeparator());
        var debugModeLabel = new ToolStripLabel("Debug Mode Active");
        debugModeLabel.ForeColor = Color.Gray;
        debugModeLabel.Enabled = false;
        _contextMenuStrip.Items.Add(debugModeLabel);
            
        _cpuTempDebugLabel = new ToolStripLabel("CPU Temp: 0°C");
        _cpuTempDebugLabel.ForeColor = Color.Gray;
        _cpuTempDebugLabel.Enabled = false;
        _contextMenuStrip.Items.Add(_cpuTempDebugLabel);
            
        _gpuTempDebugLabel = new ToolStripLabel("GPU Temp: 0°C");
        _gpuTempDebugLabel.ForeColor = Color.Gray;
        _gpuTempDebugLabel.Enabled = false;
        _contextMenuStrip.Items.Add(_gpuTempDebugLabel);
    }

    private void AddSensorSelectionMenus(int insertIndex = -1)
    {
        var separator = new ToolStripSeparator();
        
        // CPU Sensor Selection
        var cpuSensorMenu = new ToolStripMenuItem("CPU Sensor");
        var cpuSensors = _monitor.GetAvailableCpuSensors();
        var selectedCpuSensor = _monitor.GetSelectedCpuSensorName();
        
        foreach (var (name, sensor) in cpuSensors)
        {
            var item = new ToolStripMenuItem(name);
            item.Checked = (name == selectedCpuSensor);
            var capturedSensor = sensor;
            item.Click += (s, e) =>
            {
                _monitor.SetCpuSensor(capturedSensor);
                RefreshSensorMenus();
            };
            cpuSensorMenu.DropDownItems.Add(item);
        }
        
        // GPU Sensor Selection
        var gpuSensorMenu = new ToolStripMenuItem("GPU Sensor");
        var gpuSensors = _monitor.GetAvailableGpuSensors();
        var selectedGpuSensor = _monitor.GetSelectedGpuSensorName();
        
        foreach (var (name, sensor) in gpuSensors)
        {
            var item = new ToolStripMenuItem(name);
            item.Checked = (name == selectedGpuSensor);
            var capturedSensor = sensor;
            item.Click += (s, e) =>
            {
                _monitor.SetGpuSensor(capturedSensor);
                RefreshSensorMenus();
            };
            gpuSensorMenu.DropDownItems.Add(item);
        }
        
        if (insertIndex >= 0)
        {
            _contextMenuStrip.Items.Insert(insertIndex, separator);
            _contextMenuStrip.Items.Insert(insertIndex + 1, cpuSensorMenu);
            _contextMenuStrip.Items.Insert(insertIndex + 2, gpuSensorMenu);
        }
        else
        {
            _contextMenuStrip.Items.Add(separator);
            _contextMenuStrip.Items.Add(cpuSensorMenu);
            _contextMenuStrip.Items.Add(gpuSensorMenu);
        }
    }

    private void RefreshSensorMenus()
    {
        // Find and remove the sensor menus and separator
        var cpuMenuIndex = -1;
        
        for (int i = _contextMenuStrip.Items.Count - 1; i >= 0; i--)
        {
            if (_contextMenuStrip.Items[i] is ToolStripMenuItem mi && mi.Text == "CPU Sensor")
            {
                cpuMenuIndex = i;
                break;
            }
        }
        
        if (cpuMenuIndex > 0)
        {
            // Remove GPU Sensor menu, CPU Sensor menu, and separator
            _contextMenuStrip.Items.RemoveAt(cpuMenuIndex + 1); // GPU menu
            _contextMenuStrip.Items.RemoveAt(cpuMenuIndex);     // CPU menu
            _contextMenuStrip.Items.RemoveAt(cpuMenuIndex - 1); // Separator
            
            // Re-insert the sensor menus at their original position (after the app
            // name/debug labels) instead of appending them to the end of the menu.
            AddSensorSelectionMenus(cpuMenuIndex - 1);
            SaveConfiguration();
        }
    }

    private void SaveConfiguration()
    {
        try
        {
            var cpuName = _monitor.GetSelectedCpuSensorFullName();
            var gpuName = _monitor.GetSelectedGpuSensorFullName();
            
            Console.WriteLine($"[SAVE] CPU Sensor: {cpuName}");
            Console.WriteLine($"[SAVE] GPU Sensor: {gpuName}");

            // Persist to the per-user settings file rather than next to the executable.
            // The exe folder is not reliably writable (e.g. installed under Program Files)
            // and appsettings.json gets overwritten on every rebuild/publish.
            UserSettingsStore.Save(cpuName, gpuName);
            
            Console.WriteLine("[SAVE] Configuration saved successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SAVE] Error: {ex}");
            Logger.LogError(new Exception("Failed to save configuration", ex));
        }
    }

    private void StartupToggleMenuItemClicked(object? sender, EventArgs e)
    {
        using (var taskService = new TaskService())
        {
            var existingTask = taskService.FindTask(ElevatedTaskName);

            if (existingTask != null)
            {
                taskService.RootFolder.DeleteTask(ElevatedTaskName);
            }
            else
            {
                RegisterStartupTask(taskService);
            }
        }

        UpdateStartupMenuItemText();
    }

    /// <summary>
    /// Registers (or refreshes) the logon task that launches this app when the
    /// user signs in. Re-registering with CreateOrUpdate updates the action to
    /// the current executable path and re-enables a task that Task Scheduler may
    /// have disabled after failed runs.
    /// </summary>
    private void RegisterStartupTask(TaskService taskService)
    {
        var exePath = Application.ExecutablePath;

        var startupTask = taskService.NewTask();
        startupTask.RegistrationInfo.Description = "Flux Pro Display Service Task with Admin Privileges";
        startupTask.Principal.RunLevel = TaskRunLevel.Highest;
        startupTask.Principal.LogonType = TaskLogonType.InteractiveToken;

        // start as soon as the scheduler can if the logon trigger is missed, and
        // never let the default 72-hour execution limit kill a long-running task
        startupTask.Settings.Enabled = true;
        startupTask.Settings.StartWhenAvailable = true;
        startupTask.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        startupTask.Settings.DisallowStartIfOnBatteries = false;
        startupTask.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

        startupTask.Triggers.Add(new LogonTrigger());
        startupTask.Actions.Add(new ExecAction(exePath, null, Path.GetDirectoryName(exePath)));

        // the two-argument overload registers with CreateOrUpdate and the
        // definition's principal (current user + InteractiveToken), so it both
        // creates and refreshes the task
        taskService.RootFolder.RegisterTaskDefinition(ElevatedTaskName, startupTask);
    }

    private void UpdateStartupMenuItemText()
    {
        using var ts = new TaskService();
        var taskEnabled = ts.FindTask(ElevatedTaskName) != null;
        _startupToggleMenuItem!.Text = taskEnabled ? "✓ Start with Windows" : "Start with Windows";
    }

    private void QuitMenuItem_Click(object sender, EventArgs e)
    {
        Application.Exit();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            // stop issuing writes during the transition; an in-flight report that
            // gets interrupted here must not be treated as a device failure
            _suspendedSinceUtc = DateTime.UtcNow;
            _suspended = true;
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _resumeDetected = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _pollTimer?.Dispose();
            _device?.Dispose();
            _monitor.Dispose();
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Hides the main window on startup.
    /// </summary>
    /// <param name="value"></param>
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) {
            value = false;
            CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    /// <summary>
    /// Main heartbeat loop. Writes a keep-alive report to the display panel every
    /// polling tick using the last known temperatures, then refreshes sensor
    /// readings in the background. Sensor latency and USB write stalls are kept
    /// off the critical path so the panel never goes without a heartbeat (the
    /// panel flickers whenever writes are delayed or fail).
    /// </summary>
    private async Task WriteToDisplay()
    {
        // interval is in ms, set in appsettings.json
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval));

        // 0 = idle, 1 = sensor refresh running. Sensor reads can block for
        // hundreds of ms (LibreHardwareMonitor hardware updates), so they run on
        // their own task and are skipped while one is still in flight.
        var sensorRefreshBusy = 0;

        do
        {
            try
            {
                // system resumed from sleep: drop the stale handle and reconnect immediately
                if (_resumeDetected)
                {
                    _resumeDetected = false;
                    _suspended = false;
                    ForceReconnect("System resumed from sleep; forcing reconnect");
                }
                else if (_suspended && DateTime.UtcNow - _suspendedSinceUtc > MaxSuspendHold)
                {
                    // the resume event never arrived (aborted sleep) - drop the
                    // handle so the parked transfer is aborted and start over
                    _suspended = false;
                    ForceReconnect("Suspend state timed out; reconnecting");
                }

                // (re)connect as soon as the device is available; health is judged
                // by write success, not enumeration. The lookup runs on a worker
                // thread so a wedged device can never freeze the heartbeat loop.
                HandleDeviceAttach();

                // Heartbeat first: write the last known temperatures before any
                // slow work happens this tick. The panel treats these reports as
                // a keep-alive, so delayed writes make the display flicker.
                if (_device != null && !_suspended)
                {
                    var reportLength = _device.Capabilities.OutputReportByteLength;
                    if (_payload == null || _payload.Length != reportLength)
                    {
                        _payload = new byte[reportLength];
                        // constant report header; digits and checksum are rewritten each tick
                        _payload[0] = 0;
                        _payload[1] = 85;
                        _payload[2] = 170;
                        _payload[3] = 1;
                        _payload[4] = 1;
                        _payload[5] = 6;
                    }

                    FillPayload(_payload, _lastCpuTemp, _lastGpuTemp);

                    var writeResult = WriteHeartbeat(_payload);
                    if (writeResult == WriteResult.Success)
                    {
                        _consecutiveWriteFailures = 0;
                    }
                    else if (writeResult == WriteResult.Interrupted)
                    {
                        // the machine started suspending mid-write: the transfer is
                        // parked rather than cancelled, and the resume path drops
                        // the handle cleanly, so there is nothing to recover here
                    }
                    else if (writeResult == WriteResult.TimedOut)
                    {
                        // the device stopped ACKing the report; the pending
                        // transfer has already been cancelled, so recycle the
                        // handle and open a fresh connection next tick
                        DropDevice("Write stalled; reconnecting");
                    }
                    else
                    {
                        _consecutiveWriteFailures++;
                        LogConnection($"Write failed (failure #{_consecutiveWriteFailures})");
                        if (_consecutiveWriteFailures >= MaxConsecutiveWriteFailures)
                        {
                            DropDevice("Write failed repeatedly; reconnecting");
                        }
                    }
                }

                // Refresh temperatures AFTER the heartbeat so sensor latency can
                // only affect data freshness, never the write cadence.
                if (Interlocked.CompareExchange(ref sensorRefreshBusy, 1, 0) == 0)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var cpuTempRaw = _monitor.GetCpuTemperature();
                            var gpuTempRaw = _monitor.GetGpuTemperature();

                            // hold the last good reading whenever a sensor reports an invalid value
                            _lastCpuTemp = SanitizeTemperature(cpuTempRaw, _lastCpuTemp);
                            _lastGpuTemp = SanitizeTemperature(gpuTempRaw, _lastGpuTemp);

                            if (_debug)
                            {
                                UpdateDebugLabels(cpuTempRaw, gpuTempRaw);
                            }
                        }
                        catch (Exception ex)
                        {
                            // a bad sensor read must never kill the heartbeat loop
                            Logger.LogError(ex);
                        }
                        finally
                        {
                            Interlocked.Exchange(ref sensorRefreshBusy, 0);
                        }
                    });
                }

                // update tray/status UI (marshaled onto the UI thread)
                var connected = _device != null;
                if (_lastReportedConnected != connected)
                {
                    _lastReportedConnected = connected;
                    SetConnectionStatus(connected);
                }
            }
            catch (Exception ex)
            {
                // never let a single bad tick kill the update loop
                Logger.LogError(ex);
            }
        } while (await _pollTimer.WaitForNextTickAsync());
    }

    /// <summary>
    /// Updates the connection status label and tray icon on the UI thread.
    /// </summary>
    private void SetConnectionStatus(bool connected)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new System.Action(() => SetConnectionStatus(connected)));
            return;
        }

        if (_connectionStatusLabel == null || _appStatusNotifyIcon == null)
            return;

        _connectionStatusLabel.Text = connected ? "Connected" : "Not Connected";
        _appStatusNotifyIcon.Icon = connected ? _iconConnected : _iconDisconnected;
        _connectionStatusLabel.ForeColor = connected ? Color.Green : Color.Crimson;
    }

    /// <summary>
    /// Updates the debug temperature labels on the UI thread.
    /// </summary>
    private void UpdateDebugLabels(float? cpuTemp, float? gpuTemp)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(new System.Action(() => UpdateDebugLabels(cpuTemp, gpuTemp)));
            return;
        }

        if (_cpuTempDebugLabel == null || _gpuTempDebugLabel == null)
            return;

        _cpuTempDebugLabel.Text = "CPU Temp: " + FormatDebugTemp(cpuTemp) + "°C";
        _gpuTempDebugLabel.Text = "GPU Temp: " + FormatDebugTemp(gpuTemp) + "°C";
    }

    private static string FormatDebugTemp(float? value)
    {
        if (value is null || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
            return "N/A";

        return Math.Round(value.Value, 1).ToString("0.0");
    }

    /// <summary>
    /// Drops the current device handle and reconnects on the next tick.
    /// </summary>
    private void DropDevice(string reason)
    {
        _device?.Dispose();
        _device = null;
        _payload = null;
        _consecutiveWriteFailures = 0;
        _deviceMissingLogged = false;
        LogConnection(reason);
    }

    /// <summary>
    /// Drops the current device handle and reconnects immediately on the next tick.
    /// </summary>
    private void ForceReconnect(string reason)
    {
        _device?.Dispose();
        _device = null;
        _payload = null;
        _consecutiveWriteFailures = 0;
        _deviceMissingLogged = false;
        LogConnection(reason);
    }

    /// <summary>
    /// Picks up a finished device lookup, or starts a new one. The lookup itself
    /// runs on a worker thread and is never awaited here, so a wedged USB device
    /// cannot block the heartbeat loop (previously this froze the app for hours
    /// until the next sleep/resume cycle).
    /// </summary>
    private void HandleDeviceAttach()
    {
        if (_attachTask != null && _attachTask.IsCompleted)
        {
            var attached = _attachTask.Status == TaskStatus.RanToCompletion ? _attachTask.Result : null;
            _attachTask = null;
            _attachBlockedLogged = false;

            if (attached != null)
            {
                if (_device == null)
                {
                    _device = attached;
                    _payload = null;
                    _consecutiveWriteFailures = 0;
                    _deviceMissingLogged = false;
                    LogConnection("Device connected");
                }
                else
                {
                    // a reconnect overtook this lookup; drop the extra handle
                    attached.Dispose();
                }
            }
            else if (_device == null && !_deviceMissingLogged)
            {
                _deviceMissingLogged = true;
                LogConnection("Device not found; retrying");
            }
        }

        if (_device == null && !_suspended && _attachTask == null)
        {
            _attachStartedUtc = DateTime.UtcNow;
            _attachBlockedLogged = false;
            _attachTask = Task.Run(AttachDevice);
        }
        else if (_attachTask != null && !_attachBlockedLogged &&
                 DateTime.UtcNow - _attachStartedUtc > TimeSpan.FromMilliseconds(AttachStallWarningMs))
        {
            // HidLibrary's enumeration opens each matching device to read its
            // attributes; on a device stuck mid-power-transition that call can sit
            // for a long time. Keep ticking and let it finish in its own time.
            _attachBlockedLogged = true;
            LogConnection("Device lookup blocked; waiting for the USB stack to respond");
        }
    }

    /// <summary>
    /// Looks up and opens the display panel. Runs on a worker thread because the
    /// underlying SetupAPI/HID calls can block indefinitely on a wedged device.
    /// </summary>
    private HidDevice? AttachDevice()
    {
        try
        {
            var device = HidDevices.Enumerate(_vendorId, _productId).FirstOrDefault();
            if (device == null)
                return null;

            // Open in overlapped write mode so a write can be bounded, and do it
            // here rather than in the heartbeat path, where CreateFile could block.
            if (!device.IsOpen)
            {
                device.OpenDevice(DeviceMode.NonOverlapped, DeviceMode.Overlapped, ShareMode.ShareRead | ShareMode.ShareWrite);
            }

            return device;
        }
        catch (Exception ex)
        {
            Logger.LogError(new Exception("Failed to attach to the display", ex));
            return null;
        }
    }

    /// <summary>
    /// Writes a timestamped connection event to the app's log directory.
    /// </summary>
    private static void LogConnection(string message)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluxProDisplay");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "connection.log"), $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the app
        }
    }

    /// <summary>
    /// Returns a valid, displayable temperature, holding the last good value when a
    /// sensor reports null, NaN, infinite or negative values.
    /// </summary>
    private static float SanitizeTemperature(float? value, float lastGood)
    {
        if (value is null || float.IsNaN(value.Value) || float.IsInfinity(value.Value) || value.Value < 0f)
            return lastGood;

        return Math.Clamp(value.Value, 0f, 99.9f);
    }

    /// <summary>
    /// fills the temperature digits and checksum into a pre-allocated payload buffer.
    /// the constant report header (bytes 0-5) is written once at buffer creation.
    /// </summary>
    private static void FillPayload(byte[] payload, float? cpuTemperature, float? gpuTemperature)
    {
        var roundedCpuTemp = Math.Round(cpuTemperature ?? 0, 1);
        var roundedGpuTemp = Math.Round(gpuTemperature ?? 0, 1);

        var wholeNumCpuTemp = (int)roundedCpuTemp;
        var tensPlaceCpuTemp = wholeNumCpuTemp / 10;
        var onesPlaceCpuTemp = wholeNumCpuTemp % 10;
        var tenthsPlaceCpuTemp = (int)((roundedCpuTemp - wholeNumCpuTemp) * 10);

        var wholeNumGpuTemp = (int)roundedGpuTemp;
        var tensPlaceGpuTemp = wholeNumGpuTemp / 10;
        var onesPlaceGpuTemp = wholeNumGpuTemp % 10;
        var tenthsPlaceGpuTemp = (int)((roundedGpuTemp - wholeNumGpuTemp) * 10);

        payload[6] = (byte)tensPlaceCpuTemp;
        payload[7] = (byte)onesPlaceCpuTemp;
        payload[8] = (byte)tenthsPlaceCpuTemp;

        payload[9] = (byte)tensPlaceGpuTemp;
        payload[10] = (byte)onesPlaceGpuTemp;
        payload[11] = (byte)tenthsPlaceGpuTemp;

        byte checksum = 0;
        for (var i = 0; i < 12; i++) checksum += payload[i];
        payload[12] = checksum;
    }

    /// <summary>
    /// Writes one heartbeat report using an overlapped HID write bounded by
    /// <see cref="WriteTimeoutMs"/>, so a stalled device can never block the
    /// heartbeat loop. If the system starts suspending mid-write the transfer is
    /// parked (not cancelled) and reported as
    /// <see cref="WriteResult.Interrupted"/>: cancelling during a power transition
    /// leaves the USB port wedged until the next sleep/resume cycle.
    /// </summary>
    private WriteResult WriteHeartbeat(byte[] payload)
    {
        var device = _device;
        if (device == null || !device.IsOpen)
            return WriteResult.Failed;

        var handle = device.WriteHandle;
        if (handle == IntPtr.Zero || handle.ToInt32() == NativeIo.InvalidHandleValue)
            return WriteResult.Failed;

        var hEvent = NativeIo.CreateEvent(IntPtr.Zero, true, false, null);
        if (hEvent == IntPtr.Zero)
            return WriteResult.Failed;

        // keep the OVERLAPPED struct in unmanaged memory so its address is stable
        // across the WriteFile / CancelIoEx calls (they match on that pointer)
        var pOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());

        // The kernel writes the final I/O status into the OVERLAPPED block when the
        // request completes. If we cannot confirm completion we must NOT free it or
        // close the event, otherwise the kernel writes into freed memory.
        var release = true;
        try
        {
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = hEvent }, pOverlapped, false);

            var started = NativeIo.WriteFile(handle, payload, (uint)payload.Length, out _, pOverlapped);
            if (!started && Marshal.GetLastWin32Error() != NativeIo.ErrorIoPending)
                return WriteResult.Failed; // no request was queued, nothing to clean up

            // if the write completed synchronously the event may not be signaled,
            // so only wait when the request was queued
            var wait = started
                ? NativeIo.WaitObject0
                : NativeIo.WaitForSingleObject(hEvent, WriteTimeoutMs);

            if (wait == NativeIo.WaitObject0)
            {
                var complete = NativeIo.GetOverlappedResult(handle, pOverlapped, out var transferred, false);
                return complete && transferred == payload.Length
                    ? WriteResult.Success
                    : WriteResult.Failed;
            }

            if (_suspended)
            {
                // The machine is going to sleep and the transfer is parked in the
                // USB stack. Leave it and its OVERLAPPED block alone: the resume
                // path disposes the handle, which aborts the request cleanly.
                release = false;
                return WriteResult.Interrupted;
            }

            if (wait != NativeIo.WaitFailed)
                NativeIo.CancelIoEx(handle, pOverlapped);

            // Wait for the cancellation to actually complete before touching the
            // OVERLAPPED block. If it does not, leak the block on purpose rather
            // than risk the kernel writing into freed memory.
            if (NativeIo.WaitForSingleObject(hEvent, CancelWaitMs) != NativeIo.WaitObject0)
            {
                release = false;
                LogConnection("Cancelled write did not complete; leaking its overlapped block");
                return WriteResult.TimedOut;
            }

            return WriteResult.TimedOut;
        }
        finally
        {
            if (release)
            {
                NativeIo.CloseHandle(hEvent);
                Marshal.FreeHGlobal(pOverlapped);
            }
        }
    }

    /// <summary>
    /// Outcome of a single heartbeat write attempt.
    /// </summary>
    private enum WriteResult
    {
        /// <summary>The report was acknowledged by the device.</summary>
        Success,

        /// <summary>
        /// The device rejected or could not accept the report. The handle is
        /// still usable, so the next tick retries before reconnecting.
        /// </summary>
        Failed,

        /// <summary>
        /// The write did not complete within <see cref="WriteTimeoutMs"/> and
        /// was cancelled. The handle is recycled and a fresh connection is made.
        /// </summary>
        TimedOut,

        /// <summary>
        /// A power transition interrupted the write. The transfer is left parked
        /// in the USB stack and the resume path reconnects, so the device is not
        /// treated as failed.
        /// </summary>
        Interrupted
    }

    /// <summary>
    /// Minimal kernel32 declarations used for bounded, cancellable overlapped HID
    /// writes. HidLibrary's synchronous Write can block indefinitely when a device
    /// stops acknowledging reports.
    /// </summary>
    private static class NativeIo
    {
        // Win32 return codes / constants used by WriteHeartbeat
        internal const uint WaitObject0 = 0;        // WAIT_OBJECT_0
        internal const uint WaitTimeout = 258;      // WAIT_TIMEOUT
        internal const uint WaitFailed = 0xFFFFFFFF; // WAIT_FAILED
        internal const int ErrorIoPending = 997;    // ERROR_IO_PENDING
        internal const int InvalidHandleValue = -1; // INVALID_HANDLE_VALUE

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }
}
