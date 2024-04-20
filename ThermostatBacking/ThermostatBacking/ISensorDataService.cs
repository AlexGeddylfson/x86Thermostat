public interface ISensorDataService
{
    float CurrentTemperature { get; set; }
    float CurrentHumidity { get; set; }
    event EventHandler DataUpdated; // Optional: For event-driven updates
}
