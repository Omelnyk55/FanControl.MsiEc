# Third-party notices

## LpcACPIEC (PawnIO module)

`FanControl.MsiEc.dll` embeds `LpcACPIEC.bin`, a compiled and signed PawnIO
module, unmodified, from the PawnIO.Modules project:

- Copyright (C) 2023 namazso <admin@namazso.eu>
- License: GNU Lesser General Public License v2.1 or later (LGPL-2.1-or-later)
- Source code: https://github.com/namazso/PawnIO.Modules
  (module source: `LpcACPIEC.p`; a copy is included in this repository at
  `src/FanControl.MsiEc/Modules/LpcACPIEC.p`)
- Binary releases: https://github.com/namazso/PawnIO.Modules/releases

The module is loaded at runtime into the separately installed PawnIO driver
(https://pawnio.eu). PawnIO itself is not distributed with this plugin.

To satisfy LGPL requirements, the embedded module can be replaced: rebuild the
plugin with a different `Modules/LpcACPIEC.bin` (it is a plain embedded
resource), or build the module yourself from the source linked above.
