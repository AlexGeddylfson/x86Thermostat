using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Data;
using Microsoft.AspNetCore.Internal;

public class ServerController
{
    public void Start()
    {
        Console.WriteLine("Starting server...");
        // Additional server starting logic would be added here if needed
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            // Add DbContext with SQLite
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlite("Data Source=mydatabase.db"));
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            // Middleware configuration goes here
            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", async context =>
                {
                    await context.Response.WriteAsync("Hello World!");
                });
            });
        }
    }

    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<SensorData> SensorData { get; set; }
        public DbSet<UserSettings> UserSettings { get; set; }
        public DbSet<OutsideTemperature> OutsideTemperature { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SensorData>().ToTable("sensor_data");
            modelBuilder.Entity<UserSettings>().ToTable("user_settings");
            modelBuilder.Entity<OutsideTemperature>().ToTable("outside_temperature");

            modelBuilder.Entity<SensorData>().Property(p => p.IpAddress).HasMaxLength(15);
            modelBuilder.Entity<UserSettings>().Property(p => p.DeviceId).HasMaxLength(255);
            modelBuilder.Entity<OutsideTemperature>().Property(p => p.Temperature).HasColumnType("DECIMAL(5, 2)");
        }
    }

    public class SensorData
    {
        public int Id { get; set; }
        public string DeviceId { get; set; }
        public decimal Temperature { get; set; }
        public decimal Humidity { get; set; }
        public DateTime Timestamp { get; set; }
        public string IpAddress { get; set; }
    }

    public class UserSettings
    {
        public int Id { get; set; }
        public string DeviceId { get; set; }
        public decimal TargetTemperature { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class OutsideTemperature
    {
        public int Id { get; set; }
        public decimal Temperature { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public static void InitializeDatabase()
    {
        using (var connection = new SqliteConnection("Data Source=mydatabase.db"))
        {
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText =
            @"
CREATE TABLE sensor_data (
    id INT AUTO_INCREMENT PRIMARY KEY,
    device_id VARCHAR(255),
    temperature DECIMAL(5, 2),
    humidity DECIMAL(5, 2),
    timestamp TIMESTAMP,
    ip_address VARCHAR(15)
);

CREATE TABLE user_settings (
    id INT AUTO_INCREMENT PRIMARY KEY,
    device_id VARCHAR(255),
    target_temperature DECIMAL(5, 2),
    timestamp TIMESTAMP
);

CREATE TABLE outside_temperature (
    id INT AUTO_INCREMENT PRIMARY KEY,
    temperature DECIMAL(5, 2),
    timestamp TIMESTAMP
);
            ";
            command.ExecuteNonQuery();
        }
    }
}
