namespace Inventory.Api.Contracts;

public record RegisterRequest(string Name, string Email, string Password);

public record LoginRequest(string Email, string Password);

public record UserDto(int Id, string Name, string Email);

public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);
