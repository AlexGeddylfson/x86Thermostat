// Config.cs - Configuration and enums
using System.Collections.Generic;
using System.Linq;

namespace ThermostatController
{
    public enum DeploymentType { Thermostat, Probe, Server, HybridProbe, HybridThermo }
    public enum HardwareMode { Auto, Windows, Linux }

    public class DatabaseConfig { } // placeholder for compatibility

    public class RelayCommand
    {
        // Support multiple formats:
        // - Single byte: "0x0F" or 15
        // - Multiple bytes: "0x0F,0x00" or [15, 0]
        // - Binary string: "0b00001111"
        // - Decimal array: [15, 0, 255]
        public object Command { get; set; } = 0x00;

        // Parse the command into byte array
        public byte[] ToBytes()
        {
            if (Command == null)
                return new byte[] { 0x00 };

            // Handle string formats
            if (Command is string str)
            {
                str = str.Trim();

                // Binary format: "0b00001111"
                if (str.StartsWith("0b"))
                {
                    var binary = str.Substring(2);
                    return new byte[] { System.Convert.ToByte(binary, 2) };
                }

                // Hex format with comma separation: "0x0F,0x00"
                if (str.Contains(","))
                {
                    return str.Split(',')
                        .Select(s => s.Trim())
                        .Select(s => s.StartsWith("0x") 
                            ? System.Convert.ToByte(s.Substring(2), 16)
                            : byte.Parse(s))
                        .ToArray();
                }

                // Single hex format: "0x0F"
                if (str.StartsWith("0x"))
                {
                    return new byte[] { System.Convert.ToByte(str.Substring(2), 16) };
                }

                // Plain decimal string: "15"
                return new byte[] { byte.Parse(str) };
            }

            // Handle numeric types
            if (Command is int intVal)
                return new byte[] { (byte)intVal };

            if (Command is long longVal)
                return new byte[] { (byte)longVal };

            // Handle JsonElement (from JSON deserialization)
            if (Command is System.Text.Json.JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    return new byte[] { (byte)jsonElement.GetInt32() };
                }

                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var cmd = new RelayCommand { Command = jsonElement.GetString() ?? "0x00" };
                    return cmd.ToBytes();
                }

                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    return jsonElement.EnumerateArray()
                        .Select(e => (byte)e.GetInt32())
                        .ToArray();
                }
            }

            // Handle arrays/lists
            if (Command is System.Collections.IEnumerable enumerable)
            {
                var bytes = new List<byte>();
                foreach (var item in enumerable)
                {
                    if (item is int i)
                        bytes.Add((byte)i);
                    else if (item is byte b)
                        bytes.Add(b);
                }
                if (bytes.Count > 0)
                    return bytes.ToArray();
            }

            return new byte[] { 0x00 };
        }
    }

    public class RelayCommands
    {
        // Support flexible command formats
        // Examples:
        //   "Off": "0x00"           - Single hex byte
        //   "Off": 0                - Decimal number
        //   "Off": "0b00000000"     - Binary string
        //   "Cool": "0x0C,0x00"     - Multiple hex bytes
        //   "Cool": [12, 0]         - Array of decimals
        public RelayCommand Off { get; set; } = new() { Command = "0x00" };
        public RelayCommand FanOnly { get; set; } = new() { Command = "0x08" };
        public RelayCommand Cool { get; set; } = new() { Command = "0x0C" };
        public RelayCommand Heat { get; set; } = new() { Command = "0x0D" };
        public RelayCommand Emergency { get; set; } = new() { Command = "0x0F" };
    }

    public class ThermostatConfig
    {
        public DeploymentType DeploymentType { get; set; } = DeploymentType.HybridThermo;
        public DatabaseConfig DatabaseConfig { get; set; } = new();
        public string VmServer { get; set; } = "http://localhost:5000";
        public string DeviceId { get; set; } = "ThermostatTest";
        public HardwareMode Mode { get; set; } = HardwareMode.Auto;
        
        // COM Port Configuration
        public string ArduinoComPort { get; set; } = "COM3";
        public string RelayComPort { get; set; } = "COM4";
        public string LinuxComPrefix { get; set; } = "ttyUSB";
        public int BaudRate { get; set; } = 9600;
        public int ComTimeout { get; set; } = 2000;
        
        // FTDI Configuration
        public bool EnableFtdiRelay { get; set; } = false;  // Must be explicitly enabled
        public string FtdiSerialNumber { get; set; } = "A1002zEM";  // Your FTDI device serial
        
        // GPIO Pin Configuration (for Linux/Raspberry Pi)
        public List<int> RelayPins { get; set; } = new() { 17, 27, 22, 23 };
        public int DhtSensorPin { get; set; } = 4;
        
        // Relay Command Configuration - supports multiple formats
        public RelayCommands RelayCommands { get; set; } = new();
        
        // Temperature Settings
        public string TemperatureUnit { get; set; } = "F";
        public double CoolingSetTemperatureOffset { get; set; } = 0.5;
        public double HeatingSetTemperatureOffset { get; set; } = 0.5;
        public double TemperatureDifferenceThreshold { get; set; } = 1.3;
        
        // Timing Configuration
        public int InitialOffDurationSeconds { get; set; } = 100;
        public int EmergencyHeatDelaySeconds { get; set; } = 1800;
        public int CompressorMinOffMinutes { get; set; } = 3;
        public int SensorPollIntervalSeconds { get; set; } = 10;
        public int DataSendIntervalSeconds { get; set; } = 120;
        public int ControlLoopIntervalMs { get; set; } = 10000;
        
        // Emergency Heat Configuration
        // Minimum temperature rise (in degrees F) expected per 10 minutes of heating
        // If heat pump doesn't meet this rate, emergency heat will be activated
        public double MinimumHeatingRatePer10Min { get; set; } = 0.3;
        
        // Network & API
        public int HttpRetryCount { get; set; } = 3;
        public int SensorFailureThreshold { get; set; } = 5;
        public string ApiHost { get; set; } = "0.0.0.0";
        public int ApiPort { get; set; } = 5001;
        public double DefaultUserSetTemperature { get; set; } = 65.0;

        public bool NeedsSensorReading() => DeploymentType != DeploymentType.Server;
        public bool NeedsThermostatControl() => DeploymentType == DeploymentType.Thermostat || DeploymentType == DeploymentType.HybridThermo;
        public bool NeedsServerComponent() => DeploymentType == DeploymentType.Server || DeploymentType == DeploymentType.HybridProbe || DeploymentType == DeploymentType.HybridThermo;
        public bool IsHybridMode() => DeploymentType == DeploymentType.HybridProbe || DeploymentType == DeploymentType.HybridThermo;
    }
}
