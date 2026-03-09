using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Usuarios;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IJwtTokenGenerator _token;
    private readonly IAppDbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;

    public AuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IJwtTokenGenerator token,
        IAppDbContext db,
        IEmailService email,
        IConfiguration configuration)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _token = token;
        _db = db;
        _email = email;
        _configuration = configuration;
    }

    public record RegisterDto(
        string Email,
        string Password,
        string? Role,
        int? TipoOferente,
        // Campos demográficos opcionales
        string? Sexo,
        DateTime? FechaNacimiento,
        string? LugarOrigen,
        bool AceptaPoliticaDatos = false);

    public record LoginDto(string Email, string Password);
    public record VerificarTotpDto(string Email, string Codigo);
    public record HabilitarTotpDto(string Codigo);
    public record HabilitarTotpRegistroDto(string Email, string Codigo);
    public record CambiarPasswordDto(string PasswordActual, string PasswordNueva);
    public record ForgotPasswordDto(string Email);
    public record ResetPasswordDto(string Email, string Token, string PasswordNueva);
    public record ReenviarConfirmacionDto(string Email);
    public record ActualizarDemografiaDto(string? Sexo, DateTime? FechaNacimiento, string? LugarOrigen);

    // ── Registro ────────────────────────────────────────────────────────────

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto)
    {
        var passwordValidation = ValidarPasswordFuerte(dto.Password);
        if (!passwordValidation.ok)
            return BadRequest(new { message = passwordValidation.message });

        if (!dto.AceptaPoliticaDatos)
            return BadRequest(new { message = "Debes aceptar la política de privacidad para registrar tus datos." });

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            EmailConfirmed = false,
            Sexo = dto.Sexo,
            FechaNacimiento = NormalizeUtc(dto.FechaNacimiento),
            LugarOrigen = dto.LugarOrigen
        };

        var result = await _userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded) return BadRequest(new { errors = result.Errors });

        // Evitar escalamiento por registro público
        var requestedRole = string.IsNullOrWhiteSpace(dto.Role) ? "Cliente" : dto.Role!;
        var role = requestedRole.Equals("Oferente", StringComparison.OrdinalIgnoreCase) ? "Oferente" : "Cliente";
        await _userManager.AddToRoleAsync(user, role);

        if (role == "Oferente")
        {
            var tipoOferente = dto.TipoOferente.HasValue
                ? (Domain.Entities.Enums.TipoOferente)dto.TipoOferente.Value
                : Domain.Entities.Enums.TipoOferente.Ambos;

            var oferente = new Oferente
            {
                Id = user.Id,
                Nombre = dto.Email.Split('@')[0],
                NumeroAlojamientos = 0,
                Tipo = tipoOferente
            };
            _db.Oferentes.Add(oferente);
            await _db.SaveChangesAsync();
        }

        if (role == "Cliente")
        {
            var setup = await EnsureTotpSetupAsync(user);
            var confirmationSentCliente = await SendConfirmationEmailAsync(user);

            return Ok(new
            {
                message = confirmationSentCliente
                    ? "Registro exitoso. Configura tu autenticación de dos pasos y luego confirma tu correo."
                    : "Registro exitoso. Configura tu autenticación de dos pasos, pero no se pudo enviar el correo de confirmación.",
                confirmationEmailSent = confirmationSentCliente,
                requiresEmailConfirmation = true,
                requiresTwoFactorSetup = true,
                key = setup.key,
                qrUri = setup.qrUri,
                email = user.Email
            });
        }

        var confirmationSent = await SendConfirmationEmailAsync(user);

        return Ok(new
        {
            message = confirmationSent
                ? "Registro exitoso. Revisa tu correo para confirmar tu cuenta."
                : "Registro exitoso, pero no se pudo enviar el correo de confirmación. Contacta al administrador.",
            confirmationEmailSent = confirmationSent,
            requiresEmailConfirmation = true
        });
    }

    // ── Login con soporte 2FA ──────────────────────────────────────────────

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null) return Unauthorized(new { message = "Credenciales inválidas." });

        if (!user.EmailConfirmed)
        {
            return Unauthorized(new
            {
                message = "Debes confirmar tu correo antes de iniciar sesión.",
                requiresEmailConfirmation = true
            });
        }

        // Verificar si la cuenta está bloqueada
        if (await _userManager.IsLockedOutAsync(user))
        {
            var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
            return Unauthorized(new
            {
                message = $"Cuenta bloqueada por demasiados intentos fallidos. Intenta de nuevo a las {lockoutEnd?.ToLocalTime():HH:mm}.",
                accountLocked = true
            });
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, dto.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return Unauthorized(new
            {
                message = "Cuenta bloqueada por 15 minutos tras demasiados intentos fallidos.",
                accountLocked = true
            });
        }

        if (!result.Succeeded)
        {
            var failed = await _userManager.GetAccessFailedCountAsync(user);
            var remainingAttempts = Math.Max(0, _userManager.Options.Lockout.MaxFailedAccessAttempts - failed);
            return Unauthorized(new
            {
                message = "Credenciales inválidas.",
                remainingAttempts
            });
        }

        // Si el usuario tiene 2FA habilitado, pedir código TOTP
        if (user.TwoFactorEnabled)
        {
            return Ok(new
            {
                requiresTwoFactor = true,
                email = user.Email
            });
        }

        // Login completo — emitir JWT
        if (!user.FechaPrimerLogin.HasValue)
        {
            user.FechaPrimerLogin = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
        }

        var roles = await _userManager.GetRolesAsync(user);
        var jwt = _token.Generate(user.Id, user.Email!, roles, user.RequiereCambioPassword);
        return Ok(new { token = jwt });
    }

    // ── Verificar código TOTP durante el login ────────────────────────────

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("2fa/verify-login")]
    public async Task<IActionResult> VerifyTotpLogin([FromBody] VerificarTotpDto dto)
    {
        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null || !user.TwoFactorEnabled) return BadRequest(new { message = "Solicitud inválida." });

        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            dto.Codigo);

        if (!valid) return BadRequest(new { message = "Código incorrecto o expirado." });

        if (!user.FechaPrimerLogin.HasValue)
        {
            user.FechaPrimerLogin = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
        }

        var roles = await _userManager.GetRolesAsync(user);
        var jwt = _token.Generate(user.Id, user.Email!, roles, user.RequiereCambioPassword);
        return Ok(new { token = jwt });
    }

    // ── 2FA Habilitar en registro de cliente ─────────────────────────────

    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    [HttpPost("2fa/enable-register")]
    public async Task<IActionResult> Habilitar2FARegistro([FromBody] HabilitarTotpRegistroDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Codigo))
            return BadRequest(new { message = "Solicitud inválida." });

        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null) return BadRequest(new { message = "Solicitud inválida." });

        var esCliente = await _userManager.IsInRoleAsync(user, "Cliente");
        if (!esCliente) return BadRequest(new { message = "Solo aplica para registro de clientes." });

        if (user.TwoFactorEnabled)
            return Ok(new { message = "La autenticación en dos pasos ya está habilitada." });

        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            dto.Codigo);

        if (!valid) return BadRequest(new { message = "Código TOTP incorrecto. Verifica que tu aplicación esté sincronizada." });

        await _userManager.SetTwoFactorEnabledAsync(user, true);

        var confirmationSent = false;
        if (!user.EmailConfirmed)
        {
            confirmationSent = await SendConfirmationEmailAsync(user);
        }

        return Ok(new
        {
            message = confirmationSent
                ? "Autenticación en dos pasos habilitada. Te enviamos un nuevo enlace de confirmación a tu correo."
                : "Autenticación en dos pasos habilitada exitosamente.",
            confirmationEmailSent = confirmationSent
        });
    }

    // ── 2FA Setup: obtener clave + URI para QR ────────────────────────────

    [Authorize]
    [HttpGet("2fa/setup")]
    public async Task<IActionResult> ObtenerConfiguracion2FA()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var setup = await EnsureTotpSetupAsync(user);

        return Ok(new
        {
            key = setup.key,
            qrUri = setup.qrUri,
            habilitado = user.TwoFactorEnabled
        });
    }

    // ── 2FA Habilitar ─────────────────────────────────────────────────────

    [Authorize]
    [HttpPost("2fa/enable")]
    public async Task<IActionResult> Habilitar2FA([FromBody] HabilitarTotpDto dto)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            dto.Codigo);

        if (!valid) return BadRequest(new { message = "Código TOTP incorrecto. Verifica que tu aplicación esté sincronizada." });

        await _userManager.SetTwoFactorEnabledAsync(user, true);
        return Ok(new { message = "Autenticación en dos pasos habilitada exitosamente." });
    }

    // ── 2FA Deshabilitar ──────────────────────────────────────────────────

    [Authorize]
    [HttpPost("2fa/disable")]
    public async Task<IActionResult> Deshabilitar2FA()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        await _userManager.SetTwoFactorEnabledAsync(user, false);
        await _userManager.ResetAuthenticatorKeyAsync(user);
        return Ok(new { message = "Autenticación en dos pasos deshabilitada." });
    }

    // ── Perfil ────────────────────────────────────────────────────────────

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();
        var roles = await _userManager.GetRolesAsync(user);
        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            roles,
            sexo = user.Sexo,
            fechaNacimiento = user.FechaNacimiento,
            lugarOrigen = user.LugarOrigen,
            twoFactorEnabled = user.TwoFactorEnabled
        });
    }

    // ── Actualizar datos demográficos ─────────────────────────────────────

    [Authorize]
    [HttpPut("demografia")]
    public async Task<IActionResult> ActualizarDemografia([FromBody] ActualizarDemografiaDto dto)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        user.Sexo = dto.Sexo;
        user.FechaNacimiento = NormalizeUtc(dto.FechaNacimiento);
        user.LugarOrigen = dto.LugarOrigen;
        await _userManager.UpdateAsync(user);

        return Ok(new { message = "Datos actualizados." });
    }

    // ── Cambiar contraseña ────────────────────────────────────────────────

    [Authorize]
    [HttpPost("cambiar-password")]
    public async Task<IActionResult> CambiarPassword([FromBody] CambiarPasswordDto dto)
    {
        var passwordValidation = ValidarPasswordFuerte(dto.PasswordNueva);
        if (!passwordValidation.ok)
            return BadRequest(new { message = passwordValidation.message });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound();

        var result = await _userManager.ChangePasswordAsync(user, dto.PasswordActual, dto.PasswordNueva);
        if (!result.Succeeded)
            return BadRequest(new { message = "Contraseña actual incorrecta o la nueva no cumple los requisitos.", errors = result.Errors });

        if (user.RequiereCambioPassword)
        {
            user.RequiereCambioPassword = false;
            await _userManager.UpdateAsync(user);
        }

        return Ok(new { message = "Contraseña actualizada exitosamente." });
    }

    // ── Confirmación de correo ────────────────────────────────────────────

    [AllowAnonymous]
    [HttpGet("confirm-email")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] string email, [FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return BadRequest(new { message = "Parámetros inválidos" });

        var user = await _userManager.FindByEmailAsync(email);
        if (user is null) return BadRequest(new { message = "Usuario no encontrado" });

        string decodedToken;
        try { decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token)); }
        catch { return BadRequest(new { message = "Token inválido" }); }

        var result = await _userManager.ConfirmEmailAsync(user, decodedToken);
        if (!result.Succeeded)
            return BadRequest(new { message = "No se pudo confirmar el correo", errors = result.Errors });

        return Ok(new { message = "Correo confirmado exitosamente" });
    }

    [AllowAnonymous]
    [HttpPost("reenviar-confirmacion")]
    public async Task<IActionResult> ReenviarConfirmacion([FromBody] ReenviarConfirmacionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email))
            return Ok(new { message = "Si el correo existe, se envió un enlace de confirmación" });

        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is not null && !user.EmailConfirmed)
            await SendConfirmationEmailAsync(user);

        return Ok(new { message = "Si el correo existe, se envió un enlace de confirmación" });
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email))
            return Ok(new { message = "Si el correo existe, se envió un enlace de restablecimiento" });

        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is not null)
        {
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var frontendBaseUrl = _configuration["AppUrls:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:4200";
            var resetUrl = $"{frontendBaseUrl}/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(encodedToken)}";

            var html = $@"<h2>Restablecer contraseña</h2>
<p>Recibimos una solicitud para cambiar tu contraseña en Arroyo Seco.</p>
<p><a href='{resetUrl}'>Haz clic aquí para restablecer tu contraseña</a></p>
<p>Este enlace expira en 1 hora.</p>
<p>Si no solicitaste este cambio, ignora este correo.</p>";

            var sent = await _email.SendEmailAsync(user.Email!, "Restablecer contraseña - Arroyo Seco", html);
            if (!sent)
                return StatusCode(503, new { message = "No se pudo enviar el correo de restablecimiento." });
        }

        return Ok(new { message = "Si el correo existe, se envió un enlace de restablecimiento" });
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(dto.PasswordNueva))
            return BadRequest(new { message = "Parámetros inválidos" });

        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null)
            return BadRequest(new { message = "No se pudo restablecer la contraseña" });

        string decodedToken;
        try { decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(dto.Token)); }
        catch { return BadRequest(new { message = "Token inválido" }); }

        var passwordValidation = ValidarPasswordFuerte(dto.PasswordNueva);
        if (!passwordValidation.ok)
            return BadRequest(new { message = passwordValidation.message });

        var result = await _userManager.ResetPasswordAsync(user, decodedToken, dto.PasswordNueva);
        if (!result.Succeeded)
            return BadRequest(new { message = "No se pudo restablecer la contraseña", errors = result.Errors });

        return Ok(new { message = "Contraseña restablecida exitosamente" });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<bool> SendConfirmationEmailAsync(ApplicationUser user)
    {
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var frontendBaseUrl = _configuration["AppUrls:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:4200";
        var confirmUrl = $"{frontendBaseUrl}/confirm-email?email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(encodedToken)}";

        var html = $@"<h2>Confirma tu correo</h2>
<p>Gracias por registrarte en Arroyo Seco.</p>
<p><a href='{confirmUrl}'>Haz clic aquí para confirmar tu cuenta</a></p>
<p>Si no creaste esta cuenta, ignora este correo.</p>";

        return await _email.SendEmailAsync(user.Email!, "Confirma tu correo - Arroyo Seco", html);
    }

    private static (bool ok, string message) ValidarPasswordFuerte(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return (false, "La contraseña es obligatoria.");

        if (password.Length < 8)
            return (false, "La contraseña debe tener al menos 8 caracteres.");

        if (!Regex.IsMatch(password, "[A-Z]"))
            return (false, "La contraseña debe incluir al menos una mayúscula.");

        if (!Regex.IsMatch(password, "[0-9]"))
            return (false, "La contraseña debe incluir al menos un número.");

        if (!Regex.IsMatch(password, "[^a-zA-Z0-9]"))
            return (false, "La contraseña debe incluir al menos un símbolo especial.");

        return (true, string.Empty);
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue) return null;

        var dt = value.Value;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc)
        };
    }

    private async Task<(string key, string qrUri)> EnsureTotpSetupAsync(ApplicationUser user)
    {
        var key = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await _userManager.ResetAuthenticatorKeyAsync(user);
            key = await _userManager.GetAuthenticatorKeyAsync(user) ?? string.Empty;
        }

        var issuer = Uri.EscapeDataString("Arroyo Seco");
        var account = Uri.EscapeDataString(user.Email ?? string.Empty);
        var qrUri = $"otpauth://totp/{issuer}:{account}?secret={key}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
        return (key, qrUri);
    }
}
