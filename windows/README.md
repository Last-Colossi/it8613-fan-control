# IT8613 Fan4 (CPU fan) — Windows FanControl plugin

A [FanControl](https://getfancontrol.com/) plugin that gives you real control over the
**CPU fan (Fan4)** on Topton/CWWK boards using the ITE **IT8613E** Super‑I/O — the Windows
companion to the Linux `it87` driver setup in the [root of this repo](../README.md).

## The problem

On these boards the IT8613's embedded controller (EC) runs ITE **SmartGuardian** and
**permanently owns Fan4's PWM control register (`0x7f`)** — it silently reverts every write
to it. The board's BIOS only exposes CPU "Smart Control" as *on* (auto curve) or *off*
(full speed); there is no manual/software mode.

The result under Windows: FanControl (and the LibreHardwareMonitor engine it uses) appears
to control the CPU fan at idle, but the moment the CPU warms up the EC's auto curve takes
over and the fan spikes — software writes to the duty register are ignored because the
channel never actually leaves automatic mode. The normal "clear bit 7 for manual mode"
trick works for the case fans (registers `0x15/0x16/0x17`) but **not** for Fan4.

## The fix

Instead of fighting the EC for manual mode, this plugin **reprograms the EC's own
SmartGuardian curve for Fan4 to be flat**, so the EC outputs exactly the PWM we ask for at
any temperature. The curve *setpoint* registers are not write‑locked (only the control
register is), so this sticks.

Fan4's newer‑autopwm curve block (base `0x78`):

| Register | Meaning | Value written |
|---|---|---|
| `0x78` | fan‑off temperature | `0x00` (never below) |
| `0x79` | fan‑start temperature | `0x00` (always in range) |
| `0x7a` | fan‑max / full‑speed temperature | `0x5F` = **95 °C** (thermal failsafe) |
| `0x7b` | start PWM (0–255) | **your target duty** |
| `0x7c` | PWM slope (rise per °C) | `0x00` (flat — no temp ramp) |

With slope `0`, the EC's output is a flat line at the start‑PWM value, which FanControl
drives from your CPU curve. The `0x7a` value is a safety net: if the plugin/FanControl ever
stops updating the curve and the CPU still climbs to 95 °C, the EC ramps Fan4 to full on
its own (CPU Tjmax is 100 °C).

The plugin talks to the chip through **[PawnIO](https://pawnio.eu/)** (the same signed,
HVCI‑compatible ring‑0 mechanism LibreHardwareMonitor uses), so no custom kernel driver is
needed and it works with Memory Integrity / Core Isolation enabled.

## Requirements

- Windows 10/11 on a Topton/CWWK board with an **IT8613E** Super‑I/O.
- **FanControl** (tested on V269) — it runs elevated and ships with PawnIO.
- **PawnIO** installed (FanControl/LibreHardwareMonitor installs it; verify it exists under
  `C:\Program Files\PawnIO`).
- To build: the **.NET 10 SDK**.

## Install

1. Copy `FanControl.IT8613.dll` into FanControl's `Plugins` folder
   (`C:\Program Files (x86)\FanControl\Plugins\`). This needs admin.
2. Restart FanControl. You should see `[IT8613] Ready` in its `log.txt` and a new control
   **"IT8613 CPU Fan (Fan4)"** plus a fan sensor under Settings → Plugins.
3. On the Home screen:
   - Point your CPU temperature curve at the **"IT8613 CPU Fan (Fan4)"** control.
   - **Disable FanControl's built‑in "CPU Fan" control** so the two don't both write Fan4.

## Build

```powershell
dotnet build FanControl.IT8613.csproj -c Release
```

Targets `net10.0-windows`, references `FanControl.Plugins.dll` from the FanControl install
directory, and embeds `LpcIO.bin`. Output: `bin/Release/FanControl.IT8613.dll` (a single
self‑contained DLL).

## Files

- `IT8613Plugin.cs` — FanControl plugin entry point (control + RPM sensor).
- `It8613Controller.cs` — IT8613 EC access and the flat‑curve logic.
- `PawnIo.cs` — minimal PawnIO client (talks to the driver via `DeviceIoControl`).
- `LpcIO.bin` — the signed PawnIO "LpcIO" Super‑I/O module (from
  [namazso/PawnIO_Modules](https://github.com/namazso/PawnIO_Modules), as redistributed in
  LibreHardwareMonitor). Used to do Super‑I/O / port I/O.

## Credits & licence

Register layout and access patterns are derived from
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL‑2.0)
and the [frankcrawford/it87](https://github.com/frankcrawford/it87) Linux driver (GPL‑2.0).
PawnIO and the `LpcIO` module are by [namazso](https://pawnio.eu/). Provided as‑is; writing
to embedded‑controller registers is inherently board‑specific — use at your own risk.
