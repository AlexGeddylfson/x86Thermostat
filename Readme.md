# Smart Thermostat Controller

A flexible, multi-mode thermostat controller for heat pump systems with emergency heat support. Built with .NET 9, it runs on Windows, Linux, and Raspberry Pi with support for multiple hardware configurations.

## Features

- **Multi-Mode Operation**: Server, Thermostat, Probe, or Hybrid deployment modes
- **Heat Pump Control**: Intelligent heat pump operation with automatic emergency heat activation
- **Multiple Hardware Backends**: 
  - Arduino-based sensors (DHT22 via serial)
  - Raspberry Pi GPIO (native DHT22 reading)
  - Serial relay control
  - FTDI BitBang relay control
  - Windows/Linux COM port support
- **Web API**: REST API for temperature monitoring and control
- **Smart Emergency Heat**: Automatically activates emergency heat when heat pump performance drops below threshold
- **Flexible Configuration**: JSON-based configuration with comprehensive validation
- **Database Support**: SQLite
## Mobile App

Control your thermostat from anywhere with the companion mobile app:
- **[Thermostat Mobile App](https://github.com/AlexGeddylfson/Thermostat-App)** - Flutter app available for Android, Windows, Linux, and Web

## Deployment Modes

### Server Mode
Central server that manages temperature settings and collects data from multiple probes.

### Thermostat Mode
Standalone thermostat with local control and sensor reading.

### Probe Mode
Remote temperature sensor that reports to a central server.

### Hybrid Modes
- **HybridProbe**: Runs both server and probe functionality
- **HybridThermo**: Runs both server and thermostat functionality

## Hardware Requirements

### Minimum Requirements
- Any platform supporting .NET 9 (Windows, Linux, macOS)
- For sensor operation: DHT22 temperature/humidity sensor
- For relay control: 4-relay module OR FTDI USB-to-Serial adapter

### Supported Hardware Configurations

#### Arduino + Serial Relay
- Arduino (Uno, Nano, etc.) running DHT22 sensor code
- 4-channel relay module controlled via serial port
- Works on Windows and Linux
- **Arduino sketch included**: `arduino_dht22_reader.ino`

#### Raspberry Pi Native
- DHT22 sensor connected to GPIO pin
- 4-channel relay connected to GPIO pins
- Native GPIO control via System.Device.Gpio

#### FTDI BitBang Mode
- FT232R/FT245R USB-to-Serial adapter
- Up to 8 relays controlled via BitBang mode
- Arduino for DHT22 sensor reading (uses `arduino_dht22_reader.ino`)
- Requires libftdi1 on Linux

## Deployment Workflow

For a complete multi-device setup, deploy in this order:

### 1. Set Up the Server First
The server is the central hub that ties everything together. It manages temperature settings and collects data from all devices.

```bash
# On your server machine (can be a Raspberry Pi, PC, or cloud server)
# Download and extract the server release
# Configure as DeploymentType: "Server"
# Run on port 5000 (default)
```

### 2. Deploy Probes and Thermostats
Once the server is running, set up your temperature probes and thermostats. They will connect to the server.

```bash
# On each probe/thermostat device
# Download and extract the release
# Configure with VmServer: "http://your-server-ip:5000"
# Configure as DeploymentType: "Probe" or "Thermostat"
```

### 3. Install the Mobile App
Control everything from your phone, computer, or browser:
- [**[Thermostat Mobile App](https://github.com/AlexGeddylfson/Thermostat-App)**](https://github.com/AlexGeddylfson/x86ThermostatApp) - Flutter app for Android, Windows, Linux, and Web

**Example Configuration Files:**
- `config.server.json` - Example server configuration
- `config.thermostat.json` - Example thermostat configuration
- These are included in the repository for reference

## Installation

### From Pre-compiled Binaries

Download the appropriate release for your platform from [Releases](https://github.com/AlexGeddylfson/x86Thermostat/releases):

#### Raspberry Pi (ARM64)
```bash
wget https://github.com/AlexGeddylfson/x86Thermostat/releases/latest/download/ThermostatController-linux-arm64.tar.gz
tar -xzf ThermostatController-linux-arm64.tar.gz
cd ThermostatController-linux-arm64
```

#### Linux (x64)
```bash
wget https://github.com/AlexGeddylfson/x86Thermostat/releases/latest/download/ThermostatController-linux-x64.tar.gz
tar -xzf ThermostatController-linux-x64.tar.gz
cd ThermostatController-linux-x64
```

#### Windows (x64)
```bash
# Download ThermostatController-win-x64.zip from releases
# Extract and run ThermostatController.exe
```

**Note**: All releases include the same application. Configure deployment mode (Server, Thermostat, Probe, or Hybrid) in `config.json` after installation.

### From Source

#### Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- For Raspberry Pi GPIO: pigpio library
- For FTDI support: libftdi1

#### Clone and Build
```bash
git clone https://github.com/AlexGeddylfson/x86Thermostat.git
cd x86Thermostat
dotnet restore
dotnet build
```

#### Install Dependencies (Raspberry Pi)
```bash
# Install pigpio for GPIO/DHT22
sudo apt-get update
sudo apt-get install -y pigpio libpigpio-dev

# Start pigpio daemon
sudo pigpiod

# Install FTDI library (if using FTDI relay)
sudo apt-get install -y libftdi1-2
```

#### Compile DHT22 Library (Raspberry Pi)
```bash
# Compile the native library
gcc -shared -fPIC -o libdht22.so dht22.c -lpigpio -lpthread

# Install to system library path
sudo cp libdht22.so /usr/local/lib/

# Update library cache
sudo ldconfig
```

**Note**: The library must be in `/usr/local/lib/` for the application to find it at runtime.

## Configuration

On first run, a `config.json` file will be created with default settings. Edit this file to match your hardware setup.

### Example Configurations

#### Standalone Thermostat (Arduino + Serial Relay)
```json
{
  "DeploymentType": "Thermostat",
  "DeviceId": "LivingRoomThermostat",
  "Mode": "Auto",
  "ArduinoComPort": "COM3",
  "RelayComPort": "COM4",
  "BaudRate": 9600,
  "EnableFtdiRelay": false,
  "DefaultUserSetTemperature": 68.0
}
```

#### Raspberry Pi Native GPIO
```json
{
  "DeploymentType": "Thermostat",
  "DeviceId": "RaspberryPiThermostat",
  "Mode": "Linux",
  "LinuxComPrefix": "ttyUSB",
  "RelayPins": [17, 27, 22, 23],
  "DhtSensorPin": 4,
  "EnableFtdiRelay": false
}
```

#### FTDI BitBang Relay Control
```json
{
  "DeploymentType": "Thermostat",
  "DeviceId": "FtdiThermostat",
  "Mode": "Auto",
  "ArduinoComPort": "COM3",
  "EnableFtdiRelay": true,
  "FtdiSerialNumber": "A1002zEM"
}
```

#### Server + Multiple Probes
Server config:
```json
{
  "DeploymentType": "Server",
  "ApiHost": "0.0.0.0",
  "ApiPort": 5000
}
```

Probe config:
```json
{
  "DeploymentType": "Probe",
  "DeviceId": "BedroomProbe",
  "VmServer": "http://192.168.1.100:5000",
  "ArduinoComPort": "/dev/ttyUSB0"
}
```

### Configuration Options

#### Relay Commands
Supports multiple formats for relay control:
```json
"RelayCommands": {
  "Off": "0x00",          // Hex string
  "FanOnly": 8,           // Decimal
  "Cool": "0b00001100",   // Binary string
  "Heat": "0x0D",         // Hex string
  "Emergency": [15, 0]    // Byte array
}
```

#### Temperature Settings
```json
"TemperatureUnit": "F",
"CoolingSetTemperatureOffset": 0.5,
"HeatingSetTemperatureOffset": 0.5,
"TemperatureDifferenceThreshold": 1.3,
"MinimumHeatingRatePer10Min": 0.3
```

#### Timing Configuration
```json
"CompressorMinOffMinutes": 3,
"SensorPollIntervalSeconds": 10,
"DataSendIntervalSeconds": 120,
"ControlLoopIntervalMs": 10000
```

## Running

### Linux/Raspberry Pi
```bash
# Run directly
./ThermostatController

# Or with dotnet
dotnet ThermostatController.dll

# Run as background service
sudo systemctl enable thermostat
sudo systemctl start thermostat
```

### Windows
```cmd
ThermostatController.exe
```

### Systemd Service (Linux)

**Using Published Binary (Production):**
```ini
[Unit]
Description=Thermostat Control and Server
After=network.target pigpiod.service
Requires=pigpiod.service

[Service]
User=root
Group=root
WorkingDirectory=/home/yourusername/thermostat
ExecStartPre=/bin/sleep 10
ExecStart=/home/yourusername/thermostat/ThermostatController
Environment=ASPNETCORE_ENVIRONMENT=Production
Restart=always
RestartSec=5
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

**Using dotnet run (Development):**
```ini
[Unit]
Description=ThermostatController Service
After=network.target

[Service]
WorkingDirectory=/home/yourusername/thermostat-source
ExecStart=/usr/bin/dotnet run --configuration Release
Restart=always
RestartSec=5
User=root
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
```

Enable and start:
```bash
sudo systemctl daemon-reload
sudo systemctl enable thermostat
sudo systemctl start thermostat
```

## API Endpoints

### Server Mode (Port 5000)
- `GET /api/settings` - Get current temperature settings
- `POST /api/settings` - Update temperature settings
- `GET /api/data/{deviceId}` - Get latest sensor data for device
- `POST /api/data` - Submit sensor data (used by probes)

### Thermostat/Probe Mode (Port 5001)
- `GET /api/status` - Get current status
- `GET /api/temperature` - Get current temperature/humidity
- `POST /api/set-temperature` - Set target temperature
- `POST /api/mode/{mode}` - Set HVAC mode (off, cool, heat, auto)

## Hardware Wiring

### Arduino Setup

1. **Upload the sketch**:
   - Open `arduino_dht22_reader.ino` in Arduino IDE
   - Install DHT sensor library: Sketch → Include Library → Manage Libraries → Search "DHT sensor library" by Adafruit
   - Also install "Adafruit Unified Sensor" library (dependency)
   - Select your Arduino board and port
   - Upload the sketch

2. **Verify operation**:
   - Open Serial Monitor (9600 baud)
   - Type 'R' and press Enter
   - Should see: `T:72.5,H:45.0` (temperature in Fahrenheit, humidity in %)

### DHT22 Sensor to Arduino
- VCC → 5V
- GND → GND
- DATA → Digital Pin 2 (configurable in Arduino code)

**Note**: The Arduino sketch converts temperature to Fahrenheit. The C# application expects Fahrenheit values.

### Relay Module
4-channel relay module connected to GPIO pins or controlled via serial/FTDI:
- Relay 1: Fan
- Relay 2: Cooling (Compressor)
- Relay 3: Heating (Heat Pump)
- Relay 4: Emergency Heat

### FTDI Connections (BitBang Mode)
Connect relay module control pins to FTDI D0-D7 pins:
- D0 → Relay 1 (Fan)
- D1 → Relay 2 (Cooling)
- D2 → Relay 3 (Heating)
- D3 → Relay 4 (Emergency Heat)

## Development

### Building from Source
```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Run
dotnet run
```

### Publishing for Distribution
```bash
# Linux ARM64 (Raspberry Pi)
dotnet publish -c Release -r linux-arm64 --self-contained

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained

# Windows x64
dotnet publish -c Release -r win-x64 --self-contained
```

### Cross-Compilation from Linux
See `BUILD.md` for detailed cross-compilation instructions.

## Troubleshooting

### Permission Denied on Linux COM Ports
```bash
sudo usermod -a -G dialout $USER
# Log out and back in
```

### pigpio Daemon Not Running
```bash
sudo pigpiod
# Or enable at startup:
sudo systemctl enable pigpiod
```

### FTDI Device Not Found
```bash
# List FTDI devices
lsusb | grep FTDI

# Check permissions
ls -l /dev/bus/usb/

# Add udev rule for FTDI
echo 'SUBSYSTEM=="usb", ATTR{idVendor}=="0403", MODE="0666"' | sudo tee /etc/udev/rules.d/99-ftdi.rules
sudo udevadm control --reload-rules
```

### Configuration Validation Errors
Run the application - it will validate your config and provide detailed error messages about what needs to be fixed.

## License

MIT License - See LICENSE file for details

## Contributing

Contributions welcome! Please open an issue or submit a pull request.

## Acknowledgments

- Uses [pigpio](http://abyz.me.uk/rpi/pigpio/) for Raspberry Pi GPIO control
- Uses [libftdi](https://www.intra2net.com/en/developer/libftdi/) for FTDI device support
- Built with [.NET 9](https://dotnet.microsoft.com/)
