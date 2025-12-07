// Hardware.cs - Hardware abstraction and implementations
using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Iot.Device.DHTxx;
using Microsoft.Extensions.Logging;
using UnitsNet;

namespace ThermostatController
{
    // Native DHT22 interop interface - UPDATED for threaded polling
    internal static class NativeDht22
    {
        [DllImport("libdht22.so", EntryPoint = "dht22_init", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Init();

        [DllImport("libdht22.so", EntryPoint = "dht22_terminate", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Terminate();

        // New function: Starts the background thread to continuously poll the sensor.
        [DllImport("libdht22.so", EntryPoint = "dht22_start_polling", CallingConvention = CallingConvention.Cdecl)]
        public static extern int StartPolling(int gpioPin);

        // New function: Gets the last cached reading from the background thread.
        // Returns 0 on success, 1 on no valid data yet.
        [DllImport("libdht22.so", EntryPoint = "dht22_get_last_valid_reading", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetLastValidReading(out float temperatureC, out float humidity);
    }

    public interface IHardwareInterface : IDisposable
    {
        void Initialize();
        void SetRelayByte(byte config);
        void SetRelayBytes(byte[] config);  // Support for multiple bytes
        (double? temp, double? humidity) ReadSensor();
        void Cleanup();
    }

    public interface IHardwareFactory
    {
        IHardwareInterface Create();
    }

    public class HardwareFactory : IHardwareFactory
    {
        private readonly ThermostatConfig _config;
        private readonly ILogger<HardwareFactory> _logger;

        public HardwareFactory(ThermostatConfig config, ILogger<HardwareFactory> logger)
        {
            _config = config;
            _logger = logger;
        }

        public IHardwareInterface Create()
        {
            bool needsRelay = _config.NeedsThermostatControl();

            var probes = new List<(string name, Func<IHardwareInterface?>)>
            {
                ("Windows COM", TryWindowsCom),
                ("Linux FTDI BitBang", TryLinuxFtdi),  // Try FTDI first!
                ("Linux COM", TryLinuxCom),
                ("Linux GPIO", TryLinuxGpio),
                ("Windows IoT GPIO", TryWindowsGpio)
            };

            foreach (var (name, factory) in probes)
            {
                if (_config.Mode != HardwareMode.Auto &&
                    !name.Contains(_config.Mode.ToString(), StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var hw = factory();
                    if (hw != null)
                    {
                        _logger.LogInformation("Hardware selected: {Name} (Relay control: {Relay})",
                            name, needsRelay ? "enabled" : "disabled");
                        return hw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hardware probe failed: {Name}", name);
                }
            }

            throw new InvalidOperationException("No compatible hardware found.");
        }

        private IHardwareInterface? TryWindowsCom() => TryCom(true);
        private IHardwareInterface? TryLinuxCom() => TryCom(false);

        private IHardwareInterface? TryLinuxFtdi()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
            if (!_config.NeedsThermostatControl()) return null;  // FTDI is only for relays

            if (!_config.EnableFtdiRelay) 
            {
                _logger.LogDebug("FTDI relay mode not enabled in config");
                return null;
            }

            if (string.IsNullOrEmpty(_config.FtdiSerialNumber))
            {
                _logger.LogWarning("FTDI enabled but no serial number configured");
                return null;
            }

            try
            {
                _logger.LogInformation("Attempting FTDI BitBang mode for relay control (Serial: {Serial})", _config.FtdiSerialNumber);

                // We still need sensor hardware (Arduino or GPIO for DHT22)
                IHardwareInterface? sensorHw = null;

                // Try Arduino for sensor first
                string arduinoPort = _config.ArduinoComPort.StartsWith("/dev/") 
                    ? _config.ArduinoComPort 
                    : $"/dev/{_config.ArduinoComPort}";

                if (System.IO.File.Exists(arduinoPort))
                {
                    _logger.LogInformation("Using Arduino at {Port} for sensor", arduinoPort);
                    var comHw = new ComHardware(_config, _logger, arduinoPort, null);
                    comHw.Initialize();
                    sensorHw = comHw;
                }
                else
                {
                    // Fall back to GPIO for sensor
                    _logger.LogInformation("Arduino not found, attempting GPIO for sensor");
                    var gpioHw = new GpioHardware(_config, _logger);
                    gpioHw.Initialize();
                    sensorHw = gpioHw;
                }

                if (sensorHw == null)
                {
                    _logger.LogDebug("No sensor hardware available for FTDI mode");
                    return null;
                }

                // Create FTDI hardware for relay control
                var ftdiHw = new FtdiHardware(_config, _logger, sensorHw, _config.FtdiSerialNumber);
                ftdiHw.Initialize();
                return ftdiHw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to initialize FTDI BitBang hardware");
                return null;
            }
        }

        private IHardwareInterface? TryCom(bool isWindows)
        {
            if (isWindows && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            if (!isWindows && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;

            string portA;
            if (isWindows)
            {
                portA = _config.ArduinoComPort;
                if (!SerialPort.GetPortNames().Contains(portA))
                {
                    _logger.LogDebug("Arduino port {Port} not found on Windows", portA);
                    return null;
                }
            }
            else
            {
                if (_config.ArduinoComPort.StartsWith("/dev/"))
                {
                    portA = _config.ArduinoComPort;
                }
                else
                {
                    portA = $"/dev/{_config.ArduinoComPort}";
                }
                
                if (!File.Exists(portA))
                {
                    _logger.LogInformation("Arduino port {Port} not found on Linux - trying next hardware mode", portA);
                    return null;
                }
                else
                {
                    _logger.LogInformation("Arduino port {Port} found - checking relay port...", portA);
                }
            }

            string? portR = null;
            if (_config.NeedsThermostatControl())
            {
                if (isWindows)
                {
                    portR = _config.RelayComPort;
                    if (!SerialPort.GetPortNames().Contains(portR))
                    {
                        _logger.LogDebug("Relay port {Port} not found on Windows", portR);
                        return null;
                    }
                }
                else
                {
                    if (_config.RelayComPort.StartsWith("/dev/"))
                    {
                        portR = _config.RelayComPort;
                    }
                    else
                    {
                        portR = $"/dev/{_config.RelayComPort}";
                    }
                    
                    if (!File.Exists(portR))
                    {
                        _logger.LogInformation("Relay port {Port} not found on Linux - trying next hardware mode", portR);
                        return null;
                    }
                    else
                    {
                        _logger.LogInformation("Relay port {Port} found - will use COM mode", portR);
                    }
                }
            }

            try
            {
                var hw = new ComHardware(_config, _logger, portA, portR);
                hw.Initialize();
                return hw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to initialize COM hardware");
                return null;
            }
        }

        private string ExtractPortNumber(string port)
        {
            var digits = new string(port.Where(char.IsDigit).ToArray());
            return string.IsNullOrEmpty(digits) ? "0" : digits;
        }

        private IHardwareInterface? TryLinuxGpio() => TryGpio(false);
        private IHardwareInterface? TryWindowsGpio() => TryGpio(true);

        private IHardwareInterface? TryGpio(bool isWindows)
        {
            if (isWindows && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
            if (!isWindows && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;

            if (_config.NeedsThermostatControl() && _config.RelayPins.Count < 4)
            {
                _logger.LogDebug("GPIO requires at least 4 relay pins for thermostat control");
                return null;
            }

            try
            {
                var hw = new GpioHardware(_config, _logger);
                hw.Initialize();
                return hw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to initialize GPIO hardware");
                return null;
            }
        }
    }

    // === COM HARDWARE ===
    public class ComHardware : IHardwareInterface
    {
        private readonly ThermostatConfig _config;
        private readonly ILogger _logger;
        private readonly string _arduinoPort;
        private readonly string? _relayPort;
        private SerialPort? _arduinoSerial;
        private SerialPort? _relaySerial;
        private readonly object _lock = new();
        private bool _disposed = false;

        public ComHardware(ThermostatConfig config, ILogger logger, string arduinoPort, string? relayPort)
        {
            _config = config;
            _logger = logger;
            _arduinoPort = arduinoPort;
            _relayPort = relayPort;
        }

        public void Initialize()
        {
            lock (_lock)
            {
                _logger.LogInformation("Opening Arduino port {Port}", _arduinoPort);
                try
                {
                    _arduinoSerial = new SerialPort(_arduinoPort, _config.BaudRate)
                    {
                        ReadTimeout = _config.ComTimeout,
                        WriteTimeout = _config.ComTimeout,
                        NewLine = "\n",
                        DtrEnable = true,
                        RtsEnable = true
                    };
                    _arduinoSerial.Open();
                    Thread.Sleep(2000);
                    _arduinoSerial.DiscardInBuffer();
                    _arduinoSerial.DiscardOutBuffer();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open Arduino port {Port}", _arduinoPort);
                    throw;
                }

                if (_config.NeedsThermostatControl() && !string.IsNullOrEmpty(_relayPort))
                {
                    _logger.LogInformation("Opening Relay port {Port}", _relayPort);
                    try
                    {
                        _relaySerial = new SerialPort(_relayPort, _config.BaudRate)
                        {
                            WriteTimeout = _config.ComTimeout,
                            Parity = Parity.None,
                            DataBits = 8,
                            StopBits = StopBits.One,
                            DtrEnable = false,
                            RtsEnable = false
                        };
                        _relaySerial.Open();
                        Thread.Sleep(100);
                        SetRelayByte(0x00);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to open Relay port {Port}", _relayPort);
                        _arduinoSerial?.Dispose();
                        throw;
                    }
                }
            }
        }

        public void SetRelayByte(byte config)
        {
            if (!_config.NeedsThermostatControl() || _disposed) return;

            lock (_lock)
            {
                if (_relaySerial?.IsOpen != true) return;
                try
                {
                    byte[] cmd = { config };
                    _relaySerial.Write(cmd, 0, 1);
                    _logger.LogDebug("Relay byte set: 0x{Config:X2}", config);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Relay write failed");
                }
            }
        }

        public void SetRelayBytes(byte[] config)
        {
            if (!_config.NeedsThermostatControl() || _disposed || config == null || config.Length == 0) return;
            lock (_lock)
            {
                if (_relaySerial?.IsOpen != true) return;
                try
                {
                    _relaySerial.Write(config, 0, config.Length);
                    var hexString = string.Join(" ", config.Select(b => $"0x{b:X2}"));
                    _logger.LogDebug("Relay bytes set: {Bytes}", hexString);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Relay write failed");
                }
            }
        }

        public (double? temp, double? humidity) ReadSensor()
        {
            if (_disposed) return (null, null);
            lock (_lock)
            {
                try
                {
                    if (_arduinoSerial?.IsOpen != true)
                    {
                        _logger.LogWarning("Arduino port not open");
                        return (null, null);
                    }

                    _arduinoSerial.DiscardInBuffer();
                    _arduinoSerial.DiscardOutBuffer();
                    _arduinoSerial.Write("R");

                    var line = _arduinoSerial.ReadLine().Trim();

                    if (!line.Contains("T:") || !line.Contains("H:"))
                    {
                        _logger.LogWarning("Invalid sensor response: {Line}", line);
                        return (null, null);
                    }

                    var parts = line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length != 2) return (null, null);

                    if (double.TryParse(parts[0].Substring(2), out var temp) &&
                        double.TryParse(parts[1].Substring(2), out var hum))
                    {
                        _logger.LogDebug("Sensor read: {Temp}°F, {Hum}%", temp, hum);
                        return (temp, hum);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sensor read failed");
                }
            }
            return (null, null);
        }

        public void Cleanup() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            lock (_lock)
            {
                _disposed = true;
                try { SetRelayByte(0x00); } catch { }
                try { _arduinoSerial?.Close(); _arduinoSerial?.Dispose(); } catch { }
                try { _relaySerial?.Close(); _relaySerial?.Dispose(); } catch { }
            }
        }
    }

    // === GPIO HARDWARE ===
    public class GpioHardware : IHardwareInterface
    {
        private readonly ThermostatConfig _config;
        private readonly ILogger _logger;
        private GpioController? _controller;
        private bool _disposed = false;
        private bool _nativeInitialized = false;

        public GpioHardware(ThermostatConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;
        }

        public void Initialize()
        {
            _controller = new GpioController();

            if (_config.NeedsThermostatControl())
            {
                _logger.LogInformation("Initializing GPIO relay pins: {Pins}", string.Join(", ", _config.RelayPins.Take(4)));
                foreach (var pin in _config.RelayPins.Take(4))
                {
                    _controller.OpenPin(pin, PinMode.Output);
                    _controller.Write(pin, PinValue.High);
                }
            }

            _logger.LogInformation("Initializing native DHT22 library...");
            if (NativeDht22.Init() != 0)
            {
                _logger.LogError("Failed to initialize native DHT22 (pigpio)");
                throw new InvalidOperationException("Failed to initialize pigpio library via native DHT22 interop.");
            }

            if (NativeDht22.StartPolling(_config.DhtSensorPin) != 0)
            {
                 _logger.LogError("Failed to start DHT22 polling thread on pin {Pin}", _config.DhtSensorPin);
                 throw new InvalidOperationException($"Failed to start DHT22 polling thread on pin {_config.DhtSensorPin}.");
            }
            
            _nativeInitialized = true;
            _logger.LogInformation("Native DHT22 polling started successfully on pin {Pin}", _config.DhtSensorPin);
        }

        public void SetRelayByte(byte config)
        {
            if (!_config.NeedsThermostatControl() || _controller == null || _disposed) return;

            for (int i = 0; i < 4 && i < _config.RelayPins.Count; i++)
            {
                bool on = (config & (1 << i)) != 0;
                _controller.Write(_config.RelayPins[i], on ? PinValue.Low : PinValue.High);
            }
            _logger.LogDebug("GPIO relay byte set: 0x{Config:X2}", config);
        }

        public void SetRelayBytes(byte[] config)
        {
            if (!_config.NeedsThermostatControl() || _disposed || _controller == null || config == null || config.Length == 0)
                return;
            for (int byteIdx = 0; byteIdx < config.Length && byteIdx < _config.RelayPins.Count / 8 + 1; byteIdx++)
            {
                byte currentByte = config[byteIdx];
                int pinOffset = byteIdx * 8;
                for (int bit = 0; bit < 8 && (pinOffset + bit) < _config.RelayPins.Count; bit++)
                {
                    bool on = (currentByte & (1 << bit)) != 0;
                    _controller.Write(_config.RelayPins[pinOffset + bit], on ? PinValue.Low : PinValue.High);
                }
            }
        }

        public (double? temp, double? humidity) ReadSensor()
        {
            if (_disposed || !_nativeInitialized) return (null, null);

            float tempC, hum;
            int result = NativeDht22.GetLastValidReading(out tempC, out hum);

            if (result == 0)
            {
                double temp = _config.TemperatureUnit == "F" ? (tempC * 9.0 / 5.0 + 32.0) : tempC;
                _logger.LogDebug("Native DHT22 read: {Temp}°{Unit}, {Hum}%", temp, _config.TemperatureUnit, hum);
                return (temp, hum);
            }
            else
            {
                _logger.LogWarning("Native DHT22 read failed (code {Code}) - No valid data in cache yet.", result);
                return (null, null);
            }
        }

        public void Cleanup() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SetRelayByte(0x00); } catch { }
            try { NativeDht22.Terminate(); } catch { }
            try { _controller?.Dispose(); } catch { }
        }
    }
}