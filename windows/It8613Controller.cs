// Low-level IT8613E Fan4 (CPU fan) control via the PawnIO "LpcIO" module.
//
// Root cause: on this Topton/CWWK board the IT8613 EC (SmartGuardian) permanently owns
// Fan4's PWM *control* register (0x7f) and reverts any write to it, so the documented
// "clear bit 7 for manual mode" approach (used for fans 1-3) does NOT work for Fan4 — and
// the board's BIOS only offers Smart-Control on (auto curve) or off (full speed), with no
// software/manual mode.
//
// Fix: don't fight the EC for manual mode. Instead reprogram the SmartGuardian *curve*
// the EC follows so that it outputs a constant PWM we choose, regardless of temperature.
// The curve setpoint registers are NOT write-locked (only the control register is), so
// this sticks. FanControl drives our target PWM from the user's CPU curve.
//
// Fan4 newer-autopwm SmartGuardian curve block (base 0x78), verified live on the chip:
//   0x78 fan-off temp, 0x79 fan-start temp, 0x7a fan-max(full-speed) temp,
//   0x7b start-PWM (0..255), 0x7c PWM slope (rise per degree), 0x7d hysteresis.
// Setting slope = 0 makes output a flat line at start-PWM; a high max-temp keeps the EC
// from ramping to full. EC runtime access: index port = base+0x05, data port = base+0x06.

using System;
using System.Threading;

namespace FanControl.IT8613
{
    internal sealed class It8613Controller : IDisposable
    {
        private const string ModuleResource = "FanControl.IT8613.LpcIO.bin";
        private const string IsaBusMutexName = "Global\\Access_ISABUS.HTP.Method";

        // Super-I/O config sequence constants (port 0x2E/0x2F).
        private const byte CHIP_ID_REGISTER = 0x20;
        private const byte BASE_ADDRESS_REGISTER = 0x60;
        private const byte LDN_SELECT_REGISTER = 0x07;
        private const byte ENVIRONMENT_CONTROLLER_LDN = 0x04;
        private const byte CONFIG_CONTROL_REGISTER = 0x02;

        // EC register offsets from the discovered base address.
        private const byte ADDRESS_REGISTER_OFFSET = 0x05;
        private const byte DATA_REGISTER_OFFSET = 0x06;

        // Fan4 control register (EC-owned, read-only in practice) and tachometer.
        private const byte FAN4_PWM_CTRL_REG = 0x7f;
        private const byte FAN4_TACH_REG = 0x80;
        private const byte FAN4_TACH_EXT_REG = 0x81;

        // Fan4 SmartGuardian curve block (base 0x78).
        private const byte AUTO_OFF_TEMP = 0x78;
        private const byte AUTO_START_TEMP = 0x79;
        private const byte AUTO_MAX_TEMP = 0x7a;
        private const byte AUTO_START_PWM = 0x7b;
        private const byte AUTO_SLOPE = 0x7c;
        private static readonly byte[] CurveRegs = { 0x78, 0x79, 0x7a, 0x7b, 0x7c, 0x7d };

        // Thermal failsafe: if our software stops updating the curve and the CPU still
        // climbs past this temperature, the EC ramps Fan4 to full speed on its own.
        // Below it, Fan4 follows our flat setpoint. 0x5F = 95 C (CPU Tjmax is 100 C).
        private const byte FAILSAFE_FULL_SPEED_TEMP = 0x5f;

        private readonly PawnIo _pawn = new PawnIo();
        private readonly Mutex _isaMutex;
        private readonly Action<string> _log;

        private ushort _addressReg;
        private ushort _dataReg;
        private byte _initialCtrl;
        private readonly byte[] _initialCurve = new byte[6];
        private bool _ready;

        public It8613Controller(Action<string> log = null)
        {
            _log = log;
            _isaMutex = TryOpenIsaMutex();
        }

        public bool Ready => _ready;
        public string LastError { get; private set; } = "not initialized";

        public bool Initialize()
        {
            if (!_pawn.LoadModuleFromResource(typeof(It8613Controller).Assembly, ModuleResource))
            {
                LastError = "PawnIO not available or LpcIO module failed to load (is PawnIO installed?).";
                return false;
            }

            if (!WaitIsa(250))
            {
                LastError = "Could not acquire the ISA bus mutex.";
                return false;
            }

            try
            {
                SelectSlot(0);          // primary Super-I/O at 0x2E
                IT87Enter();
                ushort chipId = SioReadWord(CHIP_ID_REGISTER);
                if (chipId != 0x8613)
                {
                    IT87Exit();
                    LastError = "IT8613 not found (chip id 0x" + chipId.ToString("X4") + ").";
                    return false;
                }

                FindBars();             // authorize PawnIO pio access to the discovered BARs
                Select(ENVIRONMENT_CONTROLLER_LDN);
                ushort baseAddress = SioReadWord(BASE_ADDRESS_REGISTER);
                IT87Exit();

                if (baseAddress < 0x100 || (baseAddress & 0xF007) != 0)
                {
                    LastError = "Invalid EC base address 0x" + baseAddress.ToString("X4") + ".";
                    return false;
                }

                _addressReg = (ushort)(baseAddress + ADDRESS_REGISTER_OFFSET);
                _dataReg = (ushort)(baseAddress + DATA_REGISTER_OFFSET);

                // Remember the firmware defaults so Reset/Close can hand Fan4 back to the EC.
                _initialCtrl = EcRead(FAN4_PWM_CTRL_REG);
                for (int i = 0; i < CurveRegs.Length; i++)
                    _initialCurve[i] = EcRead(CurveRegs[i]);

                _ready = true;
                LastError = "ok; EC base 0x" + baseAddress.ToString("X4");
                return true;
            }
            finally
            {
                ReleaseIsa();
            }
        }

        /// <summary>
        /// Make the EC output a constant PWM (0..255) on Fan4 by flattening its
        /// SmartGuardian curve: slope 0, start-PWM = target, with a high-temp failsafe.
        /// </summary>
        public void ApplyDuty(byte duty)
        {
            if (!_ready || !WaitIsa(50))
                return;

            try
            {
                EcWrite(AUTO_OFF_TEMP, 0x00);               // never below "off" temp
                EcWrite(AUTO_START_TEMP, 0x00);             // always past "start" temp
                EcWrite(AUTO_MAX_TEMP, FAILSAFE_FULL_SPEED_TEMP); // ramp to full only as a failsafe
                EcWrite(AUTO_SLOPE, 0x00);                  // no PWM rise with temperature
                EcWrite(AUTO_START_PWM, duty);              // constant output = our target
            }
            finally
            {
                ReleaseIsa();
            }
        }

        /// <summary>
        /// Restore Fan4's original SmartGuardian curve so the BIOS auto behavior returns
        /// (called when the control is disabled or FanControl closes).
        /// </summary>
        public void RestoreDefault()
        {
            if (!_ready || !WaitIsa(50))
                return;

            try
            {
                for (int i = 0; i < CurveRegs.Length; i++)
                    EcWrite(CurveRegs[i], _initialCurve[i]);
                EcWrite(FAN4_PWM_CTRL_REG, _initialCtrl);
            }
            finally
            {
                ReleaseIsa();
            }
        }

        public float ReadRpm()
        {
            if (!_ready || !WaitIsa(50))
                return 0;

            try
            {
                int value = EcRead(FAN4_TACH_REG);
                value |= EcRead(FAN4_TACH_EXT_REG) << 8;
                if (value > 0x3f && value < 0xffff)
                    return 1.35e6f / (value * 2);
                return 0;
            }
            finally
            {
                ReleaseIsa();
            }
        }

        // ---- EC runtime register access (raw port I/O to base+5 / base+6) ----

        private byte EcRead(byte register)
        {
            PioOutb(_addressReg, register);
            return PioInb(_dataReg);
        }

        private void EcWrite(byte register, byte value)
        {
            PioOutb(_addressReg, register);
            PioOutb(_dataReg, value);
        }

        // ---- PawnIO LpcIO module ioctls ----

        private void SelectSlot(int slot) => _pawn.Execute("ioctl_select_slot", new long[] { slot }, 0);
        private void FindBars() => _pawn.Execute("ioctl_find_bars", Array.Empty<long>(), 0);
        private byte PioInb(ushort port) => (byte)_pawn.Execute("ioctl_pio_inb", new long[] { port }, 1)[0];
        private void PioOutb(ushort port, byte value) => _pawn.Execute("ioctl_pio_outb", new long[] { port, value }, 0);
        private ushort SioReadWord(byte register) => (ushort)_pawn.Execute("ioctl_superio_inw", new long[] { register }, 1)[0];
        private void SioWriteByte(byte register, byte value) => _pawn.Execute("ioctl_superio_outb", new long[] { register, value }, 0);

        private void Select(byte ldn) => SioWriteByte(LDN_SELECT_REGISTER, ldn);

        private void IT87Enter()
        {
            // IT87 Super-I/O config entry key on port 0x2E.
            PioOutb(0x2E, 0x87);
            PioOutb(0x2E, 0x01);
            PioOutb(0x2E, 0x55);
            PioOutb(0x2E, 0x55);
        }

        private void IT87Exit()
        {
            SioWriteByte(CONFIG_CONTROL_REGISTER, 0x02);
        }

        // ---- ISA bus mutex (shared with FanControl's own LHM instance) ----

        private static Mutex TryOpenIsaMutex()
        {
            try
            {
                return Mutex.OpenExisting(IsaBusMutexName);
            }
            catch
            {
                // FanControl's LHM creates it at startup; if it isn't there yet we simply
                // run without it (best effort) rather than failing.
                return null;
            }
        }

        private bool WaitIsa(int millisecondsTimeout)
        {
            if (_isaMutex == null)
                return true;

            try
            {
                return _isaMutex.WaitOne(millisecondsTimeout, false);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ReleaseIsa()
        {
            try { _isaMutex?.ReleaseMutex(); }
            catch { /* not owned */ }
        }

        public void Dispose()
        {
            try { _pawn?.Dispose(); } catch { }
            try { _isaMutex?.Dispose(); } catch { }
        }
    }
}
