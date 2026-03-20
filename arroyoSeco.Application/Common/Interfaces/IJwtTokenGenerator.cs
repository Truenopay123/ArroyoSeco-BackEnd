using System.Security.Claims;

namespace arroyoSeco.Application.Common.Interfaces;

public interface IJwtTokenGenerator
{
    string Generate(string userId, string email, IEnumerable<string> roles, bool requiereCambioPassword = false, DateTime? expires = null);

    // Token temporal para flujo de 2FA facial (corta duración, sin roles)
    string GenerateTempToken(string userId, string email);

    // Valida el token temporal y retorna las claims si es válido
    ClaimsPrincipal? ValidateTempToken(string token);
}