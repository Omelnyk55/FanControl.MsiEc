using System.Text.Json;

namespace FanControl.MsiEc;

/// <summary>
/// Optional user configuration, read from "FanControl.MsiEc.json" next to the
/// plugin dll. Missing file or unreadable content means defaults.
/// </summary>
internal sealed class PluginConfig
{
    /// <summary>
    /// Extra EC firmware prefixes to treat as supported, e.g. ["1552EMS1"].
    /// Only for users who verified their MSI laptop uses the standard EC layout.
    /// </summary>
    public string[] AdditionalFirmwarePrefixes { get; set; } = [];

    /// <summary>"auto" (detect at startup), "on", or "off".</summary>
    public string GpuFan { get; set; } = "auto";

    /// <summary>
    /// Engage MSI Cooler Boost when a control is asked for at least this duty
    /// (1..100). Boost bypasses the EC's slow internal ramp — fans hit max in
    /// ~2 s instead of ~1 %/s. 0 disables the feature. Default: 100.
    /// </summary>
    public int CoolerBoostAtDuty { get; set; } = 100;

    public static string ConfigPath =>
        Path.Combine(Path.GetDirectoryName(typeof(PluginConfig).Assembly.Location) ?? ".", "FanControl.MsiEc.json");

    public static PluginConfig Load(Action<string>? log)
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new PluginConfig();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var config = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(ConfigPath), options);
            if (config is not null)
            {
                log?.Invoke($"Loaded config: gpuFan={config.GpuFan}, coolerBoostAtDuty={config.CoolerBoostAtDuty}, extra firmware prefixes=[{string.Join(",", config.AdditionalFirmwarePrefixes)}]");
                return config;
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Failed to read {ConfigPath}: {e.Message} — using defaults");
        }

        return new PluginConfig();
    }
}
