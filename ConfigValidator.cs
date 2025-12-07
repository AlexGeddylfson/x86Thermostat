// ConfigValidator.cs - Comprehensive configuration validation
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ThermostatController
{
    public class ConfigValidator
    {
        private readonly ThermostatConfig _config;
        private readonly ILogger _logger;
        private readonly List<string> _warnings = new();
        private readonly List<string> _errors = new();

        public ConfigValidator(ThermostatConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public bool Validate()
        {
            _logger.LogInformation("========================================");
            _logger.LogInformation("Validating Configuration...");
            _logger.LogInformation("========================================");

            ValidatePlatformCompatibility();
            ValidateHardwareMode();
            ValidatePortConfiguration();
            ValidateGpioConfiguration();
            ValidateRelayCommands();
            ValidateTemperatureSettings();
            ValidateTimingSettings();

            // Log all warnings and errors
            if (_warnings.Count > 0)
            {
                _logger.LogInformation("Configuration Warnings:");
                foreach (var warning in _warnings)
                {
                    _logger.LogWarning("  ⚠️  {Warning}", warning);
                }
            }

            if (_errors.Count > 0)
            {
                _logger.LogError("Configuration Errors:");
                foreach (var error in _errors)
                {
                    _logger.LogError("{Error}", error);
                }
                _logger.LogError("Configuration validation failed with {Count} error(s)", _errors.Count);
                return false;
            }

            _logger.LogInformation("Configuration validation passed with {Count} warning(s)", _warnings.Count);
            _logger.LogInformation("========================================");
            return true;
        }

        private void ValidatePlatformCompatibility()
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            _logger.LogInformation("Platform: {Platform}", RuntimeInformation.OSDescription);
            _logger.LogInformation("Hardware Mode: {Mode}", _config.Mode);

            // Check for platform mismatches
            if (_config.Mode == HardwareMode.Windows && !isWindows)
            {
                _errors.Add("Hardware mode is set to 'Windows' but running on non-Windows platform");
            }

            if (_config.Mode == HardwareMode.Linux && isWindows)
            {
                _errors.Add("Hardware mode is set to 'Linux' but running on Windows platform");
            }

            // Warn about GPIO on Windows
            if (isWindows && _config.RelayPins.Any())
            {
                _warnings.Add("GPIO pins configured but running on Windows. GPIO mode will be skipped.");
            }
        }

        private void ValidateHardwareMode()
        {
            if (_config.Mode == HardwareMode.Auto)
            {
                _logger.LogInformation("Hardware mode is 'Auto' - system will probe for available hardware");
                return;
            }

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            // Validate mode-specific requirements
            if (_config.Mode == HardwareMode.Windows)
            {
                if (string.IsNullOrEmpty(_config.ArduinoComPort))
                {
                    _errors.Add("Windows mode requires ArduinoComPort to be configured");
                }
                
                if (_config.NeedsThermostatControl() && string.IsNullOrEmpty(_config.RelayComPort))
                {
                    _errors.Add("Windows thermostat mode requires RelayComPort to be configured");
                }
            }

            if (_config.Mode == HardwareMode.Linux)
            {
                _logger.LogInformation("Linux mode configured - will try FTDI, COM, then GPIO");
                
                // Check if any Linux-compatible hardware is configured
                bool hasLinuxConfig = !string.IsNullOrEmpty(_config.ArduinoComPort) ||
                                     !string.IsNullOrEmpty(_config.RelayComPort) ||
                                     _config.RelayPins.Any() ||
                                     _config.DhtSensorPin > 0 ||
                                     _config.EnableFtdiRelay;

                if (!hasLinuxConfig)
                {
                    _errors.Add("Linux mode requires at least one of: ArduinoComPort, RelayComPort, RelayPins, DhtSensorPin, or EnableFtdiRelay");
                }
            }
        }

        private void ValidatePortConfiguration()
        {
            if (!_config.NeedsSensorReading())
            {
                return; // Server-only mode doesn't need ports
            }

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            // Validate Arduino port
            if (!string.IsNullOrEmpty(_config.ArduinoComPort))
            {
                if (isWindows)
                {
                    // Windows COM port validation
                    if (!_config.ArduinoComPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        _warnings.Add($"ArduinoComPort '{_config.ArduinoComPort}' doesn't match Windows COM port format (e.g., COM3)");
                    }

                    var availablePorts = SerialPort.GetPortNames();
                    if (availablePorts.Length == 0)
                    {
                        _warnings.Add("No COM ports detected on system");
                    }
                    else if (!availablePorts.Contains(_config.ArduinoComPort))
                    {
                        _warnings.Add($"ArduinoComPort '{_config.ArduinoComPort}' not found. Available: {string.Join(", ", availablePorts)}");
                    }
                    else
                    {
                        _logger.LogInformation("✓ Arduino port '{Port}' is available", _config.ArduinoComPort);
                    }
                }
                else
                {
                    // Linux port validation
                    string portPath = _config.ArduinoComPort.StartsWith("/dev/") 
                        ? _config.ArduinoComPort 
                        : $"/dev/{_config.ArduinoComPort}";

                    if (File.Exists(portPath))
                    {
                        _logger.LogInformation("Arduino port '{Port}' exists", portPath);
                    }
                    else
                    {
                        _warnings.Add($"ArduinoComPort '{portPath}' not found. If using GPIO, this is expected.");
                    }
                }
            }
            else if (_config.Mode == HardwareMode.Windows)
            {
                _errors.Add("Windows mode requires ArduinoComPort to be configured");
            }

            // Validate Relay port (if needed)
            if (_config.NeedsThermostatControl() && !string.IsNullOrEmpty(_config.RelayComPort))
            {
                if (isWindows)
                {
                    if (!_config.RelayComPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                    {
                        _warnings.Add($"RelayComPort '{_config.RelayComPort}' doesn't match Windows COM port format (e.g., COM4)");
                    }

                    var availablePorts = SerialPort.GetPortNames();
                    if (!availablePorts.Contains(_config.RelayComPort))
                    {
                        _warnings.Add($"RelayComPort '{_config.RelayComPort}' not found. Available: {string.Join(", ", availablePorts)}");
                    }
                    else
                    {
                        _logger.LogInformation("✓ Relay port '{Port}' is available", _config.RelayComPort);
                    }
                }
                else
                {
                    string portPath = _config.RelayComPort.StartsWith("/dev/") 
                        ? _config.RelayComPort 
                        : $"/dev/{_config.RelayComPort}";

                    if (File.Exists(portPath))
                    {
                        _logger.LogInformation("✓ Relay port '{Port}' exists", portPath);
                    }
                    else
                    {
                        _warnings.Add($"RelayComPort '{portPath}' not found. If using GPIO/FTDI, this is expected.");
                    }
                }
            }

            // Validate FTDI configuration
            if (_config.EnableFtdiRelay)
            {
                if (string.IsNullOrEmpty(_config.FtdiSerialNumber))
                {
                    _errors.Add("FTDI relay is enabled but FtdiSerialNumber is not configured");
                }
                else
                {
                    _logger.LogInformation("FTDI relay enabled with serial: {Serial}", _config.FtdiSerialNumber);
                }

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    _errors.Add("FTDI relay is only supported on Linux");
                }
            }
        }

        private void ValidateGpioConfiguration()
        {
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            if (!isLinux && _config.RelayPins.Any())
            {
                _warnings.Add("GPIO pins configured but not on Linux platform - GPIO mode will be unavailable");
                return;
            }

            if (_config.NeedsThermostatControl() && _config.RelayPins.Count > 0 && _config.RelayPins.Count < 4)
            {
                _warnings.Add($"Thermostat mode typically requires 4 relay pins, but only {_config.RelayPins.Count} configured");
            }

            // Validate pin numbers (common Raspberry Pi GPIO pins - BCM numbering)
            var validPins = new[] { 2, 3, 4, 17, 27, 22, 10, 9, 11, 5, 6, 13, 19, 26, 14, 15, 18, 23, 24, 25, 8, 7, 12, 16, 20, 21 };
            foreach (var pin in _config.RelayPins)
            {
                if (!validPins.Contains(pin))
                {
                    _warnings.Add($"GPIO pin {pin} may not be a valid BCM pin number for Raspberry Pi");
                }
            }

            // Check for duplicate pins
            var duplicates = _config.RelayPins.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key);
            foreach (var dup in duplicates)
            {
                _errors.Add($"GPIO pin {dup} is configured multiple times");
            }

            // Validate DHT sensor pin
            if (_config.DhtSensorPin > 0)
            {
                if (!validPins.Contains(_config.DhtSensorPin))
                {
                    _warnings.Add($"DHT sensor pin {_config.DhtSensorPin} may not be a valid BCM pin number");
                }

                if (_config.RelayPins.Contains(_config.DhtSensorPin))
                {
                    _errors.Add($"DHT sensor pin {_config.DhtSensorPin} conflicts with relay pin configuration");
                }

                _logger.LogInformation("GPIO Configuration:");
                _logger.LogInformation("  Relay pins: [{Pins}]", string.Join(", ", _config.RelayPins));
                _logger.LogInformation("  DHT pin: {DHT}", _config.DhtSensorPin);
            }
        }

        private void ValidateRelayCommands()
        {
            if (!_config.NeedsThermostatControl())
            {
                return;
            }

            _logger.LogInformation("Validating relay commands...");

            try
            {
                var offBytes = _config.RelayCommands.Off.ToBytes();
                _logger.LogInformation("  Off: {Bytes}", BitConverter.ToString(offBytes));
            }
            catch (Exception ex)
            {
                _errors.Add($"Invalid Off command: {ex.Message}");
            }

            try
            {
                var fanBytes = _config.RelayCommands.FanOnly.ToBytes();
                _logger.LogInformation("  FanOnly: {Bytes}", BitConverter.ToString(fanBytes));
            }
            catch (Exception ex)
            {
                _errors.Add($"Invalid FanOnly command: {ex.Message}");
            }

            try
            {
                var coolBytes = _config.RelayCommands.Cool.ToBytes();
                _logger.LogInformation("  Cool: {Bytes}", BitConverter.ToString(coolBytes));
            }
            catch (Exception ex)
            {
                _errors.Add($"Invalid Cool command: {ex.Message}");
            }

            try
            {
                var heatBytes = _config.RelayCommands.Heat.ToBytes();
                _logger.LogInformation("  Heat: {Bytes}", BitConverter.ToString(heatBytes));
            }
            catch (Exception ex)
            {
                _errors.Add($"Invalid Heat command: {ex.Message}");
            }

            try
            {
                var emergencyBytes = _config.RelayCommands.Emergency.ToBytes();
                _logger.LogInformation("  Emergency: {Bytes}", BitConverter.ToString(emergencyBytes));
            }
            catch (Exception ex)
            {
                _errors.Add($"Invalid Emergency command: {ex.Message}");
            }
        }

        private void ValidateTemperatureSettings()
        {
            if (_config.TemperatureUnit != "F" && _config.TemperatureUnit != "C")
            {
                _errors.Add($"Invalid TemperatureUnit: '{_config.TemperatureUnit}' (must be 'F' or 'C')");
            }

            if (_config.CoolingSetTemperatureOffset < 0 || _config.CoolingSetTemperatureOffset > 10)
            {
                _warnings.Add($"CoolingSetTemperatureOffset {_config.CoolingSetTemperatureOffset} is outside typical range (0-10)");
            }

            if (_config.HeatingSetTemperatureOffset < 0 || _config.HeatingSetTemperatureOffset > 10)
            {
                _warnings.Add($"HeatingSetTemperatureOffset {_config.HeatingSetTemperatureOffset} is outside typical range (0-10)");
            }

            if (_config.TemperatureDifferenceThreshold < 0.1 || _config.TemperatureDifferenceThreshold > 10)
            {
                _warnings.Add($"TemperatureDifferenceThreshold {_config.TemperatureDifferenceThreshold} is outside typical range (0.1-10)");
            }

            if (_config.DefaultUserSetTemperature < 50 || _config.DefaultUserSetTemperature > 90)
            {
                _warnings.Add($"DefaultUserSetTemperature {_config.DefaultUserSetTemperature} is outside typical range (50-90°F)");
            }

            if (_config.MinimumHeatingRatePer10Min < 0.1 || _config.MinimumHeatingRatePer10Min > 5)
            {
                _warnings.Add($"MinimumHeatingRatePer10Min {_config.MinimumHeatingRatePer10Min} is outside typical range (0.1-5)");
            }
        }

        private void ValidateTimingSettings()
        {
            if (_config.InitialOffDurationSeconds < 10 || _config.InitialOffDurationSeconds > 600)
            {
                _warnings.Add($"InitialOffDurationSeconds {_config.InitialOffDurationSeconds} is outside typical range (10-600)");
            }

            if (_config.EmergencyHeatDelaySeconds < 300 || _config.EmergencyHeatDelaySeconds > 7200)
            {
                _warnings.Add($"EmergencyHeatDelaySeconds {_config.EmergencyHeatDelaySeconds} is outside typical range (300-7200)");
            }

            if (_config.CompressorMinOffMinutes < 1 || _config.CompressorMinOffMinutes > 30)
            {
                _warnings.Add($"CompressorMinOffMinutes {_config.CompressorMinOffMinutes} is outside typical range (1-30)");
            }

            if (_config.SensorPollIntervalSeconds < 5 || _config.SensorPollIntervalSeconds > 120)
            {
                _warnings.Add($"SensorPollIntervalSeconds {_config.SensorPollIntervalSeconds} is outside typical range (5-120)");
            }

            if (_config.DataSendIntervalSeconds < 30 || _config.DataSendIntervalSeconds > 600)
            {
                _warnings.Add($"DataSendIntervalSeconds {_config.DataSendIntervalSeconds} is outside typical range (30-600)");
            }

            if (_config.ControlLoopIntervalMs < 1000 || _config.ControlLoopIntervalMs > 60000)
            {
                _warnings.Add($"ControlLoopIntervalMs {_config.ControlLoopIntervalMs} is outside typical range (1000-60000)");
            }

            if (_config.ComTimeout < 500 || _config.ComTimeout > 10000)
            {
                _warnings.Add($"ComTimeout {_config.ComTimeout} is outside typical range (500-10000)");
            }
        }
    }
}
