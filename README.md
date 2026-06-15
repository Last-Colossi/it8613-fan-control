# IT8613E Fan Control for Linux

Fan monitoring and PWM control for motherboards using the **ITE IT8613E**
Super-I/O chip — including many Topton / CWWK style mini-ITX boards (e.g. the
TOPC "YUNIKI ITX WIFI D5" with the AMD Ryzen 5 7645HX).

The mainline Linux `it87` driver does **not** recognise the IT8613E, so out of
the box you get no fan readings and no fan control at all (`modprobe it87`
fails with `No such device`). This repo installs the out-of-tree
[frankcrawford/it87](https://github.com/frankcrawford/it87) driver, which has
native IT8613E support, and wires it up so it survives kernel updates with zero
maintenance.

## What it does

- Builds and loads the out-of-tree `it87` driver with full IT8613E support
  (all fan tachometers + PWM channels, including the CPU fan).
- A systemd service (`it87-load.service`) loads the driver at every boot, and
  **automatically rebuilds it after a kernel update** using the kernel headers
  already present on the system (no DKMS required, no network needed).
- If a build ever fails (e.g. a future kernel breaks the driver), it **falls
  back** to the mainline `it87` driver forced as an IT8620E. That keeps the
  case-fan channels and monitoring working — you never end up worse than the
  stock state.
- Handles the SELinux labelling needed to load a self-built module on Fedora
  atomic / Bazzite.

## Requirements

- A systemd-based Linux distribution.
- Build tools and matching kernel headers: `git`, `make`, `gcc`, and
  `/lib/modules/$(uname -r)/build`.
  - On **Bazzite / Fedora atomic** these ship in the image — nothing to do.
  - On other distros install the equivalent of `kernel-devel` for your
    running kernel plus `gcc`/`make`/`git`.
- Secure Boot **disabled**, or a MOK enrolled to sign the module — an unsigned
  self-built module will not load under Secure Boot.

## Install

```bash
git clone https://github.com/Last-Colossi/it8613-fan-control
cd it8613-fan-control
./install.sh
```

The script asks for `sudo`, fetches the driver, builds it, and starts the
service. Verify the chip is detected:

```bash
sensors
```

You should see an `it8613-isa-*` section with `fan2`/`fan3`/`fan4` RPMs.

> Note: a few of the chip's voltage and secondary temperature readings are
> not meaningful on this board and may show as `ALARM` in `sensors` — that's
> cosmetic. The fan tachometers, PWM control, and the main temperature are
> correct.

## Fan curves

This repo only provides the driver. To set fan curves, install
[CoolerControl](https://gitlab.com/coolercontrol/coolercontrol):

```bash
# Bazzite:
ujust install-coolercontrol
```

In CoolerControl the chip appears as `it8613` with `fan2`/`fan3` (case fans)
and `fan4` (CPU fan). Bind curves to your CPU temperature sensor (`k10temp`
"Tctl" on AMD). Give the CPU fan a sensible minimum (~30%) so it never stops.

### CoolerControl calibration error

If calibration fails with `Device or resource busy (os error 16)`, apply a
**Manual** control to the channel first (any fixed speed), then calibrate. The
it87 driver rejects PWM writes while a channel is still in automatic mode.

## Uninstall

```bash
./uninstall.sh
```

Removes the service, driver, config, and SELinux context, and reverts fans to
firmware control on the next boot.

## How it works

| Path | Purpose |
|------|---------|
| `/var/lib/it87/src` | cloned driver source |
| `/var/lib/it87/modules/<kver>/it87.ko` | built module, cached per kernel |
| `/usr/local/bin/it87-load.sh` | build-if-needed + load logic |
| `/etc/systemd/system/it87-load.service` | runs the loader at boot |
| `/etc/modprobe.d/it87.conf` | `force_id=0x8620` mainline fallback |

## Windows

If you dual-boot Windows, the same board has a different problem: the IT8613's
embedded controller hard-owns the CPU fan (Fan4) channel and overrides software,
so the fan spikes under load. A **FanControl plugin** that works around this (by
flattening the EC's own SmartGuardian curve) lives in [`windows/`](windows/) —
see [`windows/README.md`](windows/README.md).

## Credits

- Driver: [frankcrawford/it87](https://github.com/frankcrawford/it87)
- Fan curve GUI: [CoolerControl](https://gitlab.com/coolercontrol/coolercontrol)

## License

GPL-2.0, matching the it87 driver. See [LICENSE](LICENSE).
