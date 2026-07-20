using FanControl.Plugins;

namespace FanControl.MsiEc;

public class MsiEcPlugin : IPlugin2
{
    /// <summary>EC firmware families verified to use the standard MSI register layout.</summary>
    private static readonly string[] KnownGoodFirmwarePrefixes = ["14B3EMS1"];

    // EC traffic discipline: the Windows ACPI driver shares the EC with us and
    // cannot be locked out, so every transaction we skip is a collision that
    // cannot happen. Sensors are polled every 5th cycle, duty writes within
    // ±2 % are coalesced, the mode check runs once a minute, and on repeated
    // failures we back off entirely.
    private const int PollEveryNUpdates = 5;
    private const int ModeCheckEveryNUpdates = 60;
    private const int WriteCoalesceDelta = 3;
    private const int WriteCoalesceMs = 2000;
    private const int FailuresBeforeGivingUp = 5;
    private const int FailuresBeforeBackoff = 3;
    private const int BackoffInitialMs = 10_000;
    private const int BackoffMaxMs = 60_000;

    // Cooler Boost anti-flapping: wide hysteresis plus a minimum interval
    // between toggles (each toggle is an EC write to a shared register).
    private const int BoostOffHysteresis = 10;
    private const int BoostMinToggleMs = 60_000;

    // Sleep/resume is when the kernel talks to the EC the most. An update gap
    // longer than WakeGapSeconds means the process (or machine) was suspended —
    // stay off the bus for the grace period, then gently re-assert control.
    private const int WakeGapSeconds = 10;
    private const int WakeGraceSeconds = 45;

    // EC health: repeated anomalies (failed/rejected writes, timeouts,
    // implausible readings) within the window suspend ALL writes for a long
    // cool-down. The fan keeps its last duty; the EC-side thermal failsafe
    // bands still protect against overheating.
    private const int AnomalyWindowMs = 600_000;
    private const int AnomalySuspendThreshold = 2;
    private const int WriteSuspendMs = 900_000;

    private const float MaxPlausibleCpuTemp = 110f;
    private const float MaxPlausibleRpm = 7500f;

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
    private DateTime _lastBoostToggleUtc = DateTime.MinValue;

    private long _updateCounter;
    private int _consecutiveFailures;
    private int _backoffMs = BackoffInitialMs;
    private DateTime _suspendedUntilUtc = DateTime.MinValue;
    private DateTime _writesSuspendedUntilUtc = DateTime.MinValue;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private bool _wakeReassertPending;
    private readonly List<DateTime> _recentAnomalies = [];

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
                Log($"Factory curves CPU [{string.Join(",", _snapshot.CpuCurve)}], GPU [{string.Join(",", _snapshot.GpuCurve)}], mode 0x{_snapshot.FanMode:X2}, boost base 0x{_snapshot.CoolerBoostBase:X2}");

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

            var now = DateTime.UtcNow;
            if (_lastUpdateUtc != DateTime.MinValue && (now - _lastUpdateUtc).TotalSeconds > WakeGapSeconds)
            {
                Log($"Update gap of {(now - _lastUpdateUtc).TotalSeconds:F0}s (sleep/resume?) — staying off the EC bus for {WakeGraceSeconds}s");
                _suspendedUntilUtc = now.AddSeconds(WakeGraceSeconds);
                _wakeReassertPending = true;
            }

            _lastUpdateUtc = now;
            if (now < _suspendedUntilUtc)
                return;

            _updateCounter++;
            try
            {
                if (_wakeReassertPending)
                {
                    _wakeReassertPending = false;
                    ReassertAfterWake();
                }

                if (now >= _writesSuspendedUntilUtc)
                {
                    foreach (var channel in ActiveChannels())
                    {
                        if (channel.PendingDuty is int pending && pending != channel.LastApplied && IsWriteEligible(channel, pending))
                            ApplyDuty(channel, pending);
                    }
                }

                if (_updateCounter % PollEveryNUpdates == 0)
                    PollSensors();

                if (AnyControlActive() && _updateCounter % ModeCheckEveryNUpdates == 0 && now >= _writesSuspendedUntilUtc)
                {
                    if (_msi.ReadFanMode() != MsiEc.FanModeAdvanced)
                    {
                        Log("Fan mode drifted back to auto, re-applying manual duty");
                        foreach (var channel in ActiveChannels())
                        {
                            if (channel.ControlActive && channel.LastApplied is int duty)
                                _msi.ApplyFlatSpeed(channel.Regs, duty);
                        }
                    }

                    if (_boostActive && _snapshot is not null && !_msi.ReadCoolerBoost())
                    {
                        Log("Cooler Boost drifted off, re-engaging");
                        _msi.SetCoolerBoost(true, _snapshot.CoolerBoostBase);
                        _lastBoostToggleUtc = now;
                    }
                }

                _consecutiveFailures = 0;
                _backoffMs = BackoffInitialMs;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                RegisterFailure(e);
            }
        }
    }

    private void PollSensors()
    {
        foreach (var channel in ActiveChannels())
        {
            var status = _msi!.ReadStatus(channel.Regs);

            // A colliding transaction yields garbage bytes; never let garbage
            // reach the sensors (and through them, the user's fan curves).
            var implausible = channel.Regs.Key == "Cpu"
                ? status.Temp is < 1 or > MaxPlausibleCpuTemp || status.Rpm > MaxPlausibleRpm
                : status.Temp > MaxPlausibleCpuTemp || status.Rpm > MaxPlausibleRpm;
            if (implausible)
            {
                Log($"Implausible EC reading for {channel.Name} (temp={status.Temp}, rpm={status.Rpm}) — sample discarded");
                NoteAnomaly();
                continue;
            }

            channel.Temp = status.Temp;
            channel.Percent = status.Percent;
            channel.Rpm = status.Rpm;
        }
    }

    private void ReassertAfterWake()
    {
        if (DateTime.UtcNow < _writesSuspendedUntilUtc || !AnyControlActive())
            return;

        if (_msi!.ReadFanMode() != MsiEc.FanModeAdvanced)
        {
            Log("Re-asserting fan control after wake");
            foreach (var channel in ActiveChannels())
            {
                if (channel.ControlActive && channel.LastApplied is int duty)
                    _msi.ApplyFlatSpeed(channel.Regs, duty);
            }
        }

        if (_boostActive && _snapshot is not null && !_msi.ReadCoolerBoost())
        {
            _msi.SetCoolerBoost(true, _snapshot.CoolerBoostBase);
            _lastBoostToggleUtc = DateTime.UtcNow;
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
            if ((channel.ControlActive && channel.LastApplied == duty && channel.PendingDuty is null)
                || channel.PendingDuty == duty)
                return;

            // Small nudges are coalesced (the EC ramps ~1 %/s, nothing is lost);
            // during backoff/write-suspension the value is parked and flushed
            // by Update() once access resumes.
            var now = DateTime.UtcNow;
            if (now < _suspendedUntilUtc || now < _writesSuspendedUntilUtc || !IsWriteEligible(channel, duty))
            {
                channel.PendingDuty = duty;
                return;
            }

            try
            {
                ApplyDuty(channel, duty);
                _consecutiveFailures = 0;
                _backoffMs = BackoffInitialMs;
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                channel.PendingDuty = duty; // retry via Update() after backoff
                Log($"Set {channel.Name} ({duty}%) failed: " + e.Message);
                RegisterFailure(e, quiet: true);
            }
        }
    }

    private static bool IsWriteEligible(Channel channel, int duty) =>
        !channel.ControlActive
        || channel.LastApplied is not int last
        || Math.Abs(duty - last) >= WriteCoalesceDelta
        || (DateTime.UtcNow - channel.LastWriteUtc).TotalMilliseconds >= WriteCoalesceMs;

    private void ApplyDuty(Channel channel, int duty)
    {
        _msi!.ApplyFlatSpeed(channel.Regs, duty);
        if (!channel.ControlActive)
            Log($"Taking over {channel.Name} fan control, duty {duty}%");
        channel.ControlActive = true;
        channel.LastApplied = duty;
        channel.PendingDuty = null;
        channel.LastWriteUtc = DateTime.UtcNow;
        UpdateCoolerBoost();
    }

    /// <summary>
    /// Cooler Boost bypasses the EC's ~1 %/s PWM ramp (max RPM in ~2 s), so it
    /// backs any control asked for ≥ threshold duty. Released with wide
    /// hysteresis (threshold − 10 %) and toggled at most once a minute — every
    /// toggle is a write to a shared EC register and must stay rare.
    /// </summary>
    private void UpdateCoolerBoost()
    {
        if (_msi is null || _snapshot is null || _boostAtDuty is < 1 or > 100)
            return;

        var now = DateTime.UtcNow;
        if (now < _writesSuspendedUntilUtc)
            return;

        var controlled = ActiveChannels().Where(c => c.ControlActive && c.LastApplied is not null).ToList();
        var wantOn = controlled.Any(c => c.LastApplied >= _boostAtDuty);
        var allBelowHysteresis = controlled.All(c => c.LastApplied < _boostAtDuty - BoostOffHysteresis);

        var toggleOn = wantOn && !_boostActive;
        var toggleOff = _boostActive && (allBelowHysteresis || controlled.Count == 0);
        if (!toggleOn && !toggleOff)
            return;

        if ((now - _lastBoostToggleUtc).TotalMilliseconds < BoostMinToggleMs)
            return; // defer; re-evaluated on the next duty change or update

        _msi.SetCoolerBoost(toggleOn, _snapshot.CoolerBoostBase);
        _boostActive = toggleOn;
        _lastBoostToggleUtc = now;
        Log(toggleOn ? "Cooler Boost ON (instant max fan)" : "Cooler Boost OFF");
    }

    private void RegisterFailure(Exception e, bool quiet = false)
    {
        _consecutiveFailures++;
        NoteAnomaly();

        if (_consecutiveFailures == FailuresBeforeGivingUp)
        {
            Log("EC access keeps failing: " + e.Message);
            foreach (var channel in ActiveChannels())
                channel.Temp = channel.Percent = channel.Rpm = null;
        }

        if (_consecutiveFailures >= FailuresBeforeBackoff)
        {
            _suspendedUntilUtc = DateTime.UtcNow.AddMilliseconds(_backoffMs);
            if (!quiet)
                Log($"Backing off EC access for {_backoffMs / 1000}s (letting the ACPI driver recover)");
            _backoffMs = Math.Min(_backoffMs * 2, BackoffMaxMs);
        }
    }

    /// <summary>
    /// Health tracking: every anomaly (failed/rejected write, timeout,
    /// implausible read) is evidence of bus contention. Two within ten minutes
    /// mean the EC channel is unstable — stop writing for a long cool-down so
    /// the kernel's ACPI traffic (battery, thermal) is never contended.
    /// </summary>
    private void NoteAnomaly()
    {
        var now = DateTime.UtcNow;
        _recentAnomalies.Add(now);
        _recentAnomalies.RemoveAll(t => (now - t).TotalMilliseconds > AnomalyWindowMs);

        if (_recentAnomalies.Count >= AnomalySuspendThreshold && now >= _writesSuspendedUntilUtc)
        {
            _writesSuspendedUntilUtc = now.AddMilliseconds(WriteSuspendMs);
            Log($"EC channel looks unstable ({_recentAnomalies.Count} anomalies in {AnomalyWindowMs / 60000} min) — " +
                $"suspending all EC writes for {WriteSuspendMs / 60000} min. The fan keeps its current speed; " +
                "the EC-side thermal failsafe stays active.");
        }
    }

    internal void ResetFan(Channel channel)
    {
        lock (_lock)
        {
            channel.PendingDuty = null;
            if (!channel.ControlActive || _msi is null || _snapshot is null)
                return;

            try
            {
                if (_boostActive)
                {
                    _msi.SetCoolerBoost(false, _snapshot.CoolerBoostBase);
                    _boostActive = false;
                    Log("Cooler Boost OFF");
                }

                _msi.RestoreCurve(channel.Regs, CurveFor(channel));
                channel.ControlActive = false;
                channel.LastApplied = null;

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
            return channel.ControlActive ? (channel.PendingDuty ?? channel.LastApplied) : channel.Percent;
    }

    private void RestoreEverything()
    {
        if (_boostActive)
        {
            _msi!.SetCoolerBoost(false, _snapshot!.CoolerBoostBase);
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
        public int? PendingDuty;
        public DateTime LastWriteUtc = DateTime.MinValue;
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
