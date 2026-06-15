// FanControl plugin: holds the IT8613 Fan4 (CPU fan) in software PWM mode so the
// motherboard EC's SmartGuardian can't reclaim it under load.
//
// Usage in FanControl:
//   1. A control named "IT8613 CPU Fan (Fan4)" appears under Controls.
//   2. Point your CPU temperature curve at THIS control (and remove/disable the
//      built-in "CPU Fan" control so the two don't both write Fan4).
//   3. A fan sensor "IT8613 CPU Fan (Fan4) RPM" is also exposed for monitoring.

using System;
using System.Threading;
using FanControl.Plugins;

namespace FanControl.IT8613
{
    public class IT8613Plugin : IPlugin2
    {
        private readonly IPluginLogger _logger;
        private It8613Controller _controller;
        private It8613Control _control;
        private It8613FanSensor _fan;
        private Timer _holdTimer;

        // FanControl injects the logger into the constructor.
        public IT8613Plugin(IPluginLogger logger)
        {
            _logger = logger;
        }

        public string Name => "IT8613 Fan4 Holder";

        public void Initialize()
        {
            _controller = new It8613Controller(s => _logger?.Log(s));
            if (!_controller.Initialize())
            {
                _logger?.Log("[IT8613] Disabled: " + _controller.LastError);
                _controller.Dispose();
                _controller = null;
                return;
            }

            _logger?.Log("[IT8613] Ready (" + _controller.LastError + "). Holding Fan4 in software mode.");
            _control = new It8613Control(_controller);
            _fan = new It8613FanSensor(_controller);

            // Re-assert software mode independently of FanControl's own cycle, so the EC
            // never gets a long enough window to take Fan4 back.
            _holdTimer = new Timer(_ =>
            {
                try { _control.Reassert(); }
                catch { /* transient bus contention; next tick retries */ }
            }, null, 500, 500);
        }

        public void Load(IPluginSensorsContainer container)
        {
            if (_controller == null)
                return;

            container.ControlSensors.Add(_control);
            container.FanSensors.Add(_fan);
        }

        public void Update()
        {
            if (_controller == null)
                return;

            _fan.Update();
            _control.Reassert();
        }

        public void Close()
        {
            try { _holdTimer?.Dispose(); } catch { }
            _holdTimer = null;

            try { _control?.Disengage(); } catch { }
            try { _controller?.RestoreDefault(); } catch { }
            try { _controller?.Dispose(); } catch { }
            _controller = null;
        }
    }

    internal sealed class It8613Control : IPluginControlSensor
    {
        private readonly It8613Controller _controller;
        private readonly object _lock = new object();
        private bool _engaged;
        private byte _duty;
        private float? _percent;

        public It8613Control(It8613Controller controller)
        {
            _controller = controller;
        }

        public string Id => "it8613-fan4-cpu";
        public string Name => "IT8613 CPU Fan (Fan4)";
        public float? Value => _percent;

        public void Update() { /* value is driven by Set(), nothing to poll */ }

        public void Set(float val)
        {
            if (val < 0f) val = 0f;
            if (val > 100f) val = 100f;

            lock (_lock)
            {
                _duty = (byte)Math.Round(val / 100f * 255f);
                _percent = val;
                _engaged = true;
            }

            _controller.ApplyDuty(_duty);
        }

        public void Reset()
        {
            lock (_lock)
            {
                _engaged = false;
                _percent = null;
            }

            _controller.RestoreDefault();
        }

        // Called frequently from the hold timer and FanControl's update cycle.
        public void Reassert()
        {
            byte duty;
            lock (_lock)
            {
                if (!_engaged)
                    return;
                duty = _duty;
            }

            _controller.ApplyDuty(duty);
        }

        public void Disengage()
        {
            lock (_lock)
            {
                _engaged = false;
                _percent = null;
            }
        }
    }

    internal sealed class It8613FanSensor : IPluginSensor
    {
        private readonly It8613Controller _controller;

        public It8613FanSensor(It8613Controller controller)
        {
            _controller = controller;
        }

        public string Id => "it8613-fan4-rpm";
        public string Name => "IT8613 CPU Fan (Fan4) RPM";
        public float? Value { get; private set; }

        public void Update()
        {
            Value = _controller.ReadRpm();
        }
    }
}
