using FluxProDisplay.DTOs.AppSettings;
using HidLibrary;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System.Text.Json;
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

    // other UI components for the tab
    private NotifyIcon _appStatusNotifyIcon = null!;
    private ContextMenuStrip _contextMenuStrip = null!;

    private PeriodicTimer? _pollTimer;
    private HidDevice? _device;
    private byte[]? _payload;

    // reconnect state (accessed only from the update loop thread)
    private int _consecutiveWriteFailures;
    private DateTime _nextReconnectAttemptUtc = DateTime.MinValue;

    // last good/displayed temperatures
    private float _lastCpuTemp;
    private float _lastGpuTemp;

    // last connection state pushed to the UI
    private bool? _lastReportedConnected;

    // set by the power-mode event handler when the system resumes from sleep
    private volatile bool _resumeDetected;

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

        // sensor selection menus
        AddSensorSelectionMenus();

        _connectionStatusLabel = new ToolStripLabel();
        _connectionStatusLabel.ForeColor = Color.Crimson;
        _connectionStatusLabel.Enabled = true;
        _contextMenuStrip.Items.Add(_connectionStatusLabel);

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

    private void AddSensorSelectionMenus()
    {
        _contextMenuStrip.Items.Add(new ToolStripSeparator());
        
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
        
        _contextMenuStrip.Items.Add(cpuSensorMenu);
        _contextMenuStrip.Items.Add(gpuSensorMenu);
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
            
            AddSensorSelectionMenus();
            SaveConfiguration();
        }
    }

    private void SaveConfiguration()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var cpuName = _monitor.GetSelectedCpuSensorFullName();
            var gpuName = _monitor.GetSelectedGpuSensorFullName();
            
            Console.WriteLine($"[SAVE] Saving to: {configPath}");
            Console.WriteLine($"[SAVE] CPU Sensor: {cpuName}");
            Console.WriteLine($"[SAVE] GPU Sensor: {gpuName}");
            
            _configuration.AppSettings.SelectedCpuSensor = cpuName;
            _configuration.AppSettings.SelectedGpuSensor = gpuName;
            
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_configuration, options);
            File.WriteAllText(configPath, json);
            
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
        var exePath = Application.ExecutablePath;

        using (var taskService = new TaskService())
        {
            var existingTask = taskService.FindTask(ElevatedTaskName);

            if (existingTask != null)
            {
                taskService.RootFolder.DeleteTask(ElevatedTaskName);
            }
            else
            {
                var newStartupTask = taskService.NewTask();

                newStartupTask.RegistrationInfo.Description = "Flux Pro Display Service Task with Admin Privileges";
                newStartupTask.Principal.RunLevel = TaskRunLevel.Highest;
                newStartupTask.Principal.LogonType = TaskLogonType.InteractiveToken;

                newStartupTask.Triggers.Add(new LogonTrigger());
                newStartupTask.Actions.Add(new ExecAction(exePath, null, Path.GetDirectoryName(exePath)));

                taskService.RootFolder.RegisterTaskDefinition(ElevatedTaskName, newStartupTask);
            }
        }

        UpdateStartupMenuItemText();
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
        if (e.Mode == PowerModes.Resume)
            _resumeDetected = true;
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

    private async Task WriteToDisplay()
    {
        // interval is in ms, set in appsettings.json
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval));

        do
        {
            try
            {
                // system resumed from sleep: drop the stale handle and reconnect immediately
                if (_resumeDetected)
                {
                    _resumeDetected = false;
                    ForceReconnect();
                }

                // sample once per tick and reuse for both payload and debug labels
                var cpuTempRaw = _monitor.GetCpuTemperature();
                var gpuTempRaw = _monitor.GetGpuTemperature();

                // hold the last good reading whenever a sensor reports an invalid value
                var cpuTemp = SanitizeTemperature(cpuTempRaw, _lastCpuTemp);
                var gpuTemp = SanitizeTemperature(gpuTempRaw, _lastGpuTemp);
                _lastCpuTemp = cpuTemp;
                _lastGpuTemp = gpuTemp;

                // (re)connect only when due; health is judged by write success, not enumeration
                if (_device == null && DateTime.UtcNow >= _nextReconnectAttemptUtc)
                {
                    _device = HidDevices.Enumerate(_vendorId, _productId).FirstOrDefault();
                    if (_device == null)
                    {
                        // device still missing: back off so we don't hammer the HID subsystem
                        ScheduleReconnect();
                        LogConnection("Device not found; scheduling reconnect");
                    }
                    else
                    {
                        _payload = null;
                        LogConnection("Device connected");
                    }
                }

                if (_device != null)
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

                    // write every tick: the panel treats these reports as a keep-alive
                    // heartbeat, so gaps in writes let the display go to sleep (flicker)
                    FillPayload(_payload, cpuTemp, gpuTemp);

                    var ok = false;
                    try
                    {
                        // Write returns false on failure instead of throwing in most cases
                        ok = _device.Write(_payload);
                    }
                    catch
                    {
                        ok = false;
                    }

                    if (ok)
                    {
                        _consecutiveWriteFailures = 0;
                    }
                    else
                    {
                        DropDevice();
                    }
                }

                // update tray/status UI (marshaled onto the UI thread)
                var connected = _device != null;
                if (_lastReportedConnected != connected)
                {
                    _lastReportedConnected = connected;
                    SetConnectionStatus(connected);
                }

                if (_debug)
                {
                    UpdateDebugLabels(cpuTempRaw, gpuTempRaw);
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
    /// Drops the current device handle and schedules a reconnect attempt with backoff.
    /// </summary>
    private void DropDevice()
    {
        _device?.Dispose();
        _device = null;
        _payload = null;
        LogConnection($"Write failed; dropping device (failure #{_consecutiveWriteFailures + 1})");
        ScheduleReconnect();
    }

    /// <summary>
    /// Drops the current device handle and reconnects immediately on the next tick.
    /// </summary>
    private void ForceReconnect()
    {
        _device?.Dispose();
        _device = null;
        _payload = null;
        _consecutiveWriteFailures = 0;
        _nextReconnectAttemptUtc = DateTime.MinValue;
        LogConnection("System resumed from sleep; forcing reconnect");
    }

    /// <summary>
    /// Schedules the next reconnect attempt using exponential backoff.
    /// </summary>
    private void ScheduleReconnect()
    {
        _consecutiveWriteFailures++;
        var exponent = Math.Min(_consecutiveWriteFailures, 6);
        var delayMs = Math.Min(500 * (int)Math.Pow(2, exponent), 30_000);
        _nextReconnectAttemptUtc = DateTime.UtcNow.AddMilliseconds(delayMs);
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
}
