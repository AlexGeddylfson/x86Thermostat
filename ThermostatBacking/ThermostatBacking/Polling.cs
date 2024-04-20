using System;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Polling
{
    static SerialPort serialPort;
    static string server_url;
    static string device_id;
    static HttpClient httpClient = new HttpClient();
    static float currentTemperature = 0;
    static float currentHumidity = 0;
    public static bool IsThermostatControllerActive { get; private set; }
    public static bool IsServerActive { get; private set; }
    public static event Action OnTemperatureReady;
    static async Task Main(string[] args)
    {
        // Load configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .Build();

        // Get settings from configuration
        device_id = configuration["AppSettings:DeviceId"];
        Console.WriteLine($"Device ID: {device_id}");
        server_url = configuration["AppSettings:ServerUrl"];
        string comPort = configuration["AppSettings:ComPort"];
        int baudRate = int.Parse(configuration["AppSettings:BaudRate"]);
        IsThermostatControllerActive = bool.Parse(configuration["AppSettings:IsThermostatControllerActive"]);
        IsServerActive = bool.Parse(configuration["AppSettings:IsServerActive"]);

        // Open the serial port
        serialPort = new SerialPort(comPort, baudRate);
        serialPort.Open();
        Console.WriteLine($"Serial port {comPort} opened successfully.");

        // Create a timer to poll the Arduino every 10 seconds
        System.Timers.Timer pollTimer = new System.Timers.Timer();
        pollTimer.Interval = 10000; // 10 seconds
        pollTimer.Elapsed += async (sender, e) => await PollArduino();
        pollTimer.AutoReset = true;
        pollTimer.Start();

        // Create a timer to send data to the server every 2 minutes
        System.Timers.Timer sendDataTimer = new System.Timers.Timer();
        sendDataTimer.Interval = 120000; // 2 minutes
        sendDataTimer.Elapsed += async (sender, e) => await SendDataToServer(currentTemperature, currentHumidity);
        sendDataTimer.AutoReset = true;
        sendDataTimer.Start();

   

        // Initialize ThermostatController
        ThermostatController thermostatController = new();
        thermostatController.Start();

        // Initialize serverController conditionally
        if (IsServerActive)
        {
            ServerController serverController = new ServerController();
            serverController.Start();
            Console.WriteLine("ServerController has been started.");
        }
        else
        {
            Console.WriteLine("ServerController is not active based on configuration.");
        }
    

    float RoundToNearestHalf(float number)
        {
            return (float)(Math.Round(number * 2) / 2.0);
        }
        // Set up the web server
        var host = new WebHostBuilder()
    .UseKestrel(options =>
    {
        // Bind to any IP address on port 5001
        options.ListenAnyIP(5001);
    })
    .Configure(app =>
    {
        app.Run(async context =>
        {
            switch (context.Request.Path)
            {
                case "/api/get_current_temperature":
                    if (context.Request.Method == "GET")
                    {
                        Console.WriteLine("API endpoint requested: /api/get_current_temperature");
                        float roundedTemperature = RoundToNearestHalf(currentTemperature);
                        await context.Response.WriteAsync($"{{\"temperature\": {roundedTemperature}}}");
                    }
                    break;

                case "/api/deviceid":
                    if (context.Request.Method == "POST")
                    {
                        Console.WriteLine("API endpoint requested: /api/deviceid");
                        await HandleDeviceIdUpdate(context);
                    }
                    break;

                case "/api/update_user_set_temperature":
                    if (context.Request.Method == "POST")
                    {
                        try
                        {
                            var requestData = await new StreamReader(context.Request.Body).ReadToEndAsync();
                            var data = JsonConvert.DeserializeObject<JObject>(requestData);
                            float newTemp = data["temperature"].Value<float>();
                            thermostatController.UpdateUserSetTemperature(newTemp);
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { message = "Temperature updated successfully" }));
                        }
                        catch (Exception ex)
                        {
                            context.Response.StatusCode = 500;
                            await context.Response.WriteAsync($"Error: {ex.Message}");
                        }
                    }
                    break;

                case "/api/get_last_user_setting":
                    if (context.Request.Method == "GET")
                    {
                        try
                        {
                            float lastUserSetting = await thermostatController.GetUserSettingAsync();
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { user_set_temperature = lastUserSetting }));
                        }
                        catch (Exception ex)
                        {
                            context.Response.StatusCode = 500;
                            await context.Response.WriteAsync($"Error retrieving user setting: {ex.Message}");
                        }
                    }
                    break;

                case "/api/get_thermostat_state":
                    if (context.Request.Method == "GET")
                    {
                        try
                        {
                            string currentState = thermostatController.GetCurrentStateDescription();
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { state = currentState }));
                        }
                        catch (Exception ex)
                        {
                            context.Response.StatusCode = 500;
                            await context.Response.WriteAsync($"Error retrieving thermostat state: {ex.Message}");
                        }
                    }
                    break;

                case "/api/emergency_stop":
                    if (context.Request.Method == "GET")
                    {
                        bool isEnabled = thermostatController.IsEmergencyStopEnabled();
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { emergency_stop_enabled = isEnabled }));
                    }
                    else if (context.Request.Method == "POST")
                    {
                        var requestData = await new StreamReader(context.Request.Body).ReadToEndAsync();
                        var data = JsonConvert.DeserializeObject<JObject>(requestData);
                        bool enable = data["enable"].Value<bool>();

                        if (enable)
                        {
                            thermostatController.EnableEmergencyStop();
                        }
                        else
                        {
                            thermostatController.DisableEmergencyStop();
                        }

                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { message = "Emergency Stop " + (enable ? "Enabled" : "Disabled") }));
                    }
                    break;

                case "/api/fan_mode":
                    if (context.Request.Method == "POST")
                    {
                        Console.WriteLine("Received POST request at /api/fan_mode");
                        try
                        {
                            var requestData = await new StreamReader(context.Request.Body).ReadToEndAsync();
                            var data = JObject.Parse(requestData);

                            if (data.TryGetValue("enable", out JToken enableValue))
                            {
                                bool enable = enableValue.ToObject<bool>();
                                ThermostatController.Instance.FanModeEnabled = enable;  // Assuming Instance is your accessible ThermostatController
                                Console.WriteLine($"Fan mode set to: {(enable ? "enabled" : "disabled")}");

                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { message = $"Fan mode {(enable ? "enabled" : "disabled")}" }));
                                context.Response.StatusCode = StatusCodes.Status200OK;
                            }
                            else
                            {
                                await context.Response.WriteAsync(JsonConvert.SerializeObject(new { error = "Missing 'enable' parameter" }));
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Exception occurred: {ex.Message}");
                            await context.Response.WriteAsync(JsonConvert.SerializeObject(new { error = "Error processing request" }));
                            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        }
                        context.Response.ContentType = "application/json";
                    }
                    break;

                case "/api/get_fan_mode":
                    if (context.Request.Method == "GET")
                    {
                        Console.WriteLine("API call to retrieve fan mode state.");
                        // Make sure that you have a way to access your controller instance properly. This could be through a singleton or some other means that fits your architecture.
                        bool isEnabled = ThermostatController.Instance.FanModeEnabled; // Assuming `Instance` is a static property that gives you access to the controller.
                        await context.Response.WriteAsync(JsonConvert.SerializeObject(new { fan_mode = isEnabled }));
                        context.Response.ContentType = "application/json"; // Ensure that the response content type is set to application/json.
                        context.Response.StatusCode = StatusCodes.Status200OK; // Explicitly set the HTTP status code to 200 OK.
                        Console.WriteLine("Fan mode state returned: " + isEnabled);
                    }
                    break;


            }

        });
    })
    .Build();

        // Run the web server
        await host.RunAsync();


    }

    static async Task HandleDeviceIdUpdate(HttpContext context)
    {
        try
        {
            // Read JSON payload from the request
            string requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
            JObject json = JObject.Parse(requestBody);
            string newDeviceId = json.Value<string>("device_id");

            // Update device ID in configuration and memory
            device_id = newDeviceId;
            UpdateConfiguration("AppSettings:DeviceId", newDeviceId);

            // Reload configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .Build();
            device_id = configuration["AppSettings:DeviceId"];

            // Send response
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync($"Device ID updated to: {newDeviceId}");
            Console.WriteLine($"Device ID updated to: {newDeviceId}");
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"Error updating device ID: {ex.Message}");
            Console.WriteLine($"Error updating device ID: {ex.Message}");
        }
    }


    static void UpdateConfiguration(string key, string value)
    {
        string appSettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        // Read the existing configuration
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true)
            .Build();

        // Update the configuration in memory
        configuration[key] = value;

        // Write the updated configuration back to the file
        var json = JsonConvert.SerializeObject(configuration.AsEnumerable().ToDictionary(k => k.Key, k => k.Value), Formatting.Indented);
        File.WriteAllText(appSettingsPath, json);

        // No need to reload the configuration as it's modified in memory
    }



    static async Task PollArduino()
    {
        try
        {
            Console.WriteLine("Requesting data from Arduino...");

            // Request data from the Arduino
            serialPort.Write("R");

            // Read the response from the Arduino
            string arduinoData = serialPort.ReadLine().Trim();
            Console.WriteLine($"Received data from Arduino: {arduinoData}");

            // Parse temperature and humidity from the received data
            string[] dataParts = arduinoData.Split(',');
            foreach (string part in dataParts)
            {
                if (part.StartsWith("T:"))
                {
                    currentTemperature = float.Parse(part.Substring(2));
                }
                else if (part.StartsWith("H:"))
                {
                    currentHumidity = float.Parse(part.Substring(2));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error polling Arduino: {ex.Message}");
        }
    }
    // Method to get the current temperature
    public static async Task<float> GetCurrentTemperatureAsync()
    {
        await PollArduino();  // Ensure the latest data is fetched from the Arduino
        return currentTemperature;  // Return the latest temperature
    }
    static async Task SendDataToServer(float temperature, float humidity)
    {
        try
        {
            // Prepare data to send to the server
            string sensorData = $"{{\"device_id\": \"{device_id}\", \"temperature\": {temperature}, \"humidity\": {humidity}}}";
            Console.WriteLine($"Sending data to server: {sensorData}");

            // Send data to the server
            var content = new StringContent(sensorData, Encoding.UTF8, "application/json");
            string fullUrl = $"{server_url}/api/receive_data";
            var response = await httpClient.PostAsync(fullUrl, content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("Data sent successfully.");
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Error sending data to server: {e.Message}");
        }
    }
}