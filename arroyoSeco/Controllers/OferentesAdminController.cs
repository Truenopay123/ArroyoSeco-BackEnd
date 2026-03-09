using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Solicitudes;
using arroyoSeco.Domain.Entities.Usuarios;
using UsuarioOferente = arroyoSeco.Domain.Entities.Usuarios.Oferente;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/admin/oferentes")]
[Authorize(Roles = "Admin")] // solo admin
public class OferentesAdminController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly INotificationService _noti;
    private readonly IEmailService _email;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _configuration;

    public OferentesAdminController(
        IAppDbContext db,
        INotificationService noti,
        IEmailService email,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IConfiguration configuration)
    {
        _db = db;
        _noti = noti;
        _email = email;
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
    }

    // Crear usuario Identity de tipo Oferente y su registro en tabla Oferentes
    public record CrearUsuarioOferenteDto(string Email, string? Password, string Nombre, string? Telefono, int Tipo);

    [HttpPost("usuarios")]
    public async Task<IActionResult> CrearUsuarioOferente([FromBody] CrearUsuarioOferenteDto dto, CancellationToken ct)
    {
        var existing = await _userManager.FindByEmailAsync(dto.Email);
        if (existing is not null) return Conflict("Ya existe un usuario con ese email.");

        var tempPassword = string.IsNullOrWhiteSpace(dto.Password)
            ? GenerateTemporaryPassword()
            : dto.Password.Trim();

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            PhoneNumber = dto.Telefono,
            EmailConfirmed = false,
            RequiereCambioPassword = true
        };
        var res = await _userManager.CreateAsync(user, tempPassword);
        if (!res.Succeeded) return BadRequest(res.Errors);

        if (!await _roleManager.RoleExistsAsync("Oferente"))
            await _roleManager.CreateAsync(new IdentityRole("Oferente"));
        await _userManager.AddToRoleAsync(user, "Oferente");

        // Crea el Oferente (dominio)
        if (!await _db.Oferentes.AnyAsync(o => o.Id == user.Id, ct))
        {
            var o = new UsuarioOferente
            {
                Id = user.Id,
                Nombre = dto.Nombre,
                NumeroAlojamientos = 0,
                Tipo = (arroyoSeco.Domain.Entities.Enums.TipoOferente)dto.Tipo,
                Estado = "Activo"
            };
            _db.Oferentes.Add(o);
            await _db.SaveChangesAsync(ct);
        }

        // Notificación en BD + Enviar correo con credenciales
        var tipoTexto = GetTipoTexto(dto.Tipo);
        var emailSent = await SendOnboardingEmailAsync(dto.Email, dto.Nombre, tipoTexto, tempPassword, ct);

        // Notificación en BD
        await _noti.PushAsync(user.Id, "Cuenta de Oferente creada",
            $"Tu cuenta de oferente para {tipoTexto} ha sido creada por un administrador. Hemos enviado tus credenciales al correo.", "Oferente", null, ct);

        return CreatedAtAction(nameof(Get), new { id = user.Id }, new
        {
            user.Id,
            user.Email,
            onboardingEmailSent = emailSent
        });
    }

    // CRUD Oferente
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _db.Oferentes.AsNoTracking().ToListAsync(ct));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var o = await _db.Oferentes.Include(x => x.Alojamientos).FirstOrDefaultAsync(x => x.Id == id, ct);
        return o is null ? NotFound() : Ok(o);
    }

    public record ActualizarOferenteDto(string? Nombre, string? Telefono, int? Tipo);

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] ActualizarOferenteDto dto, CancellationToken ct)
    {
        var o = await _db.Oferentes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound(new { message = "Oferente no encontrado" });

        // Actualizar nombre si viene
        if (!string.IsNullOrWhiteSpace(dto.Nombre))
        {
            o.Nombre = dto.Nombre;
        }

        // Actualizar tipo si viene
        if (dto.Tipo.HasValue)
        {
            o.Tipo = (arroyoSeco.Domain.Entities.Enums.TipoOferente)dto.Tipo.Value;
        }

        // Actualizar teléfono en Identity si viene
        if (dto.Telefono != null)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                user.PhoneNumber = dto.Telefono;
                var updateResult = await _userManager.UpdateAsync(user);
                if (!updateResult.Succeeded)
                {
                    return BadRequest(new { message = "Error al actualizar teléfono", errors = updateResult.Errors });
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        // Retornar el oferente actualizado con todos los datos
        var userFinal = await _userManager.FindByIdAsync(id);
        return Ok(new
        {
            o.Id,
            o.Nombre,
            Tipo = (int)o.Tipo,
            o.Estado,
            Email = userFinal?.Email,
            Telefono = userFinal?.PhoneNumber
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var o = await _db.Oferentes.Include(x => x.Alojamientos).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        if (o.Alojamientos?.Any() == true) return BadRequest("No se puede eliminar: tiene alojamientos asociados.");
        _db.Oferentes.Remove(o);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Gesti�n de Solicitudes de Oferente (opcional si usas el flujo de solicitudes)
    [HttpGet("solicitudes")]
    public async Task<IActionResult> ListSolicitudes([FromQuery] string? estatus, CancellationToken ct)
    {
        var q = _db.SolicitudesOferente.AsQueryable();
        if (!string.IsNullOrWhiteSpace(estatus)) q = q.Where(s => s.Estatus == estatus);
        var items = await q.OrderByDescending(s => s.FechaSolicitud).AsNoTracking().ToListAsync(ct);
        return Ok(items);
    }

    [HttpPost("solicitudes/{id:int}/aprobar")]
    public async Task<IActionResult> Aprobar(int id, CancellationToken ct)
    {
        var s = await _db.SolicitudesOferente.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Solicitud no encontrada" });

        // crea (o reutiliza) usuario por correo de la solicitud
        var email = string.IsNullOrWhiteSpace(s.Correo) ? $"oferente{id}@arroyoseco.com" : s.Correo.Trim();
        var user = await _userManager.FindByEmailAsync(email);
        string tempPass = GenerateTemporaryPassword();
        
        if (user is null)
        {
            user = new ApplicationUser 
            { 
                UserName = email, 
                Email = email, 
                EmailConfirmed = false,
                PhoneNumber = s.Telefono,
                RequiereCambioPassword = true
            };
            
            var res = await _userManager.CreateAsync(user, tempPass);
            if (!res.Succeeded) return BadRequest(res.Errors);
            
            // Asignar rol Oferente
            if (!await _roleManager.RoleExistsAsync("Oferente"))
                await _roleManager.CreateAsync(new IdentityRole("Oferente"));
            await _userManager.AddToRoleAsync(user, "Oferente");
        }
        else
        {
            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await _userManager.ResetPasswordAsync(user, resetToken, tempPass);
            if (!resetResult.Succeeded) return BadRequest(resetResult.Errors);

            user.RequiereCambioPassword = true;
            await _userManager.UpdateAsync(user);

            if (!await _userManager.IsInRoleAsync(user, "Oferente"))
            {
                if (!await _roleManager.RoleExistsAsync("Oferente"))
                    await _roleManager.CreateAsync(new IdentityRole("Oferente"));
                await _userManager.AddToRoleAsync(user, "Oferente");
            }
        }

        // Crear oferente si no existe
        var oferente = await _db.Oferentes.FirstOrDefaultAsync(o => o.Id == user.Id, ct);
        if (oferente is null)
        {
            oferente = new UsuarioOferente
            { 
                Id = user.Id, 
                Nombre = s.NombreNegocio, 
                NumeroAlojamientos = 0,
                Tipo = s.TipoSolicitado,
                Estado = "Activo"
            };
            _db.Oferentes.Add(oferente);
        }
        else
        {
            oferente.Nombre = string.IsNullOrWhiteSpace(s.NombreNegocio) ? oferente.Nombre : s.NombreNegocio;
            oferente.Tipo = s.TipoSolicitado;
            oferente.Estado = "Activo";
        }

        // Actualizar solicitud
        s.Estatus = "Aprobada";
        s.FechaRespuesta = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var tipoTexto = GetTipoTexto((int)s.TipoSolicitado);
        var onboardingSent = await SendOnboardingEmailAsync(email, s.NombreSolicitante, tipoTexto, tempPass, ct);

        // Notificación en BD
        await _noti.PushAsync(user.Id, "Solicitud aprobada",
            $"Tu solicitud para ser oferente de {tipoTexto} fue aprobada. Hemos enviado tus credenciales al correo.", 
            "SolicitudOferente", null, ct);

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            tipo = s.TipoSolicitado,
            onboardingEmailSent = onboardingSent,
            message = onboardingSent
                ? "Solicitud aprobada y correo enviado"
                : "Solicitud aprobada, pero no se pudo enviar el correo"
        });
    }

    private string GetTipoTexto(int tipo) => tipo switch
    {
        1 => "Alojamiento",
        2 => "Gastronomía",
        3 => "Ambos",
        _ => "Desconocido"
    };

    private string GenerateTemporaryPassword()
    {
        return "Temp" + Guid.NewGuid().ToString("N")[..8] + "!";
    }

    private async Task<bool> SendOnboardingEmailAsync(string email, string nombre, string tipoTexto, string tempPassword, CancellationToken ct)
    {
        var loginUrl = (_configuration["AppUrls:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:4200") + "/login";

        var user = await _userManager.FindByEmailAsync(email);
        string confirmBlock = string.Empty;
        if (user is not null)
        {
            var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
            var confirmUrl =
                (_configuration["AppUrls:FrontendBaseUrl"]?.TrimEnd('/') ?? "http://localhost:4200") +
                $"/confirm-email?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(encodedToken)}";

            confirmBlock = $"<p><strong>Confirma tu correo:</strong> <a href='{confirmUrl}'>Confirmar cuenta</a></p>";
        }

        var correoHtml = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #2c3e50; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #ecf0f1; padding: 20px; border-radius: 0 0 5px 5px; }}
        .credentials {{ background-color: #fff; padding: 15px; border-left: 4px solid #27ae60; margin: 15px 0; }}
        .credentials p {{ margin: 5px 0; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>¡Tu Cuenta de Oferente ha sido habilitada!</h1>
        </div>
        <div class='content'>
            <p>Hola {nombre},</p>
            <p>Tu cuenta de oferente para <strong>{tipoTexto}</strong> fue creada/aprobada por un administrador.</p>
            <div class='credentials'>
                <p><strong>Email:</strong> {email}</p>
                <p><strong>Contraseña temporal:</strong> {tempPassword}</p>
                <p><strong>Acceso:</strong> <a href='{loginUrl}'>Iniciar sesión</a></p>
                {confirmBlock}
                <p><em>Debes cambiar tu contraseña en tu primer inicio de sesión.</em></p>
            </div>
        </div>
    </div>
</body>
</html>";

        return await _email.SendEmailAsync(email, "Cuenta de Oferente - Arroyo Seco", correoHtml, ct);
    }

    [HttpPost("solicitudes/{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, CancellationToken ct)
    {
        var s = await _db.SolicitudesOferente.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return NotFound(new { message = "Solicitud no encontrada" });
        
        s.Estatus = "Rechazada";
        s.FechaRespuesta = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Enviar correo de rechazo
        var correoHtml = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #e74c3c; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #ecf0f1; padding: 20px; border-radius: 0 0 5px 5px; }}
        .auto-email {{ background-color: #fff3cd; padding: 12px; border-left: 4px solid #ffc107; margin: 15px 0; font-size: 12px; color: #856404; }}
        .footer {{ margin-top: 20px; font-size: 12px; color: #7f8c8d; text-align: center; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>Solicitud de Oferente - Decisión</h1>
        </div>
        <div class='content'>
            <p>Hola {s.NombreSolicitante},</p>
            <p>Lamentablemente, tu solicitud para ser oferente en Arroyo Seco ha sido <strong style='color: #e74c3c;'>RECHAZADA</strong> en esta ocasión.</p>
            <p>Puedes volver a intentar en el futuro presentando una nueva solicitud.</p>
            <p>Si tienes preguntas, no dudes en contactarnos.</p>
            
            <div class='auto-email'>
                <strong>⚠️ Nota:</strong> Este es un correo automático, por favor no contestes a este mensaje. No recibiremos tu respuesta. Si necesitas ayuda, contáctanos a través de nuestro sitio web.
            </div>
        </div>
        <div class='footer'>
            <p>© 2025 Arroyo Seco. Todos los derechos reservados.</p>
        </div>
    </div>
</body>
</html>";

        await _email.SendEmailAsync(s.Correo, "Tu solicitud de oferente ha sido rechazada", correoHtml, ct);
        
        return Ok(new { message = "Solicitud rechazada y correo enviado" });
    }

    // Cambiar estado de oferente
    public record CambiarEstadoDto(string Estado);

    [HttpPut("{id}/estado")]
    public async Task<IActionResult> CambiarEstado(string id, [FromBody] CambiarEstadoDto dto, CancellationToken ct)
    {
        var oferente = await _db.Oferentes.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (oferente == null) 
            return NotFound(new { message = "Oferente no encontrado" });
        
        oferente.Estado = dto.Estado;
        await _db.SaveChangesAsync(ct);
        
        return Ok(new { 
            id = oferente.Id, 
            estado = oferente.Estado 
        });
    }
}