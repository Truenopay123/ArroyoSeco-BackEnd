using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Infrastructure.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace arroyoSeco.Infrastructure.Services;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtOptions _opts;

    public JwtTokenGenerator(IOptions<JwtOptions> opts) => _opts = opts.Value;

    public string Generate(string userId, string email, IEnumerable<string> roles, bool requiereCambioPassword = false, DateTime? expires = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, email),
            new Claim(ClaimTypes.Email, email),
            new Claim("RequiereCambioPassword", requiereCambioPassword.ToString())
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires ?? DateTime.UtcNow.AddMinutes(_opts.ExpirationMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Genera un token temporal de corta duración para el flujo de verificación facial.
    /// Expira en 3 minutos y lleva el claim "purpose" = "face-2fa".
    /// </summary>
    public string GenerateTempToken(string userId, string email)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, email),
            new Claim("purpose", "face-2fa")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(3),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Valida un token temporal de 2FA facial. Retorna null si es inválido o expirado.
    /// </summary>
    public ClaimsPrincipal? ValidateTempToken(string token)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Key));
        var handler = new JwtSecurityTokenHandler();

        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _opts.Issuer,
                ValidAudience = _opts.Audience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.Zero
            }, out _);

            // Verificar que sea un token de propósito facial
            var purpose = principal.FindFirstValue("purpose");
            if (purpose != "face-2fa") return null;

            return principal;
        }
        catch
        {
            return null;
        }
    }
}