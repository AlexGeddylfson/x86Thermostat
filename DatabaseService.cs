// DatabaseService.cs - SQLite Database Implementation with Device Registry and Device Settings
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ThermostatController
{
    // Custom JSON converter to format DateTime as MySQL format: yyyy-MM-dd HH:mm:ss
    public class MySqlDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTime.Parse(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }

    public class GmtDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return DateTime.Parse(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'"));
        }
    }

    // Device-specific settings model
    public class DeviceSettings
    {
        public string DeviceId { get; set; } = "";
        public double SetTemperature { get; set; }
        public string Mode { get; set; } = "Auto";  // Auto, Heat, Cool, Off
        public DateTime LastUpdated { get; set; }
        public string UpdatedBy { get; set; } = "Unknown";  // IP or user identifier
    }

    public interface IDatabaseService
    {
        Task<bool> InsertSensorDataAsync(string deviceId, double temperature, double humidity, string ipAddress);
        Task<bool> InsertModeUpdateAsync(string deviceId, string mode);
        Task<bool> InsertUserSettingAsync(string deviceId, double temperature);
        Task<double?> GetLastUserSettingAsync();
        Task<string?> GetLastModeAsync();
        Task<List<SensorDataRecord>> GetRecentSensorDataAsync(int limit);
        Task<List<ModeUpdateRecord>> GetRecentModeUpdatesAsync(int limit);
        Task<List<DeviceInfo>> GetActiveDevicesAsync();
        Task<List<SensorDataRecord>> GetSensorDataByTimeRangeAsync(DateTime startTime, DateTime? endTime = null);
        
        // Device Registry methods
        Task<bool> RegisterDeviceAsync(DeviceRegistration device, bool isActive = true);
        Task<bool> UpdateDeviceAsync(string deviceId, DeviceRegistration device);
        Task<DeviceRegistration?> GetDeviceAsync(string deviceId);
        Task<List<DeviceRegistration>> GetAllDevicesAsync();
        Task<List<DeviceRegistration>> GetDevicesByTypeAsync(string deviceType);
        Task<bool> DeactivateDeviceAsync(string deviceId);
        Task<bool> UpdateDeviceLastSeenAsync(string deviceId, string? ipAddress);
        Task<SensorDataRecord?> GetLastSensorDataAsync(string deviceId);
        
        // Device-specific settings methods
        Task<bool> UpdateDeviceSetTemperatureAsync(string deviceId, double temperature, string updatedBy = "server");
        Task<double?> GetDeviceSetTemperatureAsync(string deviceId);
        Task<DeviceSettings?> GetDeviceSettingsAsync(string deviceId);
        Task<bool> UpdateDeviceSettingsAsync(string deviceId, DeviceSettings settings);
        Task<bool> UpdateDeviceModeAsync(string deviceId, string mode, string updatedBy = "server");
        Task<int> UpdateDeviceIdInAllTablesAsync(string oldDeviceId, string newDeviceId);

    }

    public class DatabaseService : IDatabaseService
    {
        private readonly ThermostatConfig _config;
        private readonly ILogger<DatabaseService> _logger;
        private readonly string _connectionString;
        private readonly string _dbPath;

        public DatabaseService(ThermostatConfig config, ILogger<DatabaseService> logger)
        {
            _config = config;
            _logger = logger;

            _dbPath = Path.Combine(AppContext.BaseDirectory, "thermostat.db");
            _connectionString = $"Data Source={_dbPath}";

            try
            {
                InitializeDatabase().Wait();
                _logger.LogInformation("SQLite database initialized at: {Path}", _dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Database initialization failed - will retry on first query");
            }
        }
        public async Task<int> UpdateDeviceIdInAllTablesAsync(string oldDeviceId, string newDeviceId)
{
    int tablesUpdated = 0;
    
    try
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        using var transaction = conn.BeginTransaction();

        try
        {
            // 1. Update sensor_data table
            var sensorDataCmd = new SqliteCommand(
                "UPDATE sensor_data SET device_id = @new WHERE device_id = @old",
                conn, transaction);
            sensorDataCmd.Parameters.AddWithValue("@new", newDeviceId);
            sensorDataCmd.Parameters.AddWithValue("@old", oldDeviceId);
            var sensorRows = await sensorDataCmd.ExecuteNonQueryAsync();
            if (sensorRows > 0)
            {
                tablesUpdated++;
                _logger.LogInformation("Updated {Count} rows in sensor_data", sensorRows);
            }

            // 2. Update mode_updates table
            var modeCmd = new SqliteCommand(
                "UPDATE mode_updates SET device_id = @new WHERE device_id = @old",
                conn, transaction);
            modeCmd.Parameters.AddWithValue("@new", newDeviceId);
            modeCmd.Parameters.AddWithValue("@old", oldDeviceId);
            var modeRows = await modeCmd.ExecuteNonQueryAsync();
            if (modeRows > 0)
            {
                tablesUpdated++;
                _logger.LogInformation("Updated {Count} rows in mode_updates", modeRows);
            }

            // 3. Update user_settings table
            var settingsCmd = new SqliteCommand(
                "UPDATE user_settings SET device_id = @new WHERE device_id = @old",
                conn, transaction);
            settingsCmd.Parameters.AddWithValue("@new", newDeviceId);
            settingsCmd.Parameters.AddWithValue("@old", oldDeviceId);
            var settingsRows = await settingsCmd.ExecuteNonQueryAsync();
            if (settingsRows > 0)
            {
                tablesUpdated++;
                _logger.LogInformation("Updated {Count} rows in user_settings", settingsRows);
            }

            // 4. Update device_settings table
            var deviceSettingsCmd = new SqliteCommand(
                "UPDATE device_settings SET device_id = @new WHERE device_id = @old",
                conn, transaction);
            deviceSettingsCmd.Parameters.AddWithValue("@new", newDeviceId);
            deviceSettingsCmd.Parameters.AddWithValue("@old", oldDeviceId);
            var deviceSettingsRows = await deviceSettingsCmd.ExecuteNonQueryAsync();
            if (deviceSettingsRows > 0)
            {
                tablesUpdated++;
                _logger.LogInformation("Updated {Count} rows in device_settings", deviceSettingsRows);
            }

            // 5. Update device_registry table (primary record)
            var registryCmd = new SqliteCommand(
                "UPDATE device_registry SET device_id = @new, device_name = @new, updated_at = datetime('now') WHERE device_id = @old",
                conn, transaction);
            registryCmd.Parameters.AddWithValue("@new", newDeviceId);
            registryCmd.Parameters.AddWithValue("@old", oldDeviceId);
            var registryRows = await registryCmd.ExecuteNonQueryAsync();
            if (registryRows > 0)
            {
                tablesUpdated++;
                _logger.LogInformation("Updated {Count} rows in device_registry", registryRows);
            }

            // Commit all changes
            transaction.Commit();
            
            _logger.LogInformation("Successfully updated device_id from '{Old}' to '{New}' in {Count} tables",
                oldDeviceId, newDeviceId, tablesUpdated);
            
            return tablesUpdated;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to update device_id - transaction rolled back");
            throw;
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error updating device_id in tables");
        return 0;
    }
}

        private async Task InitializeDatabase()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var commands = new[]
                {
                    @"CREATE TABLE IF NOT EXISTS sensor_data (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        device_id TEXT NOT NULL,
                        temperature REAL NOT NULL,
                        humidity REAL NOT NULL,
                        ip_address TEXT,
                        timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                    )",
                    @"CREATE INDEX IF NOT EXISTS idx_sensor_device_timestamp 
                      ON sensor_data(device_id, timestamp)",
                    @"CREATE INDEX IF NOT EXISTS idx_sensor_timestamp 
                      ON sensor_data(timestamp)",
                    @"CREATE TABLE IF NOT EXISTS mode_updates (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        device_id TEXT NOT NULL,
                        mode TEXT NOT NULL,
                        timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                    )",
                    @"CREATE INDEX IF NOT EXISTS idx_mode_device_timestamp 
                      ON mode_updates(device_id, timestamp)",
                    @"CREATE TABLE IF NOT EXISTS user_settings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        device_id TEXT NOT NULL,
                        temperature REAL NOT NULL,
                        timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                    )",
                    @"CREATE INDEX IF NOT EXISTS idx_settings_timestamp 
                      ON user_settings(timestamp)",
                    @"CREATE TABLE IF NOT EXISTS device_registry (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        device_id TEXT UNIQUE NOT NULL,
                        device_type TEXT NOT NULL,
                        device_name TEXT,
                        location TEXT,
                        ip_address TEXT,
                        com_port TEXT,
                        relay_port TEXT,
                        gpio_pins TEXT,
                        is_active INTEGER DEFAULT 1,
                        last_seen DATETIME,
                        registered_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                        updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
                    )",
                    @"CREATE INDEX IF NOT EXISTS idx_device_registry_device_id 
                      ON device_registry(device_id)",
                    @"CREATE INDEX IF NOT EXISTS idx_device_registry_type 
                      ON device_registry(device_type)",
                    @"CREATE INDEX IF NOT EXISTS idx_device_registry_active 
                      ON device_registry(is_active)"
                };

                foreach (var cmdText in commands)
                {
                    using var cmd = new SqliteCommand(cmdText, conn);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Create device_settings table
                await CreateDeviceSettingsTable(conn);

                _logger.LogInformation("Database tables initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
                throw;
            }
        }

        private async Task CreateDeviceSettingsTable(SqliteConnection conn)
        {
            const string createDeviceSettings = @"
                CREATE TABLE IF NOT EXISTS device_settings (
                    device_id TEXT PRIMARY KEY,
                    set_temperature REAL NOT NULL,
                    mode TEXT NOT NULL DEFAULT 'Auto',
                    last_updated TEXT NOT NULL,
                    updated_by TEXT NOT NULL,
                    FOREIGN KEY (device_id) REFERENCES device_registry(device_id)
                )";

            using var cmd = new SqliteCommand(createDeviceSettings, conn);
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Device settings table ready");
        }

        public async Task<bool> InsertSensorDataAsync(string deviceId, double temperature, double humidity, string ipAddress)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "INSERT INTO sensor_data (device_id, temperature, humidity, ip_address, timestamp) VALUES (@device, @temp, @hum, @ip, datetime('now'))",
                    conn);

                cmd.Parameters.AddWithValue("@device", deviceId);
                cmd.Parameters.AddWithValue("@temp", temperature);
                cmd.Parameters.AddWithValue("@hum", humidity);
                cmd.Parameters.AddWithValue("@ip", ipAddress ?? (object)DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
                
                // Update device last_seen timestamp
                await UpdateDeviceLastSeenAsync(deviceId, ipAddress);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert sensor data");
                return false;
            }
        }

        public async Task<bool> InsertModeUpdateAsync(string deviceId, string mode)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "INSERT INTO mode_updates (device_id, mode, timestamp) VALUES (@device, @mode, datetime('now'))",
                    conn);

                cmd.Parameters.AddWithValue("@device", deviceId);
                cmd.Parameters.AddWithValue("@mode", mode);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert mode update");
                return false;
            }
        }

        public async Task<bool> InsertUserSettingAsync(string deviceId, double temperature)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "INSERT INTO user_settings (device_id, temperature, timestamp) VALUES (@device, @temp, datetime('now'))",
                    conn);

                cmd.Parameters.AddWithValue("@device", deviceId);
                cmd.Parameters.AddWithValue("@temp", temperature);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert user setting");
                return false;
            }
        }

        public async Task<double?> GetLastUserSettingAsync()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "SELECT temperature FROM user_settings ORDER BY timestamp DESC LIMIT 1",
                    conn);

                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToDouble(result);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get last user setting");
                return null;
            }
        }

        public async Task<string?> GetLastModeAsync()
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "SELECT mode FROM mode_updates ORDER BY timestamp DESC LIMIT 1",
                    conn);

                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return result.ToString();
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get last mode");
                return null;
            }
        }

        public async Task<List<SensorDataRecord>> GetRecentSensorDataAsync(int limit)
        {
            var records = new List<SensorDataRecord>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "SELECT device_id, temperature, humidity, ip_address, timestamp FROM sensor_data ORDER BY timestamp DESC LIMIT @limit",
                    conn);

                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    records.Add(new SensorDataRecord
                    {
                        DeviceId = reader.GetString(0),
                        Temperature = reader.GetDouble(1),
                        Humidity = reader.GetDouble(2),
                        IpAddress = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Timestamp = reader.GetDateTime(4)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get recent sensor data");
            }
            return records;
        }

        public async Task<List<SensorDataRecord>> GetSensorDataByTimeRangeAsync(DateTime startTime, DateTime? endTime = null)
        {
            var records = new List<SensorDataRecord>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var endTimeValue = endTime ?? DateTime.UtcNow;
                
                var cmd = new SqliteCommand(
                    @"SELECT device_id, temperature, humidity, ip_address, timestamp 
                      FROM sensor_data 
                      WHERE timestamp >= @start AND timestamp <= @end
                      ORDER BY timestamp DESC",
                    conn);

                cmd.Parameters.AddWithValue("@start", startTime.ToString("yyyy-MM-dd HH:mm:ss"));
                cmd.Parameters.AddWithValue("@end", endTimeValue.ToString("yyyy-MM-dd HH:mm:ss"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    records.Add(new SensorDataRecord
                    {
                        DeviceId = reader.GetString(0),
                        Temperature = reader.GetDouble(1),
                        Humidity = reader.GetDouble(2),
                        IpAddress = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Timestamp = reader.GetDateTime(4)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get sensor data by time range");
            }
            return records;
        }

        public async Task<List<ModeUpdateRecord>> GetRecentModeUpdatesAsync(int limit)
        {
            var records = new List<ModeUpdateRecord>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "SELECT device_id, mode, timestamp FROM mode_updates ORDER BY timestamp DESC LIMIT @limit",
                    conn);

                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    records.Add(new ModeUpdateRecord
                    {
                        DeviceId = reader.GetString(0),
                        Mode = reader.GetString(1),
                        Timestamp = reader.GetDateTime(2)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get recent mode updates");
            }
            return records;
        }

        public async Task<List<DeviceInfo>> GetActiveDevicesAsync()
        {
            var devices = new List<DeviceInfo>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    @"SELECT dr.device_id, dr.device_type, dr.device_name, dr.location, 
                             sd.temperature AS last_temperature, sd.humidity AS last_humidity, sd.timestamp AS last_seen
                      FROM device_registry dr
                      LEFT JOIN (SELECT device_id, temperature, humidity, timestamp,
                                        ROW_NUMBER() OVER (PARTITION BY device_id ORDER BY timestamp DESC) AS rn
                                 FROM sensor_data) sd ON dr.device_id = sd.device_id AND sd.rn = 1
                      WHERE dr.is_active = 1
                      ORDER BY dr.updated_at DESC",
                    conn);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    devices.Add(new DeviceInfo
                    {
                        DeviceId = reader.GetString(0),
                        DeviceType = reader.GetString(1),
                        DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Location = reader.IsDBNull(3) ? null : reader.GetString(3),
                        LastTemperature = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                        LastHumidity = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                        LastSeen = reader.IsDBNull(6) ? DateTime.MinValue : reader.GetDateTime(6)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get active devices");
            }
            return devices;
        }

        public async Task<bool> RegisterDeviceAsync(DeviceRegistration device, bool isActive = true)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    INSERT OR REPLACE INTO device_registry (
                        device_id, device_type, device_name, location, ip_address, 
                        com_port, relay_port, gpio_pins, is_active, last_seen,
                        registered_at, updated_at
                    ) VALUES (
                        @device_id, @device_type, @device_name, @location, @ip, 
                        @com_port, @relay_port, @gpio_pins, @is_active, datetime('now'),
                        datetime('now'), datetime('now')
                    )",
                    conn);

                cmd.Parameters.AddWithValue("@device_id", device.DeviceId);
                cmd.Parameters.AddWithValue("@device_type", device.DeviceType);
                cmd.Parameters.AddWithValue("@device_name", device.DeviceName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@location", device.Location ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ip", device.IpAddress ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@com_port", device.ComPort ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@relay_port", device.RelayPort ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@gpio_pins", device.GpioPins ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@is_active", isActive ? 1 : 0);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register device");
                return false;
            }
        }

        public async Task<bool> UpdateDeviceAsync(string deviceId, DeviceRegistration device)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    UPDATE device_registry SET
                        device_type = @device_type,
                        device_name = @device_name,
                        location = @location,
                        ip_address = COALESCE(@ip, ip_address),
                        com_port = COALESCE(@com_port, com_port),
                        relay_port = COALESCE(@relay_port, relay_port),
                        gpio_pins = COALESCE(@gpio_pins, gpio_pins),
                        updated_at = datetime('now')
                    WHERE device_id = @device_id",
                    conn);

                cmd.Parameters.AddWithValue("@device_id", deviceId);
                cmd.Parameters.AddWithValue("@device_type", device.DeviceType);
                cmd.Parameters.AddWithValue("@device_name", device.DeviceName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@location", device.Location ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ip", device.IpAddress ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@com_port", device.ComPort ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@relay_port", device.RelayPort ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@gpio_pins", device.GpioPins ?? (object)DBNull.Value);

                var rowsAffected = await cmd.ExecuteNonQueryAsync();
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update device");
                return false;
            }
        }

        public async Task<DeviceRegistration?> GetDeviceAsync(string deviceId)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    SELECT device_id, device_type, device_name, location, ip_address, com_port, relay_port, gpio_pins, is_active, last_seen, registered_at, updated_at
                    FROM device_registry WHERE device_id = @device_id",
                    conn);

                cmd.Parameters.AddWithValue("@device_id", deviceId);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new DeviceRegistration
                    {
                        DeviceId = reader.GetString(0),
                        DeviceType = reader.GetString(1),
                        DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Location = reader.IsDBNull(3) ? null : reader.GetString(3),
                        IpAddress = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ComPort = reader.IsDBNull(5) ? null : reader.GetString(5),
                        RelayPort = reader.IsDBNull(6) ? null : reader.GetString(6),
                        GpioPins = reader.IsDBNull(7) ? null : reader.GetString(7),
                        IsActive = reader.GetInt32(8) == 1,
                        LastSeen = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                        RegisteredAt = reader.GetDateTime(10),
                        UpdatedAt = reader.GetDateTime(11)
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get device");
                return null;
            }
        }

        public async Task<List<DeviceRegistration>> GetAllDevicesAsync()
        {
            var devices = new List<DeviceRegistration>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    SELECT device_id, device_type, device_name, location, ip_address, com_port, relay_port, gpio_pins, is_active, last_seen, registered_at, updated_at
                    FROM device_registry ORDER BY updated_at DESC",
                    conn);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    devices.Add(new DeviceRegistration
                    {
                        DeviceId = reader.GetString(0),
                        DeviceType = reader.GetString(1),
                        DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Location = reader.IsDBNull(3) ? null : reader.GetString(3),
                        IpAddress = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ComPort = reader.IsDBNull(5) ? null : reader.GetString(5),
                        RelayPort = reader.IsDBNull(6) ? null : reader.GetString(6),
                        GpioPins = reader.IsDBNull(7) ? null : reader.GetString(7),
                        IsActive = reader.GetInt32(8) == 1,
                        LastSeen = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                        RegisteredAt = reader.GetDateTime(10),
                        UpdatedAt = reader.GetDateTime(11)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all devices");
            }
            return devices;
        }

        public async Task<List<DeviceRegistration>> GetDevicesByTypeAsync(string deviceType)
        {
            var devices = new List<DeviceRegistration>();
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    SELECT device_id, device_type, device_name, location, ip_address, com_port, relay_port, gpio_pins, is_active, last_seen, registered_at, updated_at
                    FROM device_registry WHERE device_type = @device_type ORDER BY updated_at DESC",
                    conn);

                cmd.Parameters.AddWithValue("@device_type", deviceType);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    devices.Add(new DeviceRegistration
                    {
                        DeviceId = reader.GetString(0),
                        DeviceType = reader.GetString(1),
                        DeviceName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Location = reader.IsDBNull(3) ? null : reader.GetString(3),
                        IpAddress = reader.IsDBNull(4) ? null : reader.GetString(4),
                        ComPort = reader.IsDBNull(5) ? null : reader.GetString(5),
                        RelayPort = reader.IsDBNull(6) ? null : reader.GetString(6),
                        GpioPins = reader.IsDBNull(7) ? null : reader.GetString(7),
                        IsActive = reader.GetInt32(8) == 1,
                        LastSeen = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                        RegisteredAt = reader.GetDateTime(10),
                        UpdatedAt = reader.GetDateTime(11)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get devices by type");
            }
            return devices;
        }

        public async Task<bool> DeactivateDeviceAsync(string deviceId)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    UPDATE device_registry SET is_active = 0, updated_at = datetime('now')
                    WHERE device_id = @device_id",
                    conn);

                cmd.Parameters.AddWithValue("@device_id", deviceId);

                var rowsAffected = await cmd.ExecuteNonQueryAsync();
                return rowsAffected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate device");
                return false;
            }
        }

        public async Task<bool> UpdateDeviceLastSeenAsync(string deviceId, string? ipAddress)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(@"
                    UPDATE device_registry SET 
                        last_seen = datetime('now'),
                        ip_address = COALESCE(@ip, ip_address)
                    WHERE device_id = @device_id",
                    conn);

                cmd.Parameters.AddWithValue("@device_id", deviceId);
                cmd.Parameters.AddWithValue("@ip", ipAddress ?? (object)DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<SensorDataRecord?> GetLastSensorDataAsync(string deviceId)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                var cmd = new SqliteCommand(
                    "SELECT temperature, humidity, timestamp " +
                    "FROM sensor_data " +
                    "WHERE device_id = @device " +
                    "ORDER BY timestamp DESC LIMIT 1",
                    conn);

                cmd.Parameters.AddWithValue("@device", deviceId);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new SensorDataRecord
                    {
                        Temperature = reader.GetDouble(0),
                        Humidity = reader.GetDouble(1),
                        Timestamp = reader.GetDateTime(2)
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get last sensor data for {DeviceId}", deviceId);
                return null;
            }
        }

        // ===== DEVICE-SPECIFIC SETTINGS METHODS =====

        public async Task<bool> UpdateDeviceSetTemperatureAsync(string deviceId, double temperature, string updatedBy = "server")
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO device_settings (device_id, set_temperature, mode, last_updated, updated_by)
                    VALUES (@device_id, @temperature, 'Auto', @timestamp, @updated_by)
                    ON CONFLICT(device_id) DO UPDATE SET
                        set_temperature = @temperature,
                        last_updated = @timestamp,
                        updated_by = @updated_by";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@device_id", deviceId);
                cmd.Parameters.AddWithValue("@temperature", temperature);
                cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@updated_by", updatedBy);

                await cmd.ExecuteNonQueryAsync();
                _logger.LogInformation("Updated set temperature for {Device}: {Temp}°F (by: {By})", 
                    deviceId, temperature, updatedBy);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update device set temperature");
                return false;
            }
        }

        public async Task<double?> GetDeviceSetTemperatureAsync(string deviceId)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT set_temperature 
                    FROM device_settings 
                    WHERE device_id = @device_id";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@device_id", deviceId);

                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToDouble(result);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get device set temperature");
                return null;
            }
        }

        public async Task<DeviceSettings?> GetDeviceSettingsAsync(string deviceId)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT device_id, set_temperature, mode, last_updated, updated_by
                    FROM device_settings 
                    WHERE device_id = @device_id";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@device_id", deviceId);

                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new DeviceSettings
                    {
                        DeviceId = reader.GetString(0),
                        SetTemperature = reader.GetDouble(1),
                        Mode = reader.GetString(2),
                        LastUpdated = DateTime.Parse(reader.GetString(3)),
                        UpdatedBy = reader.GetString(4)
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get device settings");
                return null;
            }
        }

        public async Task<bool> UpdateDeviceSettingsAsync(string deviceId, DeviceSettings settings)
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO device_settings (device_id, set_temperature, mode, last_updated, updated_by)
                    VALUES (@device_id, @temperature, @mode, @timestamp, @updated_by)
                    ON CONFLICT(device_id) DO UPDATE SET
                        set_temperature = @temperature,
                        mode = @mode,
                        last_updated = @timestamp,
                        updated_by = @updated_by";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@device_id", deviceId);
                cmd.Parameters.AddWithValue("@temperature", settings.SetTemperature);
                cmd.Parameters.AddWithValue("@mode", settings.Mode);
                cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@updated_by", settings.UpdatedBy);

                await cmd.ExecuteNonQueryAsync();
                _logger.LogInformation("Updated settings for {Device}: {Temp}°F, Mode: {Mode}", 
                    deviceId, settings.SetTemperature, settings.Mode);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update device settings");
                return false;
            }
        }

        public async Task<bool> UpdateDeviceModeAsync(string deviceId, string mode, string updatedBy = "server")
        {
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO device_settings (device_id, set_temperature, mode, last_updated, updated_by)
                    VALUES (@device_id, 70.0, @mode, @timestamp, @updated_by)
                    ON CONFLICT(device_id) DO UPDATE SET
                        mode = @mode,
                        last_updated = @timestamp,
                        updated_by = @updated_by";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@device_id", deviceId);
                cmd.Parameters.AddWithValue("@mode", mode);
                cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@updated_by", updatedBy);

                await cmd.ExecuteNonQueryAsync();
                _logger.LogInformation("Updated mode for {Device}: {Mode} (by: {By})", 
                    deviceId, mode, updatedBy);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update device mode");
                return false;
            }
        }
    }

    // Data models
    public class SensorDataRecord
    {
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";
        
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
        
        [JsonPropertyName("humidity")]
        public double Humidity { get; set; }
        
        [JsonPropertyName("ip_address")]
        public string? IpAddress { get; set; }
        
        [JsonPropertyName("timestamp")]
        [JsonConverter(typeof(MySqlDateTimeConverter))]
        public DateTime Timestamp { get; set; }
    }

    public class ModeUpdateRecord
    {
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";
        
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "";
        
        [JsonPropertyName("timestamp")]
        [JsonConverter(typeof(GmtDateTimeConverter))]
        public DateTime Timestamp { get; set; }
    }

    public class DeviceInfo
    {
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";
        
        [JsonPropertyName("last_temperature")]
        public double LastTemperature { get; set; }
        
        [JsonPropertyName("last_humidity")]
        public double LastHumidity { get; set; }
        
        [JsonPropertyName("last_seen")]
        [JsonConverter(typeof(MySqlDateTimeConverter))]
        public DateTime LastSeen { get; set; }

        [JsonPropertyName("device_type")]
        public string DeviceType { get; set; } = "Unknown";

        [JsonPropertyName("device_name")]
        public string? DeviceName { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }
    }

    public class DeviceRegistration
    {
        [JsonPropertyName("device_id")]
        public string DeviceId { get; set; } = "";

        [JsonPropertyName("device_type")]
        public string DeviceType { get; set; } = "";

        [JsonPropertyName("device_name")]
        public string? DeviceName { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("ip_address")]
        public string? IpAddress { get; set; }

        [JsonPropertyName("com_port")]
        public string? ComPort { get; set; }

        [JsonPropertyName("relay_port")]
        public string? RelayPort { get; set; }

        [JsonPropertyName("gpio_pins")]
        public string? GpioPins { get; set; }

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; } = true;

        [JsonPropertyName("last_seen")]
        [JsonConverter(typeof(MySqlDateTimeConverter))]
        public DateTime? LastSeen { get; set; }

        [JsonPropertyName("registered_at")]
        [JsonConverter(typeof(MySqlDateTimeConverter))]
        public DateTime RegisteredAt { get; set; }

        [JsonPropertyName("updated_at")]
        [JsonConverter(typeof(MySqlDateTimeConverter))]
        public DateTime UpdatedAt { get; set; }
    }
}