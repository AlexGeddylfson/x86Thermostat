public class SensorDataService : ISensorDataService
{
    private float currentTemperature;
    public float CurrentTemperature
    {
        get => currentTemperature;
        set
        {
            currentTemperature = value;
            OnDataUpdated();
        }
    }

    private float currentHumidity;
    public float CurrentHumidity
    {
        get => currentHumidity;
        set
        {
            currentHumidity = value;
            OnDataUpdated();
        }
    }

    public event EventHandler DataUpdated;

    protected virtual void OnDataUpdated()
    {
        DataUpdated?.Invoke(this, EventArgs.Empty);
    }
}
