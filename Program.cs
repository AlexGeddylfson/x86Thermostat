// Program.cs - Main entry point with configuration validation
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ThermostatController
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
            var logger = loggerFactory.CreateLogger<Program>();
            
            logger.LogInformation("========================================");
            logger.LogInformation("THERMOSTAT CONTROLLER STARTING");
            logger.LogInformation("========================================");

            var config = await LoadConfigAsync(logger);
            if (config == null) return;

            // VALIDATE CONFIGURATION BEFORE PROCEEDING
            var validatorLogger = loggerFactory.CreateLogger<ConfigValidator>();
            var validator = new ConfigValidator(config, validatorLogger);
            if (!validator.Validate())
            {
                logger.LogError("========================================");
                logger.LogError("Configuration validation FAILED");
                logger.LogError("Please fix the errors in config.json");
                logger.LogError("========================================");
                logger.LogError("Press any key to exit...");
                Console.ReadKey();
                return;
            }

            using var cts = new CancellationTokenSource();
            var shutdownRequested = false;
            Console.CancelKeyPress += (_, e) =>
            {
                if (!shutdownRequested)
                {
                    logger.LogInformation("Ctrl+C received – shutting down gracefully...");
                    e.Cancel = true;
                    shutdownRequested = true;
                    cts.Cancel();
                    
                    // Give it 5 seconds to shut down, then force exit
                    Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        if (shutdownRequested)
                        {
                            logger.LogWarning("Shutdown timeout - forcing exit");
                            Environment.Exit(0);
                        }
                    });
                }
                else
                {
                    logger.LogWarning("Force exit requested");
                    Environment.Exit(1);
                }
            };

            try
            {
                // Based on deployment type, start appropriate components
                switch (config.DeploymentType)
                {
                    case DeploymentType.Server:
                        logger.LogInformation("Starting SERVER mode (Port 5000)");
                        await ServerHost.RunAsync(config, cts.Token);
                        break;

                    case DeploymentType.Thermostat:
                    case DeploymentType.Probe:
                        logger.LogInformation("Starting {Type} mode (Port 5001)", config.DeploymentType);
                        await ThermostatHost.RunAsync(config, cts.Token);
                        break;

                    case DeploymentType.HybridProbe:
                    case DeploymentType.HybridThermo:
                        logger.LogInformation("Starting HYBRID mode - {Type}", config.DeploymentType);
                        logger.LogInformation("  - Server component on Port 5000");
                        logger.LogInformation("  - {Type} component on Port 5001", 
                            config.DeploymentType == DeploymentType.HybridThermo ? "Thermostat" : "Probe");
                        
                        // Run both server and thermostat/probe in parallel
                        var serverTask = Task.Run(async () => 
                        {
                            try
                            {
                                await ServerHost.RunAsync(config, cts.Token);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Server component error");
                            }
                        });
                        
                        var clientTask = Task.Run(async () => 
                        {
                            try
                            {
                                await ThermostatHost.RunAsync(config, cts.Token);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Thermostat/Probe component error");
                            }
                        });
                        
                        await Task.WhenAll(serverTask, clientTask);
                        break;

                    default:
                        logger.LogError("Unknown deployment type: {Type}", config.DeploymentType);
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Shutdown requested");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fatal error");
            }
            finally
            {
                shutdownRequested = false; // Mark shutdown complete
                logger.LogInformation("Shutdown complete");
            }
        }

        private static async Task<ThermostatConfig?> LoadConfigAsync(ILogger logger)
        {
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };

            if (!File.Exists("config.json"))
            {
                logger.LogInformation("config.json not found - creating default configuration");
                var cfg = new ThermostatConfig();
                var json = JsonSerializer.Serialize(cfg, jsonOptions);
                await File.WriteAllTextAsync("config.json", json);
                logger.LogInformation("Created default config.json");
                logger.LogInformation("========================================");
                logger.LogInformation("IMPORTANT: Edit config.json with your settings, then restart.");
                logger.LogInformation("========================================");
                return null;
            }

            try
            {
                var text = await File.ReadAllTextAsync("config.json");
                var cfgLoaded = JsonSerializer.Deserialize<ThermostatConfig>(text, jsonOptions);

                if (cfgLoaded == null)
                {
                    logger.LogError("Failed to parse config.json");
                    return null;
                }

                logger.LogInformation("========================================");
                logger.LogInformation("Configuration Loaded:");
                logger.LogInformation("  Deployment Type: {Type}", cfgLoaded.DeploymentType);
                logger.LogInformation("  Device ID: {Id}", cfgLoaded.DeviceId);
                logger.LogInformation("  Hardware Mode: {Mode}", cfgLoaded.Mode);
                
                if (cfgLoaded.NeedsSensorReading())
                {
                    if (!string.IsNullOrEmpty(cfgLoaded.ArduinoComPort))
                        logger.LogInformation("  Arduino Port: {Port}", cfgLoaded.ArduinoComPort);
                    
                    if (cfgLoaded.NeedsThermostatControl())
                    {
                        if (!string.IsNullOrEmpty(cfgLoaded.RelayComPort))
                            logger.LogInformation("  Relay Port: {Port}", cfgLoaded.RelayComPort);
                        if (cfgLoaded.EnableFtdiRelay)
                            logger.LogInformation("  FTDI Relay: Enabled (Serial: {Serial})", cfgLoaded.FtdiSerialNumber);
                        if (cfgLoaded.RelayPins.Count > 0)
                            logger.LogInformation("  GPIO Relay Pins: [{Pins}]", string.Join(", ", cfgLoaded.RelayPins));
                    }
                    
                    if (cfgLoaded.DhtSensorPin > 0)
                        logger.LogInformation("  DHT Sensor Pin: {Pin}", cfgLoaded.DhtSensorPin);
                }
                
                logger.LogInformation("========================================");

                return cfgLoaded;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error loading config.json");
                return null;
            }
        }
    }
}
