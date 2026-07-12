using FanControl.Plugins;

namespace FanControl.MsiEc;

public class MsiEcPlugin : IPlugin2
{
    /// <summary>EC firmware families verified to use the standard MSI register layout.</summary>
    private static readonly string[] KnownGoodFirmwarePrefixes = ["14B3EMS1"];

    private const int ModeCheckEveryNUpdates = 5;
    private const int FailuresBeforeGivingUp = 5;
    private const int BoostOffHysteresis = 5;

    private readonly IPluginLogger? _logger;
    private readonly IPluginDialog? _dialog;
    private readonly object _lock = new();

    private AcpiEcIo? _ec;
    private MsiEc? _msi;
    private MsiEc.Snapshot? _snapshot;
    private bool _compatible;
    private string _firmware = "";

    private Channel? _cpu;
    private Channel? _gpu;

    private int _boostAtDuty = 100; // 1..100 enables Cooler Boost; else disabled
    private bool _boostActive;

    private int _updatesSinceModeCheck;
    private int _consecutiveFailures;

    public MsiEcPlugin(IPluginLogger logger, IPluginDialog dialog)
    {
        _logger = logger;
        _dialog = dialog;
    }

    public string Name => "MSI Laptop EC";

    public void Initialize()
    {
        lock (_lock)
        {
            if (_ec is not null)
                return;

            var config = PluginConfig.Load(Log);
            try
            {
                _ec = new AcpiEcIo();
                _msi = new MsiEc(_ec);
                _firmware = _msi.ReadFirmwareVersion();

                var supported = KnownGoodFirmwarePrefixes
                    .Concat(config.AdditionalFirmwarePrefixes)
                    .Any(p => !string.IsNullOrWhiteSpace(p) && _firmware.StartsWith(p.Trim(), StringComparison.OrdinalIgnoreCase));

                Log($"EC firmware '{_firmware}', PawnIOLib {FormatVersion(_ec.LibVersion)}, supported: {supported}");

                if (!supported)
                {
                    _ = _dialog?.ShowMessageDialog(
                        $"FanControl.MsiEc: EC firmware '{_firmware}' is not in the verified list yet, " +
                        "so the plugin disabled itself to avoid writing to an unknown EC.\n\n" +
                        "If you are sure your MSI laptop uses the standard EC layout, create " +
                        $"\"{PluginConfig.ConfigPath}\" with:\n" +
                        "{ \"additionalFirmwarePrefixes\": [\"" + (_firmware.Length >= 8 ? _firmware[..8] : _firmware) + "\"] }\n\n" +
                        "Please also report your model so it can be added to the verified list.");
                    DisposeEc();
                    return;
                }

                _compatible = true;
                _boostAtDuty = config.CoolerBoostAtDuty;
                _snapshot = _msi.CaptureSnapshot();
                Log($"Factory curves CPU [{string.Join(",", _snapshot.CpuCurve)}], GPU [{string.Join(",", _snapshot.GpuCurve)}], mode 0x{_snapshot.FanMode:X2}");

                _cpu = new Channel(MsiEc.Cpu, "CPU");
                _gpu = DetectGpuChannel(config);
                Log(_gpu is null ? "GPU fan: not present / disabled" : "GPU fan: enabled");
            }
            catch (DllNotFoundException)
            {
                Log("PawnIOLib.dll not found — PawnIO is not installed");
                _ = _dialog?.ShowMessageDialog(
                    "FanControl.MsiEc requires the PawnIO driver.\n\n" +
                    "Install it from https://pawnio.eu (or let FanControl install it: " +
                    "Settings → Install PawnIO), then restart FanControl.");
                DisposeEc();
            }
            catch (Exception e)
            {
                Log("Initialize failed: " + e.Message);
                DisposeEc();
            }
        }
    }

    private Channel? DetectGpuChannel(PluginConfig config)
    {
        switch (config.GpuFan.Trim().ToLowerInvariant())
        {
            case "off":
                return null;
            case "on":
                return new Channel(MsiEc.Gpu, "GPU");
            default: // auto: present if the EC reports any GPU activity at startup
                var status = _msi!.ReadStatus(MsiEc.Gpu);
                return status.Temp > 0 || status.Rpm > 0 || status.Percent > 0
                    ? new Channel(MsiEc.Gpu, "GPU")
                    : null;
        }
    }

    public void Load(IPluginSensorsContainer container)
    {
        lock (_lock)
        {
            if (!_compatible)
                return;

            foreach (var channel in ActiveChannels())
            {
                container.TempSensors.Add(new DelegateSensor(
                    $"MsiEc/{channel.Regs.Key}/Temp", $"{channel.Name} (MSI EC)", () => Locked(() => channel.Temp)));
                container.FanSensors.Add(new DelegateSensor(
                    $"MsiEc/{channel.Regs.Key}/FanRpm", $"{channel.Name} Fan (MSI EC)", () => Locked(() => channel.Rpm)));
                container.ControlSensors.Add(new FanControlSensor(this, channel));
            }
        }
    }

    public void Update()
    {
        lock (_lock)
        {
            if (_msi is null || !_compatible)
                return;

            try
            {
                foreach (var channel in ActiveChannels())
                {
                    var status = _msi.ReadStatus(channel.Regs);
                    channel.Temp = status.Temp;
                    channel.Percent = status.Percent;
                    channel.Rpm = status.Rpm;
                }

                _consecutiveFailures = 0;

                // The EC can fall back to auto mode on its own (e.g. after
                // resume from sleep) — re-assert manual control if we own it.
                if (AnyControlActive() && ++_updatesSinceModeCheck >= ModeCheckEveryNUpdates)
                {
                    _updatesSinceModeCheck = 0;
                    if (_msi.ReadFanMode() != MsiEc.FanModeAdvanced)
                    {
                        Log("Fan mode drifted back to auto, re-applying manual duty");
                        foreach (var channel in ActiveChannels())
                        {
                            if (channel.ControlActive && channel.LastApplied is int duty)
                                _msi.ApplyFlatSpeed(channel.Regs, duty);
                        }
                    }

                    if (_boostActive && !_msi.ReadCoolerBoost())
                    {
                        Log("Cooler Boost drifted off, re-engaging");
                        _msi.SetCoolerBoost(true);
                    }
                }
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                if (++_consecutiveFailures == FailuresBeforeGivingUp)
                {
                    Log("EC reads keep failing: " + e.Message);
                    foreach (var channel in ActiveChannels())
                        channel.Temp = channel.Percent = channel.Rpm = null;
                }
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            try
            {
                if (_msi is not null && _snapshot is not null && AnyControlActive())
                {
                    RestoreEverything();
                    Log("Restored factory fan configuration on close");
                }
            }
            catch (Exception e)
            {
                Log("Restore on close failed: " + e.Message);
            }

            _cpu = null;
            _gpu = null;
            DisposeEc();
        }
    }

    internal void SetFan(Channel channel, float value)
    {
        lock (_lock)
        {
            if (_msi is null || !_compatible)
                return;

            var duty = (int)Math.Round(Math.Clamp(value, 0f, 100f));
            if (channel.ControlActive && channel.LastApplied == duty)
                return;

            try
            {
                _msi.ApplyFlatSpeed(channel.Regs, duty);
                if (!channel.ControlActive)
                    Log($"Taking over {channel.Name} fan control, duty {duty}%");
                channel.ControlActive = true;
                channel.LastApplied = duty;
                UpdateCoolerBoost();
            }
            catch (Exception e)
            {
                Log($"Set {channel.Name} ({duty}%) failed: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Cooler Boost bypasses the EC's ~1 %/s PWM ramp (max RPM in ~2 s), so it
    /// backs any control asked for ≥ threshold duty. Released with hysteresis
    /// once every controlled fan drops below threshold − 5 %.
    /// </summary>
    private void UpdateCoolerBoost()
    {
        if (_msi is null || _boostAtDuty is < 1 or > 100)
            return;

        var controlled = ActiveChannels().Where(c => c.ControlActive && c.LastApplied is not null).ToList();
        var wantOn = controlled.Any(c => c.LastApplied >= _boostAtDuty);
        var allBelowHysteresis = controlled.All(c => c.LastApplied < _boostAtDuty - BoostOffHysteresis);

        if (wantOn && !_boostActive)
        {
            _msi.SetCoolerBoost(true);
            _boostActive = true;
            Log("Cooler Boost ON (instant max fan)");
        }
        else if (_boostActive && (allBelowHysteresis || controlled.Count == 0))
        {
            _msi.SetCoolerBoost(false);
            _boostActive = false;
            Log("Cooler Boost OFF");
        }
    }

    internal void ResetFan(Channel channel)
    {
        lock (_lock)
        {
            if (!channel.ControlActive || _msi is null || _snapshot is null)
                return;

            try
            {
                _msi.RestoreCurve(channel.Regs, CurveFor(channel));
                channel.ControlActive = false;
                channel.LastApplied = null;
                UpdateCoolerBoost();

                if (!AnyControlActive())
                    _msi.RestoreMode(_snapshot.FanMode);

                Log($"{channel.Name} fan control released" + (AnyControlActive() ? "" : ", factory mode restored"));
            }
            catch (Exception e)
            {
                Log($"Reset {channel.Name} failed: " + e.Message);
            }
        }
    }

    internal float? GetControlValue(Channel channel)
    {
        lock (_lock)
            return channel.ControlActive ? channel.LastApplied : channel.Percent;
    }

    private void RestoreEverything()
    {
        if (_boostActive)
        {
            _msi!.SetCoolerBoost(false);
            _boostActive = false;
        }

        foreach (var channel in ActiveChannels())
        {
            if (channel.ControlActive)
            {
                _msi!.RestoreCurve(channel.Regs, CurveFor(channel));
                channel.ControlActive = false;
                channel.LastApplied = null;
            }
        }

        _msi!.RestoreMode(_snapshot!.FanMode);
    }

    private byte[] CurveFor(Channel channel) =>
        channel.Regs.Key == "Gpu" ? _snapshot!.GpuCurve : _snapshot!.CpuCurve;

    private IEnumerable<Channel> ActiveChannels()
    {
        if (_cpu is not null) yield return _cpu;
        if (_gpu is not null) yield return _gpu;
    }

    private bool AnyControlActive() => ActiveChannels().Any(c => c.ControlActive);

    private T Locked<T>(Func<T> get)
    {
        lock (_lock)
            return get();
    }

    private void DisposeEc()
    {
        _ec?.Dispose();
        _ec = null;
        _msi = null;
        _snapshot = null;
        _compatible = false;
    }

    private void Log(string message) => _logger?.Log("[MsiEc] " + message);

    private static string FormatVersion(uint v) => $"{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    internal sealed class Channel(MsiEc.FanRegs regs, string name)
    {
        public MsiEc.FanRegs Regs { get; } = regs;
        public string Name { get; } = name;
        public float? Temp;
        public float? Percent;
        public float? Rpm;
        public bool ControlActive;
        public int? LastApplied;
    }

    private sealed class DelegateSensor(string id, string name, Func<float?> read) : IPluginSensor
    {
        public string Id => id;
        public string Name => name;
        public float? Value => read();
        public void Update() { }
    }

    private sealed class FanControlSensor(MsiEcPlugin owner, Channel channel) : IPluginControlSensor2
    {
        public string Id => $"MsiEc/{channel.Regs.Key}/Control";
        public string Name => $"{channel.Name} Fan (MSI EC)";
        public float? Value => owner.GetControlValue(channel);
        public string PairedFanSensorId => $"MsiEc/{channel.Regs.Key}/FanRpm";
        public void Update() { }
        public void Set(float val) => owner.SetFan(channel, val);
        public void Reset() => owner.ResetFan(channel);
    }
}
