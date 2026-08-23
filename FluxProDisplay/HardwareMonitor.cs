using FluxProDisplay.DTOs.AppSettings;
using LibreHardwareMonitor.Hardware;

namespace FluxProDisplay;

public class HardwareMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Computer _computer;
    private readonly string? _savedCpuSensorName;
    private readonly string? _savedCpuHardwareName;
    private readonly string? _savedGpuSensorName;
    private readonly string? _savedGpuHardwareName;
    private IHardware? _cpuHardware;
    private ISensor? _cpuSensor;
    private IHardware? _gpuHardware;
    private ISensor? _gpuSensor;

    public HardwareMonitor(AppSettings? appSettings = null)
    {
        _computer = new Computer()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };

        _computer.Open();
        _computer.Accept(new UpdateVisitor());
        
        Console.WriteLine($"[HW-MON] HardwareMonitor created with settings: CPU={appSettings?.SelectedCpuSensor}, GPU={appSettings?.SelectedGpuSensor}");
        
        if (appSettings?.SelectedCpuSensor != null)
        {
            var parts = appSettings.SelectedCpuSensor.Split(':', 2);
            _savedCpuSensorName = parts.Length == 2 ? parts[1] : appSettings.SelectedCpuSensor;
            _savedCpuHardwareName = parts.Length == 2 ? parts[0] : null;
            Console.WriteLine($"[HW-MON] Parsed saved CPU: hardware={_savedCpuHardwareName}, sensor={_savedCpuSensorName}");
        }
        if (appSettings?.SelectedGpuSensor != null)
        {
            var parts = appSettings.SelectedGpuSensor.Split(':', 2);
            _savedGpuSensorName = parts.Length == 2 ? parts[1] : appSettings.SelectedGpuSensor;
            _savedGpuHardwareName = parts.Length == 2 ? parts[0] : null;
            Console.WriteLine($"[HW-MON] Parsed saved GPU: hardware={_savedGpuHardwareName}, sensor={_savedGpuSensorName}");
        }
    }

    public void Dispose()
    {
        _computer.Close();
        GC.SuppressFinalize(this);
    }

    public float? GetCpuTemperature()
    {
        IHardware? cpuHardware;
        ISensor? cpuSensor;

        lock (_syncRoot)
        {
            if (_cpuSensor == null) ResolveCpuSensor();
            cpuHardware = _cpuHardware;
            cpuSensor = _cpuSensor;
        }

        if (cpuHardware == null || cpuSensor == null) return null;

        // update outside the lock: slow sensor reads must never block the UI thread
        cpuHardware.Update();
        return cpuSensor.Value;
    }

    public float? GetGpuTemperature()
    {
        IHardware? gpuHardware;
        ISensor? gpuSensor;

        lock (_syncRoot)
        {
            if (_gpuSensor == null) ResolveGpuSensor();
            gpuHardware = _gpuHardware;
            gpuSensor = _gpuSensor;
        }

        if (gpuHardware == null || gpuSensor == null) return null;

        // update outside the lock: slow sensor reads must never block the UI thread
        gpuHardware.Update();
        return gpuSensor.Value;
    }

    private static void LogDebug(string message)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FluxProDisplay");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "debug.log");
            File.AppendAllText(logPath, message + Environment.NewLine);
        }
        catch { }
    }

    private void ResolveCpuSensor()
    {
        var logMsg = $"[RESOLVE-CPU] Saved hardware: '{_savedCpuHardwareName}', sensor: '{_savedCpuSensorName}'";
        Console.WriteLine(logMsg);
        LogDebug(logMsg);
        
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            hardware.Update();
            logMsg = $"[RESOLVE-CPU] Found CPU hardware: '{hardware.Name}'";
            Console.WriteLine(logMsg);
            LogDebug(logMsg);

            ISensor? firstTempSensor = null;
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                logMsg = $"[RESOLVE-CPU]   - Sensor: '{sensor.Name}'";
                Console.WriteLine(logMsg);
                LogDebug(logMsg);

                // Check for saved sensor name first
                if (_savedCpuSensorName != null && 
                    sensor.Name.Equals(_savedCpuSensorName, StringComparison.OrdinalIgnoreCase) &&
                    (_savedCpuHardwareName == null || hardware.Name.Equals(_savedCpuHardwareName, StringComparison.OrdinalIgnoreCase)))
                {
                    logMsg = $"[RESOLVE-CPU]   ✓ MATCHED saved sensor: {hardware.Name}:{sensor.Name}";
                    Console.WriteLine(logMsg);
                    LogDebug(logMsg);
                    _cpuHardware = hardware;
                    _cpuSensor = sensor;
                    return;
                }

                if (sensor.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
                {
                    logMsg = $"[RESOLVE-CPU]   ✓ MATCHED preferred sensor: {hardware.Name}:{sensor.Name}";
                    Console.WriteLine(logMsg);
                    LogDebug(logMsg);
                    _cpuHardware = hardware;
                    _cpuSensor = sensor;
                    return;
                }

                firstTempSensor ??= sensor;
            }

            // fall back to any CPU temperature sensor
            if (firstTempSensor != null)
            {
                logMsg = $"[RESOLVE-CPU]   ✓ MATCHED fallback sensor: {hardware.Name}:{firstTempSensor.Name}";
                Console.WriteLine(logMsg);
                LogDebug(logMsg);
                _cpuHardware = hardware;
                _cpuSensor = firstTempSensor;
                return;
            }
        }
        logMsg = "[RESOLVE-CPU] No CPU sensor found";
        Console.WriteLine(logMsg);
        LogDebug(logMsg);
    }

    private void ResolveGpuSensor()
    {
        var logMsg = $"[RESOLVE-GPU] Saved hardware: '{_savedGpuHardwareName}', sensor: '{_savedGpuSensorName}'";
        Console.WriteLine(logMsg);
        LogDebug(logMsg);
        
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

            hardware.Update();
            logMsg = $"[RESOLVE-GPU] Found GPU hardware: '{hardware.Name}'";
            Console.WriteLine(logMsg);
            LogDebug(logMsg);

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                logMsg = $"[RESOLVE-GPU]   - Sensor: '{sensor.Name}'";
                Console.WriteLine(logMsg);
                LogDebug(logMsg);

                // Check for saved sensor name first
                if (_savedGpuSensorName != null && 
                    sensor.Name.Equals(_savedGpuSensorName, StringComparison.OrdinalIgnoreCase) &&
                    (_savedGpuHardwareName == null || hardware.Name.Equals(_savedGpuHardwareName, StringComparison.OrdinalIgnoreCase)))
                {
                    logMsg = $"[RESOLVE-GPU]   ✓ MATCHED saved sensor: {hardware.Name}:{sensor.Name}";
                    Console.WriteLine(logMsg);
                    LogDebug(logMsg);
                    _gpuHardware = hardware;
                    _gpuSensor = sensor;
                    return;
                }

                if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    logMsg = $"[RESOLVE-GPU]   ✓ MATCHED preferred sensor: {hardware.Name}:{sensor.Name}";
                    Console.WriteLine(logMsg);
                    LogDebug(logMsg);
                    _gpuHardware = hardware;
                    _gpuSensor = sensor;
                    return;
                }
            }
        }
        logMsg = "[RESOLVE-GPU] No GPU sensor found";
        Console.WriteLine(logMsg);
        LogDebug(logMsg);
    }

    public List<(string name, ISensor sensor)> GetAvailableCpuSensors()
    {
        lock (_syncRoot)
        {
            var sensors = new List<(string name, ISensor sensor)>();
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu) continue;

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature)
                        sensors.Add((sensor.Name, sensor));
                }
            }
            return sensors;
        }
    }

    public List<(string name, ISensor sensor)> GetAvailableGpuSensors()
    {
        lock (_syncRoot)
        {
            var sensors = new List<(string name, ISensor sensor)>();
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature)
                        sensors.Add((sensor.Name, sensor));
                }
            }
            return sensors;
        }
    }

    public void SetCpuSensor(ISensor sensor)
    {
        lock (_syncRoot)
        {
            // Find the hardware that contains this sensor
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.Sensors.Contains(sensor))
                {
                    _cpuHardware = hardware;
                    _cpuSensor = sensor;
                    return;
                }
            }
        }
    }

    public void SetGpuSensor(ISensor sensor)
    {
        lock (_syncRoot)
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.Sensors.Contains(sensor))
                {
                    _gpuHardware = hardware;
                    _gpuSensor = sensor;
                    return;
                }
            }
        }
    }

    public string? GetSelectedCpuSensorName() => _cpuSensor?.Name;
    public string? GetSelectedGpuSensorName() => _gpuSensor?.Name;
    public string? GetSelectedCpuSensorFullName() => _cpuHardware != null && _cpuSensor != null ? $"{_cpuHardware.Name}:{_cpuSensor.Name}" : null;
    public string? GetSelectedGpuSensorFullName() => _gpuHardware != null && _gpuSensor != null ? $"{_gpuHardware.Name}:{_gpuSensor.Name}" : null;
}