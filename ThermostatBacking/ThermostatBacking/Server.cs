using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

public class Server
{
    static MySqlConnection dbConnection;

    public static void Main(string[] args)
    {
        var config = LoadConfig();
        var databaseConfig = config.GetSection("database_config");

        // Retrieve database configuration values
        var host = databaseConfig["host"];
        var user = databaseConfig["user"];
        var password = databaseConfig["password"];
        var databaseName = databaseConfig["database_name"];

        // Connect to MySQL database
        string connectionString = $"Server={host};User ID={user};Password={password};Database={databaseName};";
        dbConnection = new MySqlConnection(connectionString);
        dbConnection.Open();

        var webHost = new WebHostBuilder()
            .UseKestrel()
            .Configure(app =>
            {
                app.Run(async context =>
                {
                    if (context.Request.Path == "/api/sensor_data" && context.Request.Method == "POST")
                    {
                        await ReceiveData(context);
                    }
                    else if (context.Request.Path == "/api/get_current_temperature" && context.Request.Method == "GET")
                    {
                        await GetCurrentTemperature(context);
                    }
                    else
                    {
                        context.Response.StatusCode = 404;
                    }
                });
            })
            .Build();

        webHost.Run();
    }

    static IConfiguration LoadConfig()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("config.json")
            .Build();

        return configuration;
    }

    static async Task ReceiveData(HttpContext context)
    {
        using (var reader = new StreamReader(context.Request.Body))
        {
            var data = await reader.ReadToEndAsync();
            Console.WriteLine("Received data: " + data);

            // Parse data and insert into database
            // Assuming data is in JSON format with "device_id", "temperature", "humidity" properties

            var jsonData = JsonSerializer.Deserialize<Dictionary<string, object>>(data);
            var device_id = (string)jsonData["device_id"];
            var temperature = Convert.ToSingle(jsonData["temperature"]);
            var humidity = Convert.ToSingle(jsonData["humidity"]);

            using (var cmd = new MySqlCommand("INSERT INTO sensor_data (device_id, temperature, humidity, timestamp, ip_address) VALUES (@device_id, @temperature, @humidity, @timestamp, @ip_address)", dbConnection))
            {
                cmd.Parameters.AddWithValue("@device_id", device_id);
                cmd.Parameters.AddWithValue("@temperature", temperature);
                cmd.Parameters.AddWithValue("@humidity", humidity);
                cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@ip_address", context.Connection.RemoteIpAddress.ToString());
                cmd.ExecuteNonQuery();
            }
        }

        context.Response.StatusCode = 200;
    }

    static async Task GetCurrentTemperature(HttpContext context)
    {
        using (var cmd = new MySqlCommand("SELECT temperature FROM sensor_data ORDER BY timestamp DESC LIMIT 1", dbConnection))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                var temperature = reader.GetFloat(0);
                var responseJson = JsonSerializer.Serialize(new { temperature });
                await context.Response.WriteAsync(responseJson);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
    }
}
