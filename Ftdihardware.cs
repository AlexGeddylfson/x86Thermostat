// FtdiHardware.cs - FTDI BitBang mode relay control
using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ThermostatController
{
    // Native libftdi bindings
    internal static class LibFtdi
    {
        private const string LibName = "libftdi1.so.2";

        [DllImport(LibName, EntryPoint = "ftdi_new")]
        public static extern IntPtr New();

        [DllImport(LibName, EntryPoint = "ftdi_free")]
        public static extern void Free(IntPtr ftdi);

        [DllImport(LibName, EntryPoint = "ftdi_usb_open_desc")]
        public static extern int UsbOpenDesc(IntPtr ftdi, int vendor, int product, string description, string serial);

        [DllImport(LibName, EntryPoint = "ftdi_usb_close")]
        public static extern int UsbClose(IntPtr ftdi);

        [DllImport(LibName, EntryPoint = "ftdi_set_bitmode")]
        public static extern int SetBitmode(IntPtr ftdi, byte bitmask, byte mode);

        [DllImport(LibName, EntryPoint = "ftdi_write_data")]
        public static extern int WriteData(IntPtr ftdi, byte[] buf, int size);

        [DllImport(LibName, EntryPoint = "ftdi_get_error_string")]
        public static extern IntPtr GetErrorString(IntPtr ftdi);

        // Constants
        public const byte BITMODE_BITBANG = 0x01;
        public const int VENDOR_ID = 0x0403;  // FTDI
        public const int PRODUCT_ID = 0x6001; // FT232R / FT245R
    }

    public class FtdiHardware : IHardwareInterface
    {
        private readonly ThermostatConfig _config;
        private readonly ILogger _logger;
        private readonly IHardwareInterface _sensorHardware;
        private IntPtr _ftdiContext = IntPtr.Zero;
        private bool _disposed = false;
        private readonly object _lock = new();
        private string? _serialNumber;

        public FtdiHardware(ThermostatConfig config, ILogger logger, IHardwareInterface sensorHardware, string serialNumber)
        {
            _config = config;
            _logger = logger;
            _sensorHardware = sensorHardware;
            _serialNumber = serialNumber;
        }

        public void Initialize()
        {
            _logger.LogInformation("Initializing FTDI BitBang relay controller...");

            // Create FTDI context
            _ftdiContext = LibFtdi.New();
            if (_ftdiContext == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create FTDI context");
            }

            // Open FTDI device by serial number
            int result = LibFtdi.UsbOpenDesc(_ftdiContext, 
                LibFtdi.VENDOR_ID, 
                LibFtdi.PRODUCT_ID, 
                null,  // description (null = any)
                _serialNumber);  // Your serial: A1002zEM

            if (result < 0)
            {
                var errorPtr = LibFtdi.GetErrorString(_ftdiContext);
                var errorMsg = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
                throw new InvalidOperationException($"Failed to open FTDI device: {errorMsg}");
            }

            _logger.LogInformation("FTDI device opened (Serial: {Serial})", _serialNumber);

            // Set BitBang mode - all 8 pins as outputs
            result = LibFtdi.SetBitmode(_ftdiContext, 0xFF, LibFtdi.BITMODE_BITBANG);
            if (result < 0)
            {
                var errorPtr = LibFtdi.GetErrorString(_ftdiContext);
                var errorMsg = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
                throw new InvalidOperationException($"Failed to set BitBang mode: {errorMsg}");
            }

            _logger.LogInformation("FTDI configured for BitBang mode (8 output pins)");

            // Initialize sensor hardware (Arduino for DHT22)
            _sensorHardware.Initialize();

            // Set initial state to all off
            SetRelayByte(0x00);
            _logger.LogInformation("FTDI relay controller initialized successfully");
        }

        public void SetRelayByte(byte config)
        {
            if (_disposed || _ftdiContext == IntPtr.Zero) return;

            lock (_lock)
            {
                try
                {
                    byte[] data = { config };
                    int written = LibFtdi.WriteData(_ftdiContext, data, 1);

                    if (written < 0)
                    {
                        var errorPtr = LibFtdi.GetErrorString(_ftdiContext);
                        var errorMsg = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
                        _logger.LogError("FTDI write failed: {Error}", errorMsg);
                    }
                    else
                    {
                        _logger.LogDebug("FTDI relay byte set: 0x{Config:X2}", config);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FTDI write error");
                }
            }
        }

        public void SetRelayBytes(byte[] config)
        {
            if (_disposed || _ftdiContext == IntPtr.Zero || config == null || config.Length == 0) return;

            lock (_lock)
            {
                try
                {
                    // For FTDI BitBang, we only use the first byte
                    // (8 relays maximum on FT245R)
                    byte value = config[0];
                    
                    int written = LibFtdi.WriteData(_ftdiContext, new byte[] { value }, 1);

                    if (written < 0)
                    {
                        var errorPtr = LibFtdi.GetErrorString(_ftdiContext);
                        var errorMsg = Marshal.PtrToStringAnsi(errorPtr) ?? "Unknown error";
                        _logger.LogError("FTDI write failed: {Error}", errorMsg);
                    }
                    else
                    {
                        _logger.LogDebug("FTDI relay byte set: 0x{Value:X2}", value);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FTDI write error");
                }
            }
        }

        public (double? temp, double? humidity) ReadSensor()
        {
            // Delegate to sensor hardware (Arduino/GPIO)
            return _sensorHardware.ReadSensor();
        }

        public void Cleanup() => Dispose();

        public void Dispose()
        {
            if (_disposed) return;

            lock (_lock)
            {
                _disposed = true;

                try
                {
                    // Turn off all relays
                    SetRelayByte(0x00);
                }
                catch { }

                // Close FTDI device
                if (_ftdiContext != IntPtr.Zero)
                {
                    try
                    {
                        LibFtdi.UsbClose(_ftdiContext);
                        LibFtdi.Free(_ftdiContext);
                        _ftdiContext = IntPtr.Zero;
                        _logger.LogInformation("FTDI device closed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error closing FTDI device");
                    }
                }

                // Clean up sensor hardware
                try
                {
                    _sensorHardware?.Cleanup();
                    _sensorHardware?.Dispose();
                }
                catch { }
            }
        }
    }
}