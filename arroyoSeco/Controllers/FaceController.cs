using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.Text.Json;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Usuarios;

namespace arroyoSeco.Controllers;

/// <summary>
/// Controlador para registro y verificación facial (2FA con face-api.js).
/// La comparación se hace en el navegador; el backend solo almacena/valida tokens.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class FaceController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IJwtTokenGenerator _token;

    public FaceController(
        UserManager<ApplicationUser> userManager,
        IJwtTokenGenerator token)
    {
        _userManager = userManager;
        _token = token;
    }

    // DTOs para los endpoints faciales
    public record FaceEnrollDto(float[] Descriptor);
    public record FaceVerifyDto(string TempToken);
    public record FaceEnrollInitialDto(string TempToken, float[] Descriptor);

    /// <summary>
    /// Registra el descriptor facial del usuario autenticado.
    /// Solo ADMIN y OFERENTE pueden registrar su rostro.
    /// Recibe un Float32Array serializado como array de floats.
    /// </summary>
    [Authorize(Roles = "Admin,Oferente")]
    [HttpPost("enroll")]
    public async Task<IActionResult> Enroll([FromBody] FaceEnrollDto dto)
    {
        if (dto.Descriptor == null || dto.Descriptor.Length != 128)
            return BadRequest(new { message = "Descriptor facial inválido. Se esperan 128 valores." });

        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        // Serializar el descriptor como JSON para almacenar en la columna JSONB
        user.FaceDescriptor = JsonSerializer.Serialize(dto.Descriptor);
        var result = await _userManager.UpdateAsync(user);

        if (!result.Succeeded)
            return BadRequest(new { message = "No se pudo guardar el descriptor facial.", errors = result.Errors });

        return Ok(new { message = "Rostro registrado exitosamente." });
    }

    /// <summary>
    /// Verifica el token temporal tras la validación facial en el navegador.
    /// Si el tempToken es válido, emite el JWT real.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("verify")]
    public async Task<IActionResult> Verify([FromBody] FaceVerifyDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TempToken))
            return BadRequest(new { message = "Token temporal requerido." });

        // Validar el token temporal (verifica firma, expiración y purpose=face-2fa)
        var principal = _token.ValidateTempToken(dto.TempToken);
        if (principal is null)
            return Unauthorized(new { message = "Token temporal inválido o expirado." });

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Token temporal inválido." });

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized(new { message = "Usuario no encontrado." });

        // Registrar primer login si aplica
        if (!user.FechaPrimerLogin.HasValue)
        {
            user.FechaPrimerLogin = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);
        }

        // Emitir JWT real con roles
        var roles = await _userManager.GetRolesAsync(user);
        var jwt = _token.Generate(user.Id, user.Email!, roles, user.RequiereCambioPassword);
        return Ok(new { token = jwt });
    }

    /// <summary>
    /// Registro facial inicial durante el login (sin JWT, usa tempToken).
    /// Guarda el descriptor y emite el JWT real en una sola operación.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("enroll-initial")]
    public async Task<IActionResult> EnrollInitial([FromBody] FaceEnrollInitialDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TempToken))
            return BadRequest(new { message = "Token temporal requerido." });

        if (dto.Descriptor == null || dto.Descriptor.Length != 128)
            return BadRequest(new { message = "Descriptor facial inválido. Se esperan 128 valores." });

        // Validar tempToken
        var principal = _token.ValidateTempToken(dto.TempToken);
        if (principal is null)
            return Unauthorized(new { message = "Token temporal inválido o expirado." });

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { message = "Token temporal inválido." });

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized(new { message = "Usuario no encontrado." });

        // Guardar descriptor facial
        user.FaceDescriptor = System.Text.Json.JsonSerializer.Serialize(dto.Descriptor);
        if (!user.FechaPrimerLogin.HasValue)
            user.FechaPrimerLogin = DateTime.UtcNow;

        var updateResult = await _userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return BadRequest(new { message = "No se pudo guardar el descriptor facial." });

        // Emitir JWT real
        var roles = await _userManager.GetRolesAsync(user);
        var jwt = _token.Generate(user.Id, user.Email!, roles, user.RequiereCambioPassword);

        return Ok(new { token = jwt, message = "Rostro registrado exitosamente." });
    }

    /// <summary>
    /// Elimina el descriptor facial del usuario autenticado.
    /// </summary>
    [Authorize(Roles = "Admin,Oferente")]
    [HttpDelete("unenroll")]
    public async Task<IActionResult> Unenroll()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        user.FaceDescriptor = null;
        await _userManager.UpdateAsync(user);

        return Ok(new { message = "Rostro eliminado exitosamente." });
    }

    /// <summary>
    /// Consulta si el usuario autenticado tiene rostro registrado.
    /// </summary>
    [Authorize]
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        return Ok(new { hasFaceEnrolled = !string.IsNullOrEmpty(user.FaceDescriptor) });
    }
}
