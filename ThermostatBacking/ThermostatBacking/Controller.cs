using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

public class ThermostatController
{

    public void Start()
    {

        Console.WriteLine("Thermostat Controller started.");
    }

    private const double V = 0.5;
    private const double C = 1.3;
    private int current_state = 0;
    private bool emergency_stop_enabled = false;
    private int off_between_states_counter = 0;
    private int heat_mode_timer = 0;
    private int off_mode_counter = 0;
    private readonly int off_mode_duration = 10;
    private bool fanModeEnabled = false;
    private float heating_set_temperature_offset;
    private float temperature_difference_threshold = (float)C;
    private float coolingSetTemperatureOffset = (float)V;
    private float heatingSetTemperatureOffset = (float)V;
    private string device_id;
    private string server_url;
    private SerialPort serialPort;

    private static readonly HttpClient client = new HttpClient();

    private float? userSetTemperature = null;
    private DateTime userSetTemperatureLastUpdated;
    private bool fetchedUserSetting = false;
    private Polling pollingInstance;
    public static bool IsThermostatControllerActive { get; private set; }
    // Field to store fan mode state

    private static ThermostatController _instance;
    public static ThermostatController Instance
    {
        get
        {
            if (_instance == null)
                _instance = new ThermostatController();
            return _instance;
        }
    }

    // Property to get and set fan mode with additional logic
    public bool FanModeEnabled
    {
        get => fanModeEnabled;
        set
        {
            fanModeEnabled = value;
            Console.WriteLine($"Fan mode has been {(value ? "enabled" : "disabled")}.");
        }
    }

    public ThermostatController()
    {
        // Load configuration from appsettings.json
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .Build();

        device_id = configuration["AppSettings:DeviceId"];
        server_url = configuration["AppSettings:ServerUrl"];
        string serialPortName = configuration["AppSettings:SerialPort"];
        int baudRate = int.Parse(configuration["AppSettings:BaudRate"]);
        IsThermostatControllerActive = bool.Parse(configuration["AppSettings:IsThermostatControllerActive"]);

        Console.WriteLine($"Configuration loaded: Device ID - {device_id}, Server URL - {server_url}");

        serialPort = new SerialPort(serialPortName, baudRate, Parity.None, 8, StopBits.One);
        try
        {
            serialPort.Open();
            Console.WriteLine($"Serial port {serialPortName} opened successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening serial port: {ex.Message}");
        }

        // Only start the controller operations if the flag is true
        if (IsThermostatControllerActive)
        {
            Console.WriteLine("ThermostatController is active.");
            Task.Run(() => UpdateThermostatState());
        }
        else
        {
            Console.WriteLine("ThermostatController is not active based on configuration.");
        }
    }
    private int currentStateCode = 0;  // Default to 0 which can be 'Off' state
    public string CurrentState => thermostatStates[currentStateCode];
    public void EnableEmergencyStop()
    {
        emergency_stop_enabled = true;
        Console.WriteLine("Emergency stop has been enabled.");
    }

    public void DisableEmergencyStop()
    {
        emergency_stop_enabled = false;
        Task.Run(() => UpdateThermostatState());
        Console.WriteLine("Emergency stop has been disabled.");
    }

    public bool IsEmergencyStopEnabled()
    {
        return emergency_stop_enabled;
    }
    private Dictionary<int, string> thermostatStates = new Dictionary<int, string>
    {
        {0, "Off"},
        {1, "Heating"},
        {2, "Cooling"},
        {3, "Emergency Heat"},
        {4, "Between States"},
        {5, "Emergency Off"},
        {6, "Fan Only"}
        // Add other states as needed
    };

    public string GetCurrentStateDescription()
    {
        // This method returns the current state's description or "Unknown" if not found
        return thermostatStates.TryGetValue(current_state, out var state) ? state : "Unknown";
    }
    public void UpdateUserSetTemperature(float newTemp)
    {
        Console.WriteLine($"Received new temperature from frontend: {newTemp}");
        userSetTemperature = newTemp;
        userSetTemperatureLastUpdated = DateTime.Now;
    }
    public async Task<float> GetUserSettingAsync(int maxRetries = 3, int retryDelay = 1000, float defaultValue = 68)
    {
        if (userSetTemperature.HasValue)
        {
            return userSetTemperature.Value; // Return cached value if available
        }

        int retries = 0;
        while (retries < maxRetries)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync($"{server_url}/api/get_last_user_setting");
                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    JObject data = JObject.Parse(responseBody);
                    var userSetting = data["last_user_setting"]?.Value<string>();
                    if (userSetting != null)
                    {
                        userSetTemperature = float.Parse(userSetting);
                        userSetTemperatureLastUpdated = DateTime.Now;
                        fetchedUserSetting = true; // Indicates that the setting has been fetched
                        return userSetTemperature.Value;
                    }
                    else
                    {
                        Console.WriteLine("User setting not found in response.");
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to fetch user setting: {response.StatusCode}");
                }
            }
            catch (Exception e) // Catch more generic Exception to handle all types of exceptions
            {
                Console.WriteLine($"Error fetching user setting: {e.Message}");
            }

            retries++;
            await Task.Delay(retryDelay); // Wait before retrying
        }

        Console.WriteLine("Max retries reached. Returning default value.");
        return defaultValue; // Return default if all retries fail
    }
    public ThermostatController(Polling pollingInstance)
    {
        this.pollingInstance = pollingInstance;
    }

    // Asynchronous method to update the thermostat's state based on the current and set temperatures
    public async Task UpdateThermostatState()
    {
        if (this.off_mode_counter < this.off_mode_duration)
        {
            this.InitialOffMode();
            this.off_mode_counter += 1;
        }
        else
        {
            var currentTemperature = await Polling.GetCurrentTemperatureAsync();
            if (!float.IsNaN(currentTemperature))
            {
                float targetTemperature = await GetUserSettingAsync(); // Fetch and use the latest setting

                Console.WriteLine($"Current temperature: {currentTemperature}°F");
                Console.WriteLine($"Target temperature: {targetTemperature}°F");
                var temperatureDifference = Math.Abs(currentTemperature - targetTemperature);
                Console.WriteLine($"Temperature difference: {temperatureDifference}°F");

                if (this.emergency_stop_enabled)
                {
                    Console.WriteLine("Emergency stop enabled, turning off all operations.");
                    this.OffMode();
                }
                else if (temperatureDifference > this.temperature_difference_threshold)
                {
                    if (currentTemperature < targetTemperature)
                    {
                        Console.WriteLine("Current temperature is below set point, activating heating.");
                        this.HeatMode(targetTemperature);
                    }
                    else if (currentTemperature > targetTemperature)
                    {
                        Console.WriteLine("Current temperature is above set point, activating cooling.");
                        this.CoolMode(targetTemperature);
                    }
                }
                else
                {
                    Console.WriteLine("Temperature is within the acceptable range, maintaining current state.");
                    this.OffBetweenStatesMode();
                }
            }
            else
            {
                Console.WriteLine("Error reading sensor data. Retrying...");
                await Task.Delay(5000);
            }
        }
    }

    private void SetRelayStates(SerialPort port, byte relayConfiguration)
    {
        if (port != null && port.IsOpen)
        {
            byte[] command = new byte[] { relayConfiguration };
            port.Write(command, 0, command.Length);
            Console.WriteLine($"Sent command {relayConfiguration:X2} to set relay states.");
        }
        else
        {
            Console.WriteLine("Serial port not open or not available.");
        }
    }

    public void SetMode(SerialPort port, string mode)
    {
        byte config;
        switch (mode.ToLower())
        {
            case "heat":
                config = 0xD;  // 1101
                break;
            case "emergency heat":
                config = 0xF;  // 1111
                break;
            case "fan only":
                config = 0x8;  // 1000
                break;
            case "cool":
                config = 0xC;  // 1100
                break;
            default:
                Console.WriteLine($"Unknown mode: {mode}");
                return;
        }

        SetRelayStates(port, config);
        Console.WriteLine($"Mode set to {mode}");
    }

    public async Task InitialOffMode()
    {
        byte offAll = 0x0;  // 0000 all off
        this.SetRelayStates(serialPort, offAll);
        Console.WriteLine("Off mode activated, all relays turned off.");

        this.off_between_states_counter = 0;
        this.off_mode_counter += 10; // Assuming you want to increment this counter for some logic

        Console.WriteLine("Waiting for 10 seconds...");
        await Task.Delay(10000);  // Wait for 10 seconds asynchronously

        Console.WriteLine("Resuming operations...");
        await UpdateThermostatState();  // Call the UpdateThermostatState method to continue with thermostat logic
    }


    public async Task OffMode()
    {
        current_state = 0;
        byte offAll = 0x0;  // 0000 all off
        this.SetRelayStates(serialPort, offAll);
        Console.WriteLine("Off mode activated, all relays turned off.");
        this.off_between_states_counter = 0;
        this.off_mode_counter += 10;
    }

    public async Task HeatMode(float targetTemperature)
    {
        current_state = 1;
        off_between_states_counter = 0;
        Console.WriteLine("Heating mode activated.");
        byte heatConfig = 0xD;  // Example: 1101
        this.SetRelayStates(serialPort, heatConfig);

        try
        {
            while (true)
            {
                var currentTemperature = await Polling.GetCurrentTemperatureAsync();
                if (!float.IsNaN(currentTemperature))

                {
                    float userSetTemperature = await GetUserSettingAsync();
                    Console.WriteLine($"Using offset: +{heatingSetTemperatureOffset}");
                    float heatingSetTemperature = userSetTemperature + heatingSetTemperatureOffset;

                    Console.WriteLine($"Current temperature: {currentTemperature}°F, Set Temperature: {heatingSetTemperature}°F");
                    Console.WriteLine($"Using user set temperature: {userSetTemperature}");

                    await Task.Delay(5000);
                    // Check if emergency stop is enabled before activating cooling
                    if (this.emergency_stop_enabled)
                    {
                        Console.WriteLine("Emergency stop enabled, turning off all operations.");
                        await OffMode();  // Ensure OffMode is async and await its completion
                        return;  // Exit the method to prevent cooling from proceeding
                    }
                    if (currentTemperature >= heatingSetTemperature)
                    {
                        Console.WriteLine("Temperature condition met, switching to off between states mode.");
                        OffBetweenStatesMode();
                        break;
                    }
                }
                else
                {
                    Console.WriteLine("Error reading sensor data. Retrying...");
                    await Task.Delay(10000);  // Wait for 10 seconds before retrying
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred in HeatMode: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("Exiting Heat mode.");

        }
    }
        public async Task CoolMode(float targetTemperature)
    {
        current_state = 2;
        off_between_states_counter = 0;
        Console.WriteLine("Cooling mode activated.");
        byte coolConfig = 0xC;  // Configuration byte for cooling mode
        this.SetRelayStates(serialPort, coolConfig);

        try
        {
            while (true)
            {
                var currentTemperature = await Polling.GetCurrentTemperatureAsync();
                if (!float.IsNaN(currentTemperature))
                    
                {
                    float userSetTemperature = await GetUserSettingAsync();
                    Console.WriteLine($"Using offset: -{coolingSetTemperatureOffset}");
                    float coolingSetTemperature = userSetTemperature - coolingSetTemperatureOffset;

                    Console.WriteLine($"Current temperature: {currentTemperature}°F, Set Temperature: {coolingSetTemperature}°F");
                    Console.WriteLine($"Using user set temperature: {userSetTemperature}");

                    await Task.Delay(5000);
                    // Check if emergency stop is enabled before activating cooling
                    if (this.emergency_stop_enabled)
                    {
                        Console.WriteLine("Emergency stop enabled, turning off all operations.");
                        await OffMode();  // Ensure OffMode is async and await its completion
                        return;  // Exit the method to prevent cooling from proceeding
                    }

                    if (currentTemperature <= coolingSetTemperature)
                    {
                        Console.WriteLine("Temperature condition met, switching to off between states mode.");
                        OffBetweenStatesMode();
                        break;
                    }
                }
                else
                {
                    Console.WriteLine("Error reading sensor data. Retrying...");
                    await Task.Delay(10000);  // Wait for 10 seconds before retrying
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred in CoolMode: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("Exiting Cooling Mode.");
        }
    }

    public async Task OffBetweenStatesMode()
    {
        int waitTime = 20;  // Default time to wait in seconds

        if (FanModeEnabled)
        {
            current_state = 6; // Fan Only state
            byte fanOnlyConfig = 0x8;  // Configuration byte for fan only mode
            this.SetRelayStates(serialPort, fanOnlyConfig);
            Console.WriteLine("Fan Only mode activated.");
        }
        else
        {
            current_state = 4; // Between States
            byte idleConfig = 0x0; // All relays off
            this.SetRelayStates(serialPort, idleConfig);
            Console.WriteLine("System is idling between active modes with all relays off.");
        }

        switch (off_between_states_counter)
        {
            case 0:
                waitTime = 10;  // Wait for 480 seconds (8 minutes)
                break;
            case 1:
                waitTime = 5;  // Wait for 240 seconds (4 minutes)
                break;
            case 2:
                waitTime = 5;   // Wait for 20 seconds
                break;
            default:
                Console.WriteLine("Continuing with the default idling time.");
                break;  // Keep waitTime at 20 seconds as set initially
        }

        await Task.Delay(waitTime * 1000);  // Convert seconds to milliseconds
        off_between_states_counter++;  // Increment the counter for the next state

        Console.WriteLine($"Resuming operations after waiting {waitTime} seconds.");
        this.UpdateThermostatState();  // Call UpdateThermostatState to proceed with the next actions
    }

}