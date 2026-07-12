# FanControl.MsiEc

[FanControl](https://github.com/Rem0o/FanControl.Releases) plugin that adds fan
monitoring and **direct fan control** for MSI laptops — the missing piece for
machines whose fans are normally only adjustable through MSI Center /
Creator Center ([requested here](https://github.com/Rem0o/FanControl.Releases/issues/2177)).

**Verified hardware:** EC firmware family `14B3EMS1` — MSI PS42 Modern 8MO/8RA,
Modern 14 A10M/A10RB. Other MSI laptops with the standard EC layout can opt in
via the config file (see below).

## Install (no compilation needed)

1. Install [FanControl](https://github.com/Rem0o/FanControl.Releases) (the
   standard .NET build, V271+).
2. Make sure the [PawnIO](https://pawnio.eu) driver is installed — FanControl
   itself offers to install it, or grab the installer from pawnio.eu.
3. Drop `FanControl.MsiEc.dll` into FanControl's `Plugins` folder and restart
   FanControl.

That's it. You get:

- **CPU (MSI EC)** temperature sensor,
- **CPU Fan (MSI EC)** RPM sensor (plus GPU ones on dual-fan models),
- **CPU Fan (MSI EC)** control — already paired with its RPM sensor, ready for
  a curve or the calibration assistant.

Sensor values update at FanControl's normal cycle; controls accept 0–100 %.

## Safety design

- The plugin **reads the EC firmware version first** (register `0xA0`) and
  disables itself on anything not in the verified list — it never writes to an
  unknown EC.
- Writes go only to the documented fan registers (curve tables + fan mode),
  every byte is **read back and verified**.
- **Thermal failsafe:** the two hottest bands of the EC's own curve table are
  never written below their floors (≥ ~80 °C: min 80 % duty; top band: always
  100 %). Even if the OS or FanControl freezes while a quiet duty is set, an
  overheating CPU/GPU still gets full airflow — this logic lives in the EC,
  not in software.
- The factory fan curve and mode are captured at startup and restored when you
  disable the control, close FanControl, or the plugin unloads.
- Worst case (a frozen EC — never observed): power the laptop off and hold the
  power button ~30 s; the EC resets with factory behavior.
- EC access uses the signed, HVCI-compatible [PawnIO](https://pawnio.eu)
  driver with the official
  [`LpcACPIEC`](https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p)
  module (port I/O restricted to the ACPI EC ports `0x62`/`0x66`), so it works
  with Windows Memory Integrity (HVCI) enabled. Transactions are serialized
  through the system-wide `Access_EC` mutex shared with other monitoring tools.

## Configuration (optional)

Create `FanControl.MsiEc.json` next to the dll (see
`FanControl.MsiEc.json.example`):

```json
{
  // Opt-in for EC firmwares not in the built-in verified list.
  // Check yours first (e.g. HWiNFO → Embedded Controller, or MSI's EC update
  // package name). Only do this if you know your model uses the standard
  // MSI EC layout (most 2018+ consumer/creator models do).
  "additionalFirmwarePrefixes": [],

  // "auto" (default): GPU fan sensors appear if the EC reports GPU activity
  // at startup. "on"/"off" to force.
  "gpuFan": "auto"
}
```

If your firmware isn't listed, the plugin shows exactly what to put in this
file — and please open an issue with your model + firmware string so it can be
added to the verified list.

## Technical notes

Register map (standard MSI layout, confirmed live on `14B3EMS1.102`):

| Register | Meaning |
|----------|---------|
| `0xA0..0xAB` | EC firmware version string |
| `0x68` / `0x80` | CPU / GPU temperature, °C |
| `0x71` / `0x89` | current fan duty, % |
| `0xCC/0xCD`, `0xCA/0xCB` | fan tachometer, RPM = 478000 / raw (big-endian) |
| `0x72..0x78`, `0x8A..0x90` | fan curve duty tables (7 temperature bands) |
| `0xF4` | fan mode: `0x0D` auto, `0x8D` advanced |

Control works like MSI Center's advanced mode: the plugin switches the EC to
advanced fan mode and writes a *flat* duty table, so the value FanControl asks
for applies at any temperature (except the failsafe bands above).

## Build from source

```
dotnet build src/FanControl.MsiEc/FanControl.MsiEc.csproj -c Release -p:FanControlDir=<path to FanControl>
```

## Українською (коротко)

1. Встановіть FanControl і драйвер PawnIO (FanControl сам запропонує).
2. Покладіть `FanControl.MsiEc.dll` у папку `Plugins` FanControl і
   перезапустіть програму.
3. З'являться датчики температури/обертів та керування вентилятором — далі
   звичайні криві FanControl.

## Licensing

Plugin code: MIT (see `LICENSE`). The embedded `LpcACPIEC.bin` PawnIO module
is LGPL-2.1-or-later, © namazso — source:
[PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) (see
`THIRD-PARTY-NOTICES.md`).
