using FluxProDisplay.DTOs.AppSettings;
using LibreHardwareMonitor.Hardware;

namespace FluxProDisplay;

public class HardwareMonitor : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Computer _computer;
    private string? _savedCpuSensorName;
    private string? _savedCpuHardwareName;
    private string? _savedGpuSensorName;
    private string? _savedGpuHardwareName;
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
            else TryRestoreSavedCpuSensor();

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
            else TryRestoreSavedGpuSensor();

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

        // Refresh all CPU hardware so the sensor lists are as complete as possible.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Cpu)
                hardware.Update();
        }

        // 1) Exact match against the saved selection, across all CPU hardware.
        if (_savedCpuSensorName != null)
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu) continue;

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;

                    if (sensor.Name.Equals(_savedCpuSensorName, StringComparison.OrdinalIgnoreCase) &&
                        (_savedCpuHardwareName == null || hardware.Name.Equals(_savedCpuHardwareName, StringComparison.OrdinalIgnoreCase)))
                    {
                        logMsg = $"[RESOLVE-CPU]   ✓ MATCHED saved sensor: {hardware.Name}:{sensor.Name}";
                        Console.WriteLine(logMsg);
                        LogDebug(logMsg);
                        _cpuHardware = hardware;
                        _cpuSensor = sensor;
                        return;
                    }
                }
            }
            logMsg = "[RESOLVE-CPU]   Saved sensor not found; trying defaults";
            Console.WriteLine(logMsg);
            LogDebug(logMsg);
        }

        // 2) Preferred sensor.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

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
            }
        }

        // 3) Fall back to the first CPU temperature sensor.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                logMsg = $"[RESOLVE-CPU]   ✓ MATCHED fallback sensor: {hardware.Name}:{sensor.Name}";
                Console.WriteLine(logMsg);
                LogDebug(logMsg);
                _cpuHardware = hardware;
                _cpuSensor = sensor;
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

        // Refresh all GPU hardware so the sensor lists are as complete as possible.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                hardware.Update();
        }

        // 1) Exact match against the saved selection, across all GPU hardware.
        if (_savedGpuSensorName != null)
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;

                    if (sensor.Name.Equals(_savedGpuSensorName, StringComparison.OrdinalIgnoreCase) &&
                        (_savedGpuHardwareName == null || hardware.Name.Equals(_savedGpuHardwareName, StringComparison.OrdinalIgnoreCase)))
                    {
                        logMsg = $"[RESOLVE-GPU]   ✓ MATCHED saved sensor: {hardware.Name}:{sensor.Name}";
                        Console.WriteLine(logMsg);
                        LogDebug(logMsg);
                        _gpuHardware = hardware;
                        _gpuSensor = sensor;
                        return;
                    }
                }
            }
            logMsg = "[RESOLVE-GPU]   Saved sensor not found; trying defaults";
            Console.WriteLine(logMsg);
            LogDebug(logMsg);
        }

        // 2) Preferred sensor.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

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

        // 3) Fall back to the first GPU temperature sensor.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                logMsg = $"[RESOLVE-GPU]   ✓ MATCHED fallback sensor: {hardware.Name}:{sensor.Name}";
                Console.WriteLine(logMsg);
                LogDebug(logMsg);
                _gpuHardware = hardware;
                _gpuSensor = sensor;
                return;
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
                    // Remember the new selection so it can be restored on later runs.
                    _savedCpuHardwareName = hardware.Name;
                    _savedCpuSensorName = sensor.Name;
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
                    // Remember the new selection so it can be restored on later runs.
                    _savedGpuHardwareName = hardware.Name;
                    _savedGpuSensorName = sensor.Name;
                    return;
                }
            }
        }
    }

    private void TryRestoreSavedCpuSensor()
    {
        // Nothing saved, or already on the saved sensor.
        if (_savedCpuSensorName == null || _cpuSensor == null) return;
        if (_cpuSensor.Name.Equals(_savedCpuSensorName, StringComparison.OrdinalIgnoreCase)) return;

        // Some sensors (e.g. AMD "CCDs Average (Tdie)") only appear after the first few
        // update cycles, so keep scanning until the saved sensor becomes available.
        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu) continue;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                if (sensor.Name.Equals(_savedCpuSensorName, StringComparison.OrdinalIgnoreCase) &&
                    (_savedCpuHardwareName == null || hardware.Name.Equals(_savedCpuHardwareName, StringComparison.OrdinalIgnoreCase)))
                {
                    LogDebug($"[RESTORE-CPU] ✓ Restored saved sensor: {hardware.Name}:{sensor.Name}");
                    _cpuHardware = hardware;
                    _cpuSensor = sensor;
                    return;
                }
            }
        }
    }

    private void TryRestoreSavedGpuSensor()
    {// Nothing saved, or already on the saved sensor.
        
        if (_savedGpuSensorName == null || _gpuSensor == null) return;
        if (_gpuSensor.Name.Equals(_savedGpuSensorName, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)) continue;

            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature) continue;

                if (sensor.Name.Equals(_savedGpuSensorName, StringComparison.OrdinalIgnoreCase) &&
                    (_savedGpuHardwareName == null || hardware.Name.Equals(_savedGpuHardwareName, StringComparison.OrdinalIgnoreCase)))
                {
                    LogDebug($"[RESTORE-GPU] ✓ Restored saved sensor: {hardware.Name}:{sensor.Name}");
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