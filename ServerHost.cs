// Version 15 - Added device migration endpoint
// ServerHost.cs - Complete Server component (port 5000)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
    public static class ServerHost
    {
        private static readonly DateTime StartTime = DateTime.UtcNow;

        public static async Task RunAsync(ThermostatConfig config, CancellationToken cancellationToken)
        {
            var host = Host.CreateDefaultBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(config);
                    services.AddSingleton<IDatabaseService, DatabaseService>();
                    services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.Configure((context, app) =>
                    {
                        var dbService = app.ApplicationServices.GetRequiredService<IDatabaseService>();
                        var logger = app.ApplicationServices.GetRequiredService<ILogger<Program>>();

                        // Middleware to normalize double slashes for Python app compatibility
                        app.Use(async (context, next) =>
                        {
                            var path = context.Request.Path.Value ?? "";
                            // Replace consecutive slashes with a single slash
                            if (path.Contains("//"))
                            {
                                var normalizedPath = System.Text.RegularExpressions.Regex.Replace(path, "/+", "/");
                                context.Request.Path = new PathString(normalizedPath);
                                logger.LogDebug("Normalized path: {Original} -> {Normalized}", path, normalizedPath);
                            }
                            await next();
                        });

                        app.UseCors();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            ConfigureServerEndpoints(endpoints, config, dbService, logger);
                        });
                    });
                    webBuilder.UseUrls($"http://{config.ApiHost}:5000");
                })
                .Build();

            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("========================================");
            logger.LogInformation("SERVER started on http://{Host}:5000", config.ApiHost);
            logger.LogInformation("========================================");

            await host.RunAsync(cancellationToken);
        }

        private static void ConfigureServerEndpoints(
            Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,
            ThermostatConfig config,
            IDatabaseService dbService,
            ILogger logger)
        {
            endpoints.MapGet("/", async ctx =>
            {
                var data = await dbService.GetRecentSensorDataAsync(20);
                var uptime = DateTime.UtcNow - StartTime;
                
                await ctx.Response.WriteAsJsonAsync(new
                {
                    message = "Thermostat Server API",
                    deviceId = config.DeviceId,
                    deploymentType = config.DeploymentType.ToString(),
                    currentPort = "5000",
                    uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
                    platform = RuntimeInformation.OSDescription,
                    temperatureUnit = config.TemperatureUnit,
                    sensor_data_count = data.Count
                });
            });

            endpoints.MapPost("/api/receive_data", async ctx =>
            {
                try
                {
                    var data = await JsonSerializer.DeserializeAsync<SensorDataRequest>(ctx.Request.Body);
                    if (data == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { message = "Invalid data format" });
                        return;
                    }

                    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var success = await dbService.InsertSensorDataAsync(data.DeviceId ?? "unknown", data.Temperature, data.Humidity, ip);

                    if (success)
                    {
                        logger.LogInformation("Received data from {Device}: {Temp}°F, {Hum}%", data.DeviceId, data.Temperature, data.Humidity);
                        await ctx.Response.WriteAsJsonAsync(data);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to store data" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in receive_data");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Internal Server Error" });
                }
            });

            endpoints.MapPost("/api/update_mode", async ctx =>
            {
                try
                {
                    var data = await JsonSerializer.DeserializeAsync<ModeUpdateRequest>(ctx.Request.Body);
                    if (data == null || string.IsNullOrEmpty(data.Mode))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { message = "Invalid data format or missing mode" });
                        return;
                    }

                    var deviceId = data.DeviceId ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var success = await dbService.InsertModeUpdateAsync(deviceId, data.Mode);

                    if (success)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { message = "Mode update inserted successfully" });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to store mode update" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in update_mode");
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPost("/update_temperature", async ctx =>
            {
                try
                {
                    var data = await JsonSerializer.DeserializeAsync<UpdateTemperatureRequest>(ctx.Request.Body);
                    if (data == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid data" });
                        return;
                    }

                    var deviceId = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var success = await dbService.InsertUserSettingAsync(deviceId, data.Temperature);

                    if (success)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { message = "Temperature setpoint inserted successfully" });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to store temperature" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in update_temperature");
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/api/get_last_user_setting", async ctx =>
            {
                try
                {
                    var temp = await dbService.GetLastUserSettingAsync();
                    if (temp.HasValue)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { last_user_setting = temp.Value });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        await ctx.Response.WriteAsJsonAsync(new { message = "No user setting found in the database" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in get_last_user_setting");
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/api/sensor_data", async ctx =>
            {
                try
                {
                    // Check if 'hours' query parameter is provided
                    var hoursParam = ctx.Request.Query["hours"].FirstOrDefault();
                    
                    List<SensorDataRecord> data;
                    
                    if (!string.IsNullOrEmpty(hoursParam) && int.TryParse(hoursParam, out int hours) && hours > 0)
                    {
                        // Use time-based query when hours parameter is provided
                        var startTime = DateTime.UtcNow.AddHours(-hours);
                        data = await dbService.GetSensorDataByTimeRangeAsync(startTime);
                        logger.LogDebug("Fetching sensor data for last {Hours} hours ({Count} records)", hours, data.Count);
                    }
                    else
                    {
                        // Fallback to limit-based query (default 200 records)
                        data = await dbService.GetRecentSensorDataAsync(200);
                        logger.LogDebug("Fetching sensor data (default 200 records, got {Count})", data.Count);
                    }
                    
                    await ctx.Response.WriteAsJsonAsync(data);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in sensor_data");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/api/modes", async ctx =>
            {
                try
                {
                    var data = await dbService.GetRecentModeUpdatesAsync(20);
                    await ctx.Response.WriteAsJsonAsync(data);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in modes");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/app", async ctx =>
            {
                try
                {
                    var devices = await dbService.GetActiveDevicesAsync();
                    await ctx.Response.WriteAsJsonAsync(devices);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in app");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/api/health", async ctx =>
            {
                var uptime = DateTime.UtcNow - StartTime;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    status = "healthy",
                    uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
                    deployment_type = config.DeploymentType.ToString(),
                    port = 5000,
                    device_id = config.DeviceId
                });
            });

            endpoints.MapGet("/api/deviceid", async ctx =>
            {
                await ctx.Response.WriteAsJsonAsync(new { device_id = config.DeviceId });
            });

            endpoints.MapPost("/api/deviceid", async ctx =>
            {
                var req = await JsonSerializer.DeserializeAsync<DeviceIdRequest>(ctx.Request.Body);
                if (req?.DeviceId != null)
                {
                    config.DeviceId = req.DeviceId;
                    await ctx.Response.WriteAsJsonAsync(new { message = "Device ID updated successfully", new_device_id = req.DeviceId });
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "No device_id provided in request" });
                }
            });

            // Device registry endpoints
            endpoints.MapGet("/api/devices", async ctx =>
            {
                try
                {
                    var devices = await dbService.GetAllDevicesAsync();
                    var deviceList = new List<object>();

                    foreach (var d in devices)
                    {
                        var lastSensor = await dbService.GetLastSensorDataAsync(d.DeviceId);
                        deviceList.Add(new
                        {
                            device_id = d.DeviceId,
                            device_type = d.DeviceType,
                            device_name = d.DeviceName ?? d.DeviceId,
                            location = d.Location,
                            ip_address = d.IpAddress,
                            com_port = d.ComPort,
                            relay_port = d.RelayPort,
                            gpio_pins = d.GpioPins,
                            is_active = d.IsActive,
                            last_seen = d.LastSeen,
                            registered_at = d.RegisteredAt,
                            updated_at = d.UpdatedAt,
                            last_temperature = lastSensor?.Temperature,
                            last_humidity = lastSensor?.Humidity
                        });
                    }

                    await ctx.Response.WriteAsJsonAsync(new { devices = deviceList });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in get devices");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/api/devices/{deviceId}", async ctx =>
            {
                try
                {
                    var deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var device = await dbService.GetDeviceAsync(deviceId);
                    if (device != null)
                    {
                        var lastSensor = await dbService.GetLastSensorDataAsync(deviceId);
                        await ctx.Response.WriteAsJsonAsync(new
                        {
                            device_id = device.DeviceId,
                            device_type = device.DeviceType,
                            device_name = device.DeviceName ?? device.DeviceId,
                            location = device.Location,
                            ip_address = device.IpAddress,
                            com_port = device.ComPort,
                            relay_port = device.RelayPort,
                            gpio_pins = device.GpioPins,
                            is_active = device.IsActive,
                            last_seen = device.LastSeen,
                            registered_at = device.RegisteredAt,
                            updated_at = device.UpdatedAt,
                            last_temperature = lastSensor?.Temperature,
                            last_humidity = lastSensor?.Humidity
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device not found" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in get device");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapGet("/api/devices/by-type/{type}", async ctx =>
            {
                try
                {
                    var type = ctx.Request.RouteValues["type"]?.ToString();
                    if (string.IsNullOrEmpty(type))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Type is required" });
                        return;
                    }

                    var devices = await dbService.GetDevicesByTypeAsync(type);
                    var deviceList = new List<object>();

                    foreach (var d in devices)
                    {
                        var lastSensor = await dbService.GetLastSensorDataAsync(d.DeviceId);
                        deviceList.Add(new
                        {
                            device_id = d.DeviceId,
                            device_type = d.DeviceType,
                            device_name = d.DeviceName ?? d.DeviceId,
                            location = d.Location,
                            ip_address = d.IpAddress,
                            com_port = d.ComPort,
                            relay_port = d.RelayPort,
                            gpio_pins = d.GpioPins,
                            is_active = d.IsActive,
                            last_seen = d.LastSeen,
                            registered_at = d.RegisteredAt,
                            updated_at = d.UpdatedAt,
                            last_temperature = lastSensor?.Temperature,
                            last_humidity = lastSensor?.Humidity
                        });
                    }

                    await ctx.Response.WriteAsJsonAsync(new { devices = deviceList });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in get devices by type");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPost("/api/devices/register", async ctx =>
            {
                try
                {
                    var req = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(ctx.Request.Body);
                    if (req == null || !req.TryGetValue("device_id", out var devIdObj) || 
                        !req.TryGetValue("device_type", out var devTypeObj))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID and type are required" });
                        return;
                    }

                    var deviceId = devIdObj.GetString()!;
                    var deviceType = devTypeObj.GetString()!;

                    var device = new DeviceRegistration
                    {
                        DeviceId = deviceId,
                        DeviceType = deviceType,
                        DeviceName = req.TryGetValue("device_name", out var nameEl) ? nameEl.GetString() : deviceId,
                        Location = req.TryGetValue("location", out var locEl) ? locEl.GetString() : "",
                        IpAddress = req.TryGetValue("ip_address", out var ipEl) ? ipEl.GetString() : "",
                        ComPort = req.TryGetValue("com_port", out var comEl) ? comEl.GetString() : null,
                        RelayPort = req.TryGetValue("relay_port", out var relayEl) ? relayEl.GetString() : null,
                        GpioPins = req.TryGetValue("gpio_pins", out var gpioEl) ? gpioEl.GetString() : null
                    };

                    bool isActive = true;
                    if (req.TryGetValue("is_active", out var activeEl))
                    {
                        isActive = activeEl.GetBoolean();
                    }

                    var success = await dbService.RegisterDeviceAsync(device, isActive);

                    if (success)
                    {
                        logger.LogInformation("Device {Id} registered: {Type} at {Ip}", deviceId, deviceType, device.IpAddress);
                        await ctx.Response.WriteAsJsonAsync(new { 
                            message = "Device registered successfully",
                            device_id = deviceId
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to register device" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in register device");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

 // NEW ENDPOINT: Handle device ID migration from the device itself
            endpoints.MapPost("/api/devices/{oldDeviceId}/migrate", async ctx =>
            {
                string? oldDeviceId = null;
                try
                {
                    oldDeviceId = ctx.Request.RouteValues["oldDeviceId"]?.ToString();
                    if (string.IsNullOrEmpty(oldDeviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Old device ID is required" });
                        return;
                    }

                    var req = await JsonSerializer.DeserializeAsync<DeviceRegistration>(ctx.Request.Body);
                    if (req == null || string.IsNullOrEmpty(req.DeviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "New device information is required" });
                        return;
                    }

                    var newDeviceId = req.DeviceId;

                    // Check if old device exists
                    var oldDevice = await dbService.GetDeviceAsync(oldDeviceId);
                    if (oldDevice == null)
                    {
                        ctx.Response.StatusCode = 404;
                        await ctx.Response.WriteAsJsonAsync(new { error = $"Device '{oldDeviceId}' not found" });
                        return;
                    }

                    // Check if new device ID already exists (prevent conflicts)
                    var existingDevice = await dbService.GetDeviceAsync(newDeviceId);
                    if (existingDevice != null && existingDevice.DeviceId != oldDeviceId)
                    {
                        ctx.Response.StatusCode = 409;
                        await ctx.Response.WriteAsJsonAsync(new { error = $"Device ID '{newDeviceId}' already exists" });
                        return;
                    }

                    logger.LogInformation("📝 Device migration request: '{Old}' -> '{New}'", oldDeviceId, newDeviceId);

                    // 1. Update all database tables with the new device_id
                    var tablesUpdated = await dbService.UpdateDeviceIdInAllTablesAsync(oldDeviceId, newDeviceId);
                    
                    if (tablesUpdated == 0)
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to update database tables" });
                        return;
                    }

                    // 2. Update device metadata (location, IP, etc.) using UpdateDeviceAsync
                    // This updates the EXISTING record rather than creating a new one
                    var updateSuccess = await dbService.UpdateDeviceAsync(newDeviceId, req);
                    
                    if (!updateSuccess)
                    {
                        logger.LogWarning("Device metadata update failed, but migration succeeded");
                    }

                    logger.LogInformation("Successfully migrated device from '{Old}' to '{New}' - {Count} tables updated",
                        oldDeviceId, newDeviceId, tablesUpdated);

                    await ctx.Response.WriteAsJsonAsync(new 
                    { 
                        success = true,
                        old_device_id = oldDeviceId,
                        new_device_id = newDeviceId,
                        tables_updated = tablesUpdated,
                        message = "Device migrated successfully"
                    });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error migrating device {DeviceId}", oldDeviceId ?? "unknown");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPost("/api/devices/{deviceId}/heartbeat", async ctx =>
            {
                try
                {
                    var deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();
                    var ipAddress = ctx.Request.Query["ip"].ToString();
                    
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var success = await dbService.UpdateDeviceLastSeenAsync(
                        deviceId, 
                        !string.IsNullOrEmpty(ipAddress) ? ipAddress : ctx.Connection.RemoteIpAddress?.ToString()
                    );

                    if (success)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { message = "Heartbeat received" });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to update heartbeat" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in heartbeat");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapPut("/api/devices/{deviceId}", async ctx =>
            {
                try
                {
                    var deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var req = await JsonSerializer.DeserializeAsync<DeviceRegistration>(ctx.Request.Body);
                    if (req == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
                        return;
                    }

                    var success = await dbService.UpdateDeviceAsync(deviceId, req);
                    if (success)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { message = "Device updated successfully" });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to update device" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in update device");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            endpoints.MapDelete("/api/devices/{deviceId}", async ctx =>
            {
                try
                {
                    var deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var success = await dbService.DeactivateDeviceAsync(deviceId);
                    if (success)
                    {
                        await ctx.Response.WriteAsJsonAsync(new { message = "Device deactivated successfully" });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to deactivate device" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in deactivate device");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            // ===== DEVICE-SPECIFIC SETTINGS ENDPOINTS =====

            // Get device-specific settings
            endpoints.MapGet("/api/device/{deviceId}/settings", async ctx =>
            {
                string? deviceId = null;
                try
                {
                    deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var settings = await dbService.GetDeviceSettingsAsync(deviceId);
                    if (settings != null)
                    {
                        await ctx.Response.WriteAsJsonAsync(settings);
                    }
                    else
                    {
                        // Return default settings
                        await ctx.Response.WriteAsJsonAsync(new DeviceSettings
                        {
                            DeviceId = deviceId,
                            SetTemperature = config.DefaultUserSetTemperature,
                            Mode = "Auto",
                            LastUpdated = DateTime.UtcNow,
                            UpdatedBy = "default"
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error getting device settings for {DeviceId}", deviceId ?? "unknown");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            // Update device set temperature
            endpoints.MapPost("/api/device/{deviceId}/set_temperature", async ctx =>
            {
                string? deviceId = null;
                try
                {
                    deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();
                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var data = await JsonSerializer.DeserializeAsync<SetTemperatureRequest>(ctx.Request.Body);
                    if (data == null || data.Temperature < 40 || data.Temperature > 90)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Invalid temperature (must be 40-90°F)" });
                        return;
                    }

                    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var success = await dbService.UpdateDeviceSetTemperatureAsync(deviceId, data.Temperature, ip);
                    
                    if (success)
                    {
                        logger.LogInformation("Temperature updated for {Device}: {Temp}°F (by: {IP})", 
                            deviceId, data.Temperature, ip);
                        await ctx.Response.WriteAsJsonAsync(new 
                        { 
                            success = true,
                            deviceId = deviceId,
                            temperature = data.Temperature,
                            timestamp = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to update temperature" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error updating temperature for device {DeviceId}", deviceId ?? "unknown");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            // Update device mode
            endpoints.MapPost("/api/device/{deviceId}/set_mode", async ctx =>
            {
                string? deviceId = null;
                try
                {
                    deviceId = ctx.Request.RouteValues["deviceId"]?.ToString();

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Device ID is required" });
                        return;
                    }

                    var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(ctx.Request.Body);
                    if (data == null || !data.TryGetValue("mode", out var mode))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Mode is required" });
                        return;
                    }

                    var validModes = new[] { "Auto", "Heat", "Cool", "Off" };
                    if (!Array.Exists(validModes, m => m.Equals(mode, StringComparison.OrdinalIgnoreCase)))
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.WriteAsJsonAsync(new { error = $"Invalid mode. Must be one of: {string.Join(", ", validModes)}" });
                        return;
                    }

                    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var success = await dbService.UpdateDeviceModeAsync(deviceId, mode, ip);
                    
                    if (success)
                    {
                        logger.LogInformation("Mode updated for {Device}: {Mode} (by: {IP})", 
                            deviceId, mode, ip);
                        await ctx.Response.WriteAsJsonAsync(new 
                        { 
                            success = true,
                            deviceId = deviceId,
                            mode = mode,
                            timestamp = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        ctx.Response.StatusCode = 500;
                        await ctx.Response.WriteAsJsonAsync(new { error = "Failed to update mode" });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error updating mode for device {DeviceId}", deviceId ?? "unknown");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });

            // Get all devices with their settings
            endpoints.MapGet("/api/devices/all", async ctx =>
            {
                try
                {
                    var devices = await dbService.GetAllDevicesAsync();
                    var result = new List<object>();

                    foreach (var device in devices)
                    {
                        var settings = await dbService.GetDeviceSettingsAsync(device.DeviceId);
                        var lastData = await dbService.GetLastSensorDataAsync(device.DeviceId);

                        result.Add(new
                        {
                            device.DeviceId,
                            device.DeviceName,
                            device.DeviceType,
                            device.Location,
                            device.IsActive,
                            device.LastSeen,
                            device.IpAddress,
                            settings = settings ?? new DeviceSettings
                            {
                                DeviceId = device.DeviceId,
                                SetTemperature = config.DefaultUserSetTemperature,
                                Mode = "Auto"
                            },
                            lastReading = lastData != null ? new
                            {
                                temperature = lastData.Temperature,
                                humidity = lastData.Humidity,
                                timestamp = lastData.Timestamp
                            } : null
                        });
                    }

                    await ctx.Response.WriteAsJsonAsync(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error getting all devices");
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
                }
            });
        }
    }

    // Request/Response models for Server
    public class SensorDataRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("humidity")]
        public double Humidity { get; set; }
    }

    public class ModeUpdateRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }
    }

    public class UpdateTemperatureRequest
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
    }

    public class DeviceIdRequest
    {
        [JsonPropertyName("device_id")]
        public string? DeviceId { get; set; }
    }

    public class SetTemperatureRequest
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
    }
}
