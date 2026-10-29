namespace Inventory.Api.Models;

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Stored lower-cased so lookups and the unique index are case-insensitive.</summary>
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
