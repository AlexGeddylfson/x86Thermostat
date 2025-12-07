// Version 16 - Emergency stop now respected when fan mode is toggled
// ThermostatHost.cs - Complete Thermostat/Probe component with device settings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThermostatController
{
    // === MAIN HOST ===
    public static class ThermostatHost
    {
        private static readonly DateTime StartTime = DateTime.UtcNow;

        public static async Task RunAsync(ThermostatConfig config, CancellationToken cancellationToken)
        {
            var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
            var logger = loggerFactory.CreateLogger<Program>();

            // Initialize hardware
            IHardwareInterface? hardware = null;
            if (config.NeedsSensorReading())
            {
                var hwFactory = new HardwareFactory(config, loggerFactory.CreateLogger<HardwareFactory>());
                hardware = hwFactory.Create();
            }

            if (hardware == null && config.NeedsSensorReading())
            {
                logger.LogError("Failed to initialize hardware - cannot start thermostat/probe");
                return;
            }

            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

                // Auto-register device with server on startup
                await AutoRegisterDeviceAsync(httpClient, config, logger);

                // Initialize polling service
                Polling? polling = null;
                Controller? controller = null;

                if (config.NeedsSensorReading() && hardware != null)
                {
                    // Polling constructor now starts its own dedicated background loops
                    polling = new Polling(config, hardware, httpClient, loggerFactory.CreateLogger<Polling>(), cancellationToken);

                    if (config.NeedsThermostatControl())
                    {
                        controller = new Controller(config, hardware, polling, httpClient, loggerFactory.CreateLogger<Controller>());
                    }
                }

                // Start web API
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(config);
                        if (polling != null) services.AddSingleton(polling);
                        if (controller != null) services.AddSingleton(controller);
                        services.AddSingleton(httpClient);
                        services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
                        
                        // Configure host shutdown timeout
                        services.Configure<HostOptions>(opts =>
                        {
                            opts.ShutdownTimeout = TimeSpan.FromSeconds(3);
                        });
                    })
                    .ConfigureWebHostDefaults(webBuilder =>
                    {
                        webBuilder.Configure((context, app) =>
                        {
                            var appLogger = app.ApplicationServices.GetRequiredService<ILogger<Program>>();
                            var appPolling = app.ApplicationServices.GetService<Polling>();
                            var appController = app.ApplicationServices.GetService<Controller>();
                            var appConfig = app.ApplicationServices.GetRequiredService<ThermostatConfig>();
                            var appHttpClient = app.ApplicationServices.GetRequiredService<HttpClient>();

                            app.UseCors();
                            app.UseRouting();
                            app.UseEndpoints(endpoints =>
                            {
                                ConfigureThermostatEndpoints(endpoints, appConfig, appPolling, appController, appHttpClient, appLogger);
                            });
                        });
                        webBuilder.UseUrls($"http://{config.ApiHost}:5001");
                    })
                    .Build();

                logger.LogInformation("========================================");
                logger.LogInformation("{Type} started on http://{Host}:5001", config.DeploymentType, config.ApiHost);
                logger.LogInformation("Device registered with server: {DeviceId}", config.DeviceId);
                logger.LogInformation("========================================");

                // Start device heartbeat poller
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StartHeartbeatAsync(httpClient, config, logger, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal shutdown
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Heartbeat task failed");
                    }
                }, cancellationToken);

                // Start control loop if thermostat mode
                if (config.NeedsThermostatControl() && controller != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.ControlLoopIntervalMs));
                            while (await timer.WaitForNextTickAsync(cancellationToken))
                            {
                                try
                                {
                                    controller.UpdateAsync();
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Control loop error");
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Normal shutdown
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Control task failed");
                        }
                    }, cancellationToken);
                }

                // Wait for cancellation or host shutdown
                await host.RunAsync(cancellationToken);
            }
            finally
            {
                hardware?.Cleanup();
                hardware?.Dispose();
            }
        }

        /// <summary>
        /// Retrieves the local non-loopback IPv4 address for registration.
        /// </summary>
        private static string GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                
                var lanAddress = host.AddressList.FirstOrDefault(ip => 
                    ip.AddressFamily == AddressFamily.InterNetwork && 
                    !IPAddress.IsLoopback(ip) && 
                    !ip.ToString().StartsWith("169.254.")
                );

                if (lanAddress != null)
                {
                    return lanAddress.ToString();
                }
                
                var fallbackAddress = host.AddressList.FirstOrDefault(ip => 
                    ip.AddressFamily == AddressFamily.InterNetwork && 
                    !IPAddress.IsLoopback(ip)
                );
                
                if (fallbackAddress != null)
                {
                    return fallbackAddress.ToString();
                }

                return "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private static async Task AutoRegisterDeviceAsync(HttpClient httpClient, ThermostatConfig config, ILogger logger)
        {
            var serverUrl = config.VmServer;
            var deviceId = config.DeviceId;
            var deviceType = config.DeploymentType.ToString();
            var localIp = GetLocalIpAddress();

            var payload = new
            {
                device_id = deviceId,
                device_type = deviceType,
                device_name = deviceId,
                location = "",
                ip_address = localIp,
                com_port = config.ArduinoComPort,
                relay_port = config.NeedsThermostatControl() ? config.RelayComPort : null,
                gpio_pins = config.RelayPins.Count > 0 ? string.Join(",", config.RelayPins) : null,
                is_active = true
            };

            try
            {
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync($"{serverUrl}/api/devices/register", content);
                
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Device {Id} registered successfully with server at {Ip}", deviceId, localIp);
                }
                else
                {
                    logger.LogWarning("Device registration returned status: {Status}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register device with server - will retry via heartbeat");
            }
        }

        private static async Task StartHeartbeatAsync(
            HttpClient httpClient, 
            ThermostatConfig config, 
            ILogger logger, 
            CancellationToken cancellationToken)
        {
            var serverUrl = config.VmServer;
            var deviceId = config.DeviceId;
            int consecutiveFailures = 0;
            const int MAX_FAILURES_BEFORE_WARN = 5;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var localIp = GetLocalIpAddress();
                    var response = await httpClient.PostAsync(
                        $"{serverUrl}/api/devices/{deviceId}/heartbeat?ip={localIp}", 
                        null,
                        cancellationToken
                    );
                    
                    if (response.IsSuccessStatusCode)
                    {
                        if (consecutiveFailures >= MAX_FAILURES_BEFORE_WARN)
                        {
                            logger.LogInformation("✓ Heartbeat reconnected to server after {Count} failures", consecutiveFailures);
                        }
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures == MAX_FAILURES_BEFORE_WARN)
                        {
                            logger.LogWarning("Heartbeat failing: {Status} (failed {Count} times)", 
                                response.StatusCode, consecutiveFailures);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // Let it bubble up to exit cleanly
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures == MAX_FAILURES_BEFORE_WARN)
                    {
                        logger.LogWarning(ex, "Heartbeat unreachable (failed {Count} times) - server may be down", consecutiveFailures);
                    }
                    else if (consecutiveFailures % 30 == 0)
                    {
                        // Log every 30 minutes if still failing
                        logger.LogWarning("Heartbeat still failing after {Minutes} minutes", consecutiveFailures);
                    }
                }
            }
        }

        private static void ConfigureThermostatEndpoints(
            Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,
            ThermostatConfig config,
            Polling? polling,
            Controller? controller,
            HttpClient httpClient,
            ILogger logger)
        {
            endpoints.MapGet("/", async ctx =>
            {
                var uptime = DateTime.UtcNow - StartTime;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    message = "Thermostat/Probe API",
                    deviceId = config.DeviceId,
                    deploymentType = config.DeploymentType.ToString(),
                    currentPort = "5001",
                    uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
                    platform = RuntimeInformation.OSDescription,
                    temperatureUnit = config.TemperatureUnit,
                    hasHardware = polling != null,
                    isThermostat = controller != null
                });
            });

            endpoints.MapGet("/api/health", async ctx =>
            {
                var uptime = DateTime.UtcNow - StartTime;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    status = "healthy",
                    uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
                    deployment_type = config.DeploymentType.ToString(),
                    port = 5001,
                    device_id = config.DeviceId
                });
            });

            endpoints.MapGet("/api/status", async ctx =>
            {
                var response = new Dictionary<string, object>
                {
                    ["device_id"] = config.DeviceId,
                    ["device_type"] = config.DeploymentType.ToString()
                };

                if (polling != null)
                {
                    var (temp, hum) = polling.GetCurrentReadings();
                    response["temperature"] = temp ?? 0.0;
                    response["humidity"] = hum ?? 0.0;
                    response["set_temperature"] = polling.GetUserSetTemp();
                }

                if (controller != null)
                {
                    response["state"] = controller.GetStateName();
                    response["emergency_stop"] = controller.EmergencyStop;
                    response["fan_mode"] = controller.FanMode;
                    response["cooldown_remaining_seconds"] = controller.GetRemainingCooldownSeconds();
                    response["estimated_time_to_target_seconds"] = controller.GetEstimatedTimeToTargetSeconds();
                    response["state_time_seconds"] = controller.GetStateTimeSeconds();
                    response["heating_time_seconds"] = controller.GetHeatingTimeSeconds();
                }

                await ctx.Response.WriteAsJsonAsync(response);
            });

            endpoints.MapGet("/api/sensor_data", async ctx =>
            {
                if (polling == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "No sensor available" });
                    return;
                }

                var (temp, hum) = polling.GetCurrentReadings();
                await ctx.Response.WriteAsJsonAsync(new { temperature = temp, humidity = hum });
            });

            endpoints.MapGet("/api/current_state", async ctx =>
            {
                if (controller == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Not a thermostat device" });
                    return;
                }

                await ctx.Response.WriteAsJsonAsync(new
                {
                    current_state = controller.GetStateName(),
                    emergency_stop = controller.EmergencyStop,
                    fan_mode = controller.FanMode,
                    user_temperature = controller.GetUserTemperature()
                });
            });

            endpoints.MapGet("/api/config", async ctx =>
            {
                var safeConfig = new
                {
                    device_id = config.DeviceId,
                    deployment_type = config.DeploymentType.ToString(),
                    temperature_unit = config.TemperatureUnit,
                    cooling_offset = config.CoolingSetTemperatureOffset,
                    heating_offset = config.HeatingSetTemperatureOffset,
                    temperature_threshold = config.TemperatureDifferenceThreshold,
                    compressor_min_off_minutes = config.CompressorMinOffMinutes,
                    emergency_heat_delay_seconds = config.EmergencyHeatDelaySeconds,
                    sensor_poll_interval_seconds = config.SensorPollIntervalSeconds,
                    default_user_set_temperature = config.DefaultUserSetTemperature
                };

                await ctx.Response.WriteAsJsonAsync(safeConfig);
            });

            endpoints.MapPost("/api/config", async ctx =>
            {
                try
                {
                    var updates = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body);
                    if (updates == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
                        return;
                    }

                    bool configChanged = false;

                    if (updates.TryGetValue("cooling_offset", out var coolingOffset))
                    {
                        config.CoolingSetTemperatureOffset = coolingOffset.GetDouble();
                        configChanged = true;
                    }

                    if (updates.TryGetValue("heating_offset", out var heatingOffset))
                    {
                        config.HeatingSetTemperatureOffset = heatingOffset.GetDouble();
                        configChanged = true;
                    }

                    if (updates.TryGetValue("temperature_threshold", out var threshold))
                    {
                        config.TemperatureDifferenceThreshold = threshold.GetDouble();
                        configChanged = true;
                    }

                    if (updates.TryGetValue("compressor_min_off_minutes", out var compressorOff))
                    {
                        config.CompressorMinOffMinutes = compressorOff.GetInt32();
                        configChanged = true;
                    }

                    if (updates.TryGetValue("emergency_heat_delay_seconds", out var emergencyDelay))
                    {
                        config.EmergencyHeatDelaySeconds = emergencyDelay.GetInt32();
                        configChanged = true;
                    }

                    if (updates.TryGetValue("sensor_poll_interval_seconds", out var pollInterval))
                    {
                        config.SensorPollIntervalSeconds = pollInterval.GetInt32();
                        configChanged = true;
                    }

                    if (updates.TryGetValue("default_user_set_temperature", out var defaultTemp))
                    {
                        config.DefaultUserSetTemperature = defaultTemp.GetDouble();
                        configChanged = true;
                    }

                    if (configChanged)
                    {
                        await SaveConfigAsync(config, logger);
                        await ctx.Response.WriteAsJsonAsync(new { message = "Configuration updated successfully" });
                    }
                    else
                    {
                        await ctx.Response.WriteAsJsonAsync(new { message = "No changes made" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error updating configuration");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPost("/api/set_temperature", async ctx =>
            {
                if (polling == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Not a thermostat device or polling not initialized" });
                    return;
                }

                try
                {
                    var req = await JsonSerializer.DeserializeAsync<SetTemperatureRequest>(ctx.Request.Body);
                    if (req == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Missing temperature" });
                        return;
                    }

                    polling.UpdateUserSetTemperature(req.Temperature);
                    await ctx.Response.WriteAsJsonAsync(new { message = "Temperature set successfully" });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in set_temperature");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPost("/api/fan_mode", async ctx =>
            {
                if (controller == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Not a thermostat device" });
                    return;
                }

                try
                {
                    var req = await JsonSerializer.DeserializeAsync<FanModeRequest>(ctx.Request.Body);
                    if (req == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid request" });
                        return;
                    }
                    
                    controller.SetFanMode(req.Enabled);
                    await ctx.Response.WriteAsJsonAsync(new { message = "Fan mode updated" });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in fan_mode");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPost("/api/emergency_stop", async ctx =>
            {
                if (controller == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Not a thermostat device" });
                    return;
                }

                try
                {
                    var req = await JsonSerializer.DeserializeAsync<EmergencyStopRequest>(ctx.Request.Body);
                    if (req == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid request" });
                        return;
                    }
                    
                    if (req.Enable)
                    {
                        controller.EnableEmergencyStop();
                    }
                    else
                    {
                        controller.DisableEmergencyStop();
                    }
                    await ctx.Response.WriteAsJsonAsync(new { message = "Emergency stop updated" });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in emergency_stop");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });
            endpoints.MapGet("/api/deviceid", async ctx =>
{
    await ctx.Response.WriteAsJsonAsync(new { device_id = config.DeviceId });
});

endpoints.MapPost("/api/deviceid", async ctx =>
{
    try
    {
        var req = await JsonSerializer.DeserializeAsync<DeviceIdRequest>(ctx.Request.Body);
        if (req?.DeviceId == null || string.IsNullOrWhiteSpace(req.DeviceId))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "No device_id provided in request" });
            return;
        }

        var oldDeviceId = config.DeviceId;
        var newDeviceId = req.DeviceId.Trim();

        // 1. Update the in-memory config
        config.DeviceId = newDeviceId;

        // 2. Save to config.json file
        await SaveConfigAsync(config, logger);

        logger.LogInformation("Device ID updated locally: '{Old}' -> '{New}'", oldDeviceId, newDeviceId);

        // 3. Notify server about the migration
        bool serverMigrationSuccess = false;
        string? serverError = null;
        
        try
        {
            var localIp = GetLocalIpAddress();
            var deviceRegistration = new DeviceRegistration
            {
                DeviceId = newDeviceId,
                DeviceType = config.DeploymentType.ToString(),
                DeviceName = newDeviceId,
                Location = "",
                IpAddress = localIp,
                ComPort = config.ArduinoComPort,
                RelayPort = config.NeedsThermostatControl() ? config.RelayComPort : null,
                GpioPins = config.RelayPins.Count > 0 ? string.Join(",", config.RelayPins) : null
            };

            var json = JsonSerializer.Serialize(deviceRegistration);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await httpClient.PostAsync(
                $"{config.VmServer}/api/devices/{oldDeviceId}/migrate", 
                content
            );

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
                serverMigrationSuccess = true;
                logger.LogInformation("Server migration successful - {Count} tables updated", 
                    result?["tables_updated"] ?? 0);
            }
            else
            {
                serverError = $"HTTP {response.StatusCode}";
                logger.LogWarning("Server migration failed: {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            serverError = ex.Message;
            logger.LogWarning(ex, "Failed to notify server about device migration");
        }

        await ctx.Response.WriteAsJsonAsync(new 
        { 
            success = true,
            message = "Device ID updated successfully",
            old_device_id = oldDeviceId,
            new_device_id = newDeviceId,
            local_update = true,
            server_migration = serverMigrationSuccess,
            server_error = serverError
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error updating device ID");
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
        }

        private static async Task SaveConfigAsync(ThermostatConfig config, ILogger logger)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };

                var json = JsonSerializer.Serialize(config, options);
                await System.IO.File.WriteAllTextAsync("config.json", json);
                logger.LogInformation("Configuration saved to config.json");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to save configuration");
            }
        }
    }

    // === POLLING SERVICE ===
    public class Polling
    {
        private readonly ThermostatConfig _config;
        private readonly IHardwareInterface _hardware;
        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly object _lock = new();
        private readonly CancellationToken _cancellationToken;
        private PeriodicTimer? _pollTimer;
        private PeriodicTimer? _sendTimer;
        private int _sensorFailures = 0;
        private int _successfulReads = 0;
        private bool _sensorWarmedUp = false;
        private const int WARMUP_SUCCESS_COUNT = 3;

        public double? CurrentTemperature { get; private set; }
        public double? CurrentHumidity { get; private set; }
        private double _userSetTemperature;
        private readonly string _deviceId;

        public Polling(ThermostatConfig config, IHardwareInterface hardware, HttpClient http, ILogger logger, CancellationToken cancellationToken)
        {
            _config = config;
            _hardware = hardware;
            _http = http;
            _logger = logger;
            _cancellationToken = cancellationToken;

            _deviceId = _config.DeviceId;

            _userSetTemperature = _config.DefaultUserSetTemperature;
            _logger.LogInformation("Using default set-point {Temp}°{Unit}", _userSetTemperature, _config.TemperatureUnit);

            _pollTimer = new PeriodicTimer(TimeSpan.FromSeconds(_config.SensorPollIntervalSeconds));
            int sendInterval = (config.DataSendIntervalSeconds > 0) ? config.DataSendIntervalSeconds : 60;
            _sendTimer = new PeriodicTimer(TimeSpan.FromSeconds(sendInterval));
            
            // REMOVED: No longer polling server every 30 seconds
            // Settings are fetched once on boot, then only updated via API endpoint

            // Start background loops
            _ = Task.Run(() => PollLoopAsync(_cancellationToken), _cancellationToken);
            _ = Task.Run(() => SendLoopAsync(_cancellationToken), _cancellationToken);

            // Fetch initial settings from the server on boot only
            _ = Task.Run(async () => await FetchAndApplySettingsAsync(), _cancellationToken);
        }

                private async Task FetchAndApplySettingsAsync()
        {
            const int maxAttempts = 3;
            int attempt = 0;
            bool success = false;

            while (attempt < maxAttempts && !success && !_cancellationToken.IsCancellationRequested)
            {
                attempt++;
                try
                {
                    var url = $"{_config.VmServer}/api/device/{_deviceId}/settings";
                    _logger.LogInformation("Fetching settings from {Url} (attempt {Attempt}/{Max})", url, attempt, maxAttempts);

                    var response = await _http.GetAsync(url, _cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch settings (status: {StatusCode})", response.StatusCode);
                        await Task.Delay(2000, _cancellationToken);
                        continue;
                    }

                    var settings = await response.Content.ReadFromJsonAsync<DeviceSettings>(_cancellationToken);
                    if (settings == null)
                    {
                        _logger.LogWarning("Settings response was empty");
                        await Task.Delay(2000, _cancellationToken);
                        continue;
                    }

                    lock (_lock)
                    {
                        _userSetTemperature = settings.SetTemperature;
                    }

                    _logger.LogInformation("Fetched server set-point {Temp}°{Unit}", _userSetTemperature, _config.TemperatureUnit);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Attempt {Attempt}/{Max} failed to fetch settings", attempt, maxAttempts);
                    await Task.Delay(2000, _cancellationToken);
                }
            }

            if (!success)
            {
                lock (_lock)
                {
                    _userSetTemperature = _config.DefaultUserSetTemperature;
                }
                _logger.LogWarning("Failed to fetch settings after {Max} attempts — using default {Temp}°{Unit}",
                    maxAttempts, _config.DefaultUserSetTemperature, _config.TemperatureUnit);
            }
        }

        public (double? temp, double? hum) GetCurrentReadings()
        {
            lock (_lock) return (CurrentTemperature, CurrentHumidity);
        }

        public double GetUserSetTemp()
        {
            lock (_lock) return _userSetTemperature;
        }

        private async Task PollLoopAsync(CancellationToken cancellationToken)
        {
            if (_pollTimer == null) return;

            try
            {
                while (await _pollTimer.WaitForNextTickAsync(cancellationToken))
                {
                    var (t, h) = _hardware.ReadSensor();

                    if (t.HasValue && h.HasValue)
                    {
                        lock (_lock)
                        {
                            CurrentTemperature = Math.Round(t.Value, 1);
                            CurrentHumidity = Math.Round(h.Value, 2);
                        }
                        
                        _sensorFailures = 0;
                        _successfulReads++;
                        
                        if (!_sensorWarmedUp && _successfulReads >= WARMUP_SUCCESS_COUNT)
                        {
                            _sensorWarmedUp = true;
                            _logger.LogInformation("✓ Sensor warmed up and reading reliably");
                        }
                        
                        _logger.LogDebug("Sensor: Temp: {Temp}°{Unit} Hum: {Hum}%", 
                            CurrentTemperature, _config.TemperatureUnit, CurrentHumidity);
                    }
                    else
                    {
                        _sensorFailures++;
                        
                        if (_sensorWarmedUp)
                        {
                            if (_sensorFailures >= _config.SensorFailureThreshold)
                            {
                                _logger.LogWarning("Sensor failing after warmup! Failed {Count} times (sensor may have disconnected)", 
                                    _sensorFailures);
                            }
                        }
                        else
                        {
                            if (_sensorFailures <= 10)
                            {
                                _logger.LogDebug("Sensor warming up... attempt {Count} (failures during warmup are normal for DHT22)", 
                                    _sensorFailures);
                            }
                            else if (_sensorFailures == 20)
                            {
                                _logger.LogWarning("Sensor still not reading after 20 attempts. Check wiring and power supply.");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Sensor polling loop cancelled");
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            if (_sendTimer == null) return;

            try
            {
                while (await _sendTimer.WaitForNextTickAsync(cancellationToken))
                {
                    var (t, h) = GetCurrentReadings();
                    if (t.HasValue && h.HasValue)
                    {
                        // CHANGED: Actually await the send to prevent fire-and-forget pileup
                        try
                        {
                            await SendDataWithRetryAsync(t.Value, h.Value, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send data after all retries");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Data send loop cancelled");
            }
        }

        private async Task SendDataWithRetryAsync(double temp, double hum, CancellationToken cancellationToken)
        {
            Exception? lastException = null;
            
            for (int i = 0; i < _config.HttpRetryCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                    
                try
                {
                    var json = JsonSerializer.Serialize(new { device_id = _config.DeviceId, temperature = temp, humidity = hum });
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await _http.PostAsync($"{_config.VmServer}/api/receive_data", content, cancellationToken);
                    
                    if (resp.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("Data sent to server: {Temp}°{Unit}, {Hum}%", 
                            temp, _config.TemperatureUnit, hum);
                        return;
                    }
                    else
                    {
                        _logger.LogWarning("Data send failed with status {Status} (attempt {Attempt}/{Max})", 
                            resp.StatusCode, i + 1, _config.HttpRetryCount);
                        lastException = new HttpRequestException($"Server returned {resp.StatusCode}");
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Data send failed (attempt {Attempt}/{Max})", i + 1, _config.HttpRetryCount);
                }
                
                // Wait before retry (but not after last attempt)
                if (i < _config.HttpRetryCount - 1)
                {
                    try
                    {
                        await Task.Delay(2000, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            
            // All retries exhausted
            _logger.LogError("Failed to send data after {Count} attempts. Last error: {Error}", 
                _config.HttpRetryCount, lastException?.Message ?? "Unknown");
        }

        public void UpdateUserSetTemperature(double temp)
        {
            lock (_lock)
            {
                _userSetTemperature = temp;
                _logger.LogInformation("User set temperature updated to {Temp}°{Unit}", temp, _config.TemperatureUnit);
            }
        }
    }

// === CONTROLLER CLASS ===  (updated)
public class Controller
{
    private readonly ThermostatConfig _config;
    private readonly IHardwareInterface _hardware;
    private readonly Polling _polling;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();

    private enum State
    {
        Off,
        BetweenStates,
        FanOnly,
        Cooling,
        Heating,
        EmergencyHeat
    }
    private static readonly string[] StateNames = { "Off", "Between States", "Fan Only", "Cooling", "Heating", "Emergency Heat" };
    private static readonly Dictionary<State, string> StateToMode = new()
    {
        { State.Off, "off" },
        { State.BetweenStates, "off" },
        { State.FanOnly, "fan" },
        { State.Cooling, "cool" },
        { State.Heating, "heat" },
        { State.EmergencyHeat, "emergency" }
    };

    private State CurrentState { get; set; } = State.Off;
    public bool EmergencyStop { get; private set; }
    public bool FanMode { get; private set; }

    private double _userTemp = 72.0;
    private double _lastKnownUserTemp = double.NaN;               // NEW
    private DateTime _lastCompressorOff = DateTime.MinValue;
    private DateTime _heatStart = DateTime.MinValue;
    private string? _lastSentMode;

    private readonly Queue<(DateTime time, double temp)> _temperatureHistory = new();
    private const int MAX_HISTORY_MINUTES = 15;
    private DateTime _stateStartTime = DateTime.MinValue;
    private double? _stateStartTemperature = null;

    private DateTime _lastPoorPerformanceCheck = DateTime.MinValue;
    private bool _poorPerformanceDetectedOnce = false;

    private readonly byte[] OFF_BYTES;
    private readonly byte[] FAN_ONLY_BYTES;
    private readonly byte[] COOL_BYTES;
    private readonly byte[] HEAT_BYTES;
    private readonly byte[] EMERGENCY_BYTES;

    public Controller(ThermostatConfig config, IHardwareInterface hardware, Polling polling,
                      HttpClient http, ILogger logger)
    {
        _config = config;
        _hardware = hardware;
        _polling = polling;
        _http = http;
        _logger = logger;
        _userTemp = polling.GetUserSetTemp();

        OFF_BYTES      = config.RelayCommands.Off.ToBytes();
        FAN_ONLY_BYTES = config.RelayCommands.FanOnly.ToBytes();
        COOL_BYTES     = config.RelayCommands.Cool.ToBytes();
        HEAT_BYTES     = config.RelayCommands.Heat.ToBytes();
        EMERGENCY_BYTES= config.RelayCommands.Emergency.ToBytes();

        _logger.LogInformation("Controller initialized — starting in OFF state");
        SetStateAndRelay(State.Off, OFF_BYTES);
    }

    public void UpdateAsync()
    {
        // --------------------------------------------------------------
        // 1. ALWAYS get the *fresh* user set-point
        // --------------------------------------------------------------
        double newUserTemp = _polling.GetUserSetTemp();

        // --------------------------------------------------------------
        // Emergency stop
        // --------------------------------------------------------------
        if (EmergencyStop)
        {
            SetStateAndRelay(State.Off, OFF_BYTES);
            return;
        }

        var (currentTemp, _) = _polling.GetCurrentReadings();
        if (!currentTemp.HasValue || currentTemp.Value <= 0)
        {
            _logger.LogWarning("No valid temperature reading available");
            return;
        }

        double temp   = currentTemp.Value;
        double target = newUserTemp;
        _userTemp = newUserTemp;  // ← Keep in sync

        UpdateTemperatureHistory(temp);
        double rate = CalculateTemperatureChangeRatePerMinute();

        // --------------------------------------------------------------
        // Cooldown enforcement (compressor protection)
        // --------------------------------------------------------------
        bool inCooldownState = (CurrentState == State.BetweenStates ||
                                CurrentState == State.FanOnly ||
                                CurrentState == State.Off);
        bool compressorStillInCooldown = !CanStartCompressor();

        if (inCooldownState && compressorStillInCooldown)
        {
            var remaining = TimeSpan.FromMinutes(_config.CompressorMinOffMinutes) -
                            (DateTime.Now - _lastCompressorOff);
            _logger.LogDebug("Controller in cooldown — {Time} remaining",
                             remaining.ToString(@"hh\:mm\:ss"));
            SetStateAndRelay(FanMode ? State.FanOnly : State.BetweenStates,
                             FanMode ? FAN_ONLY_BYTES : OFF_BYTES);
            return;
        }

        // --------------------------------------------------------------
        // Thresholds — fresh every loop
        // --------------------------------------------------------------
        bool needsCooling = temp > target + _config.TemperatureDifferenceThreshold;
        bool needsHeating = temp < target - _config.TemperatureDifferenceThreshold;
        double coolingCutOff = target - _config.CoolingSetTemperatureOffset;
        double heatingCutOff = target + _config.HeatingSetTemperatureOffset;

        // --------------------------------------------------------------
        // Detailed status line (optional, but very helpful)
        // --------------------------------------------------------------
        var status = $"Controller: Temp: {temp:F1}°{_config.TemperatureUnit}, " +
                     $"Target: {target:F1}°{_config.TemperatureUnit}, " +
                     $"Diff: {temp - target:+0.0;-0.0}°{_config.TemperatureUnit}, " +
                     $"State: {GetStateName()}";

        if (CurrentState == State.Heating || CurrentState == State.EmergencyHeat ||
            CurrentState == State.Cooling)
        {
            if (_temperatureHistory.Count >= 2)
            {
                var dur = (DateTime.Now - _stateStartTime).TotalMinutes;
                status += $", Rate: {rate:+0.00;-0.00}°/min";
                if (CurrentState == State.Heating || CurrentState == State.EmergencyHeat)
                    status += $", Heating: {dur:F1}min";
                else if (CurrentState == State.Cooling)
                    status += $", Cooling: {dur:F1}min";
            }
        }
        _logger.LogInformation(status);

        // --------------------------------------------------------------
        // CUT-OFF LOGIC
        // --------------------------------------------------------------

        // Cooling cut-off
        if (CurrentState == State.Cooling && temp <= coolingCutOff)
        {
            _logger.LogInformation(
                "Cooling goal met ({Temp:F1} <= {Cut:F1}) — entering cooldown.",
                temp, coolingCutOff);
            SetStateAndRelay(FanMode ? State.FanOnly : State.BetweenStates,
                             FanMode ? FAN_ONLY_BYTES : OFF_BYTES);
            return;
        }

        // Heating / Emergency-Heat cut-off
        if (CurrentState == State.Heating || CurrentState == State.EmergencyHeat)
        {
            if (temp >= heatingCutOff)
            {
                // NO 20-MINUTE LOCK-IN — EXIT IMMEDIATELY
                _logger.LogInformation("Heating goal met ({Temp:F1} >= {Cut:F1}) — entering cooldown.", temp, heatingCutOff);
                SetStateAndRelay(FanMode ? State.FanOnly : State.BetweenStates, FanMode ? FAN_ONLY_BYTES : OFF_BYTES);
                return;
            }

            // Upgrade normal heating → emergency if too slow
            if (CurrentState == State.Heating && IsHeatingIneffective())
            {
                _logger.LogWarning("Heating ineffective — switching to Emergency Heat.");
                SetStateAndRelay(State.EmergencyHeat, EMERGENCY_BYTES);
                return;
            }

            // Stay in current heating mode
            _logger.LogDebug("Maintaining {State} until cut-off.", GetStateName());
            SetStateAndRelay(CurrentState, CurrentState == State.EmergencyHeat ? EMERGENCY_BYTES : HEAT_BYTES);
            return;
        }

        // --------------------------------------------------------------
        // IDLE — temperature is stable
        // --------------------------------------------------------------
        if (inCooldownState && !needsCooling && !needsHeating)
        {
            _logger.LogDebug("Temperature stable — staying idle.");
            SetStateAndRelay(FanMode ? State.FanOnly : State.BetweenStates,
                             FanMode ? FAN_ONLY_BYTES : OFF_BYTES);
            return;
        }

        // --------------------------------------------------------------
        // START NEW CYCLE
        // --------------------------------------------------------------

        if (needsCooling)
        {
            if (CurrentState == State.Heating || CurrentState == State.EmergencyHeat)
            {
                _logger.LogInformation("Need cooling but heating active — entering cooldown.");
                SetStateAndRelay(State.BetweenStates, OFF_BYTES);
                return;
            }

            _logger.LogInformation(
                "Starting COOLING — temp {Temp:F1} > threshold {Thresh:F1}",
                temp, target + _config.TemperatureDifferenceThreshold);
            SetStateAndRelay(State.Cooling, COOL_BYTES);
            return;
        }

        if (needsHeating)
        {
            if (CurrentState == State.Cooling)
            {
                _logger.LogInformation("Need heating but cooling active — entering cooldown.");
                SetStateAndRelay(State.BetweenStates, OFF_BYTES);
                return;
            }

            _logger.LogInformation(
                "Starting HEATING — temp {Temp:F1} < threshold {Thresh:F1}",
                temp, target - _config.TemperatureDifferenceThreshold);
            SetStateAndRelay(State.Heating, HEAT_BYTES);
            return;
        }

        // --------------------------------------------------------------
        // FALLBACK — should never happen
        // --------------------------------------------------------------
        _logger.LogError(
            "FATAL STATE LOGIC ERROR — no transition taken. Forcing OFF. " +
            "State={State}, Temp={Temp:F1}°{Unit}, Target={Target:F1}°{Unit}",
            CurrentState, temp, _config.TemperatureUnit, target);
        SetStateAndRelay(State.Off, OFF_BYTES);
        throw new InvalidOperationException("Thermostat logic error — no valid state transition.");
    }

    // -------------------------------------------------------------------------
    // Helper: set state + write relay + logging + server update
    // -------------------------------------------------------------------------
    private void SetStateAndRelay(State newState, byte[] relayCommand)
    {
        lock (_stateLock)
        {
            if (CurrentState == newState)
            {
                _hardware.SetRelayBytes(relayCommand);
                return;
            }

            var oldState = CurrentState;
            var (curTemp, _) = _polling.GetCurrentReadings();

            // Emergency-Heat cycle summary
            if (oldState == State.EmergencyHeat)
            {
                var dur   = (DateTime.Now - _heatStart).TotalMinutes;
                var rate  = CalculateTemperatureChangeRatePerMinute();
                var delta = curTemp.HasValue && _stateStartTemperature.HasValue
                              ? curTemp.Value - _stateStartTemperature.Value
                              : 0;

                _logger.LogInformation(
                    "╔═══════════════════════════════════════════════╗\n" +
                    "EMERGENCY HEAT CYCLE COMPLETE\n" +
                    "╠═══════════════════════════════════════════════╣\n" +
                    $"  Duration:            {dur:F1} minutes\n" +
                    $"  Temperature Change:  {delta:+0.0;-0.0}°{_config.TemperatureUnit}\n" +
                    $"  Average Rate:        {rate:+0.00;-0.00}°/min\n" +
                    $"  Final Temperature:   {curTemp?.ToString("F1") ?? "—"}°{_config.TemperatureUnit}\n" +
                    $"  Target Temperature:  {_userTemp:F1}°{_config.TemperatureUnit}\n" +
                    $"  → Transitioning to: {GetStateName(newState)}\n" +
                    "╚═══════════════════════════════════════════════╝");
            }
            else
            {
                _logger.LogInformation("STATE CHANGE: {Old} → {New}",
                                        GetStateName(oldState), GetStateName(newState));
            }

            // Compressor-off timestamp
            if ((oldState == State.Cooling || oldState == State.Heating || oldState == State.EmergencyHeat) &&
                (newState == State.BetweenStates || newState == State.FanOnly || newState == State.Off))
            {
                _lastCompressorOff = DateTime.Now;
                _logger.LogInformation("Compressor OFF at {Time} — cooldown started.", _lastCompressorOff);
            }

            // Reset heat-start when entering any heating mode
            if (newState == State.Heating || newState == State.EmergencyHeat)
            {
                _heatStart = DateTime.Now;
                _temperatureHistory.Clear();
            }

            _stateStartTime      = DateTime.Now;
            _stateStartTemperature = curTemp;
            CurrentState = newState;
            _hardware.SetRelayBytes(relayCommand);

            _ = SendModeUpdateAsync(newState);
        }
    }

    private bool CanStartCompressor()
    {
        var required = TimeSpan.FromMinutes(_config.CompressorMinOffMinutes);
        return (DateTime.Now - _lastCompressorOff) >= required;
    }

    public double GetRemainingCooldownSeconds()
    {
        var required = TimeSpan.FromMinutes(_config.CompressorMinOffMinutes);
        var elapsed  = DateTime.Now - _lastCompressorOff;
        return elapsed >= required ? 0.0 : (required - elapsed).TotalSeconds;
    }

    public double GetEstimatedTimeToTargetSeconds()
    {
        var rate = CalculateTemperatureChangeRatePerMinute();
        if (Math.Abs(rate) < 0.01) return 0.0;

        var (cur, _) = _polling.GetCurrentReadings();
        if (!cur.HasValue) return 0.0;

        double diff = 0.0;
        if (CurrentState == State.Cooling)
        {
            diff = cur.Value - (_userTemp - _config.CoolingSetTemperatureOffset);
            if (diff <= 0) return 0.0;
        }
        else if (CurrentState == State.Heating || CurrentState == State.EmergencyHeat)
        {
            diff = (_userTemp + _config.HeatingSetTemperatureOffset) - cur.Value;
            if (diff <= 0) return 0.0;
        }
        else return 0.0;

        return Math.Round(diff / Math.Abs(rate) * 60, 0);
    }

    private bool IsHeatingIneffective()
    {
        if (CurrentState != State.Heating) return false;

        var heatDur = DateTime.Now - _heatStart;
        if (heatDur.TotalMinutes < 10) return false;
        if (_temperatureHistory.Count < 2) return false;

        var span = _temperatureHistory.Last().time - _temperatureHistory.First().time;
        if (span.TotalMinutes < 10) return false;

        var rate = CalculateTemperatureChangeRatePerMinute();
        var (cur, _) = _polling.GetCurrentReadings();
        var deficit = _userTemp - (cur ?? 0);

        double required = deficit < 3.0 ? 0.04 : deficit < 8.0 ? 0.09 : 0.15;
        bool poor = rate < required;

        if (!poor)
        {
            if (_poorPerformanceDetectedOnce)
            {
                _logger.LogInformation(
                    "Heating performance recovered (rate {Rate:F3} ≥ {Req:F2}°/min). Canceling emergency-heat trigger.",
                    rate, required);
                _poorPerformanceDetectedOnce = false;
                _lastPoorPerformanceCheck = DateTime.MinValue;
            }
            return false;
        }

        var sinceCheck = DateTime.Now - _lastPoorPerformanceCheck;
        if (!_poorPerformanceDetectedOnce)
        {
            _poorPerformanceDetectedOnce = true;
            _lastPoorPerformanceCheck = DateTime.Now;
            _logger.LogWarning(
                "Poor heating performance detected (rate {Rate:F3} < {Req:F2}°/min). Will re-check in 5 min.",
                rate, required);
            return false;
        }

        if (sinceCheck.TotalMinutes >= 5)
        {
            _logger.LogWarning(
                "EMERGENCY HEAT ACTIVATED — heating too slow ({Rate:F3} < {Req:F2}°/min).",
                rate, required);
            _poorPerformanceDetectedOnce = false;
            _lastPoorPerformanceCheck = DateTime.MinValue;
            return true;
        }

        return false;
    }

    public void SetFanMode(bool enabled)
    {
        lock (_stateLock)
        {
            if (FanMode == enabled) return;
            FanMode = enabled;
            _logger.LogInformation("Fan mode set to {Mode}", enabled ? "ON" : "AUTO");
            
            // FIXED: Respect emergency stop when toggling fan mode
            if (EmergencyStop)
            {
                _logger.LogInformation("Emergency stop is active — ignoring fan mode change");
                return;
            }
            
            if (CurrentState == State.Off || CurrentState == State.BetweenStates)
                SetStateAndRelay(enabled ? State.FanOnly : State.BetweenStates,
                                 enabled ? FAN_ONLY_BYTES : OFF_BYTES);
        }
    }

    public void EnableEmergencyStop()
    {
        lock (_stateLock)
        {
            if (EmergencyStop) return;
            EmergencyStop = true;
            _logger.LogCritical("EMERGENCY STOP ENABLED — all relays OFF");
            SetStateAndRelay(State.Off, OFF_BYTES);
        }
    }

    public void DisableEmergencyStop()
    {
        lock (_stateLock)
        {
            if (!EmergencyStop) return;
            EmergencyStop = false;
            _logger.LogInformation("Emergency stop disabled — normal operation resumed");
        }
    }

    public string GetStateName() => StateNames[(int)CurrentState];
    private string GetStateName(State s) => StateNames[(int)s];
    public double GetUserTemperature() => _userTemp;

    public double GetStateTimeSeconds()
    {
        lock (_stateLock)
            return _stateStartTime == DateTime.MinValue ? 0.0 : (DateTime.Now - _stateStartTime).TotalSeconds;
    }

    public double GetHeatingTimeSeconds()
    {
        lock (_stateLock)
            return (CurrentState != State.Heating && CurrentState != State.EmergencyHeat) ||
                   _heatStart == DateTime.MinValue
                       ? 0.0
                       : (DateTime.Now - _heatStart).TotalSeconds;
    }

    private async Task SendModeUpdateAsync(State state)
    {
        var mode = StateToMode[state];
        if (_lastSentMode == mode) return;

        try
        {
            var payload = new ModeUpdateRequest { DeviceId = _config.DeviceId, Mode = mode };
            var json    = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp    = await _http.PostAsync($"{_config.VmServer}/api/update_mode", content);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Mode update sent: {Mode}", mode);
                _lastSentMode = mode;
            }
            else
            {
                _logger.LogWarning("Mode update failed — HTTP {Status}", resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send mode update");
        }
    }

    private void UpdateTemperatureHistory(double temp)
    {
        var cutoff = DateTime.Now.AddMinutes(-MAX_HISTORY_MINUTES);
        while (_temperatureHistory.Count > 0 && _temperatureHistory.Peek().time < cutoff)
            _temperatureHistory.Dequeue();
        _temperatureHistory.Enqueue((DateTime.Now, temp));
    }

    private double CalculateTemperatureChangeRatePerMinute()
    {
        if (_temperatureHistory.Count < 2) return 0.0;
        var list = _temperatureHistory.ToList();
        var span = list.Last().time - list.First().time;
        if (span.TotalMinutes < 0.5) return 0.0;
        return (list.Last().temp - list.First().temp) / span.TotalMinutes;
    }
}

    // === REQUEST MODELS ===
    // Note: SetTemperatureRequest, DeviceSettings, ModeUpdateRequest classes
    // are defined in other files (Models.cs or similar)
    
    public class FanModeRequest
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }
    }

    public class EmergencyStopRequest
    {
        [JsonPropertyName("enable")]
        public bool Enable { get; set; }
    }
}
