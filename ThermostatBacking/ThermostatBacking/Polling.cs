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

class Polling
{
    static SerialPort serialPort;
    static string api_url;
    static string device_id;
    static HttpClient httpClient = new HttpClient();
    static float currentTemperature = 0;
    static float currentHumidity = 0;

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
        api_url = configuration["AppSettings:ApiUrl"];
        string comPort = configuration["AppSettings:ComPort"];
        int baudRate = int.Parse(configuration["AppSettings:BaudRate"]);

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
                    if (context.Request.Path == "/api/get_current_temperature" && context.Request.Method == "GET")
                    {
                        Console.WriteLine("API endpoint requested: /api/get_current_temperature");
                        await context.Response.WriteAsync($"{{\"temperature\": {currentTemperature}}}");
                    }
                    else if (context.Request.Path == "/api/deviceid" && context.Request.Method == "POST")
                    {
                        Console.WriteLine("API endpoint requested: /api/deviceid");
                        await HandleDeviceIdUpdate(context);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
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

    static async Task SendDataToServer(float temperature, float humidity)
    {
        try
        {
            // Prepare data to send to the server
            string sensorData = $"{{\"device_id\": \"{device_id}\", \"temperature\": {temperature}, \"humidity\": {humidity}}}";
            Console.WriteLine($"Sending data to server: {sensorData}");

            // Send data to the server
            var content = new StringContent(sensorData, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(api_url, content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("Data sent successfully.");
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Error sending data to server: {e.Message}");
        }
    }
}
