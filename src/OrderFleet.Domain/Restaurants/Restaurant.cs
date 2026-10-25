using OrderFleet.Domain.Geo;

namespace OrderFleet.Domain.Restaurants;

public sealed class Restaurant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public bool IsActive { get; private set; }

    private Restaurant() { }

    public static Restaurant Create(string name, Coordinates location)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException("Restaurant name is required and limited to 200 characters.", nameof(name));
        return new()
        {
            Id = Guid.NewGuid(), Name = name.Trim(), Latitude = location.Latitude,
            Longitude = location.Longitude, IsActive = true
        };
    }

    public void SetActive(bool active) => IsActive = active;
}
