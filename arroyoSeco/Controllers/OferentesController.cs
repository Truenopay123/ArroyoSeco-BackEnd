using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Enums;
using Microsoft.AspNetCore.Identity;
using arroyoSeco.Infrastructure.Auth;
using arroyoSeco.Domain.Entities.Usuarios;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OferentesController : ControllerBase
{
    private readonly IAppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ICurrentUserService _current;

    public OferentesController(IAppDbContext db, UserManager<ApplicationUser> userManager, ICurrentUserService current)
    {
        _db = db;
        _userManager = userManager;
        _current = current;
    }

    [Authorize(Roles = "Admin")]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] TipoOferente? tipo, CancellationToken ct)
    {
        var query = _db.Oferentes.AsQueryable();

        if (tipo.HasValue)
        {
            query = query.Where(o => o.Tipo == tipo.Value || o.Tipo == TipoOferente.Ambos);
        }

        var oferentes = await query
            .Include(o => o.Alojamientos)
            .Include(o => o.Establecimientos)
            .AsNoTracking()
            .ToListAsync(ct);

        // Obtener todos los usuarios de Identity en una sola consulta
        var oferenteIds = oferentes.Select(o => o.Id).ToList();
        var usuarios = await _userManager.Users
            .Where(u => oferenteIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Mapear a DTOs
        var result = oferentes.Select(oferente => new
        {
            oferente.Id,
            oferente.Nombre,
            oferente.Tipo,
            oferente.Estado,
            oferente.NumeroAlojamientos,
            Email = usuarios.ContainsKey(oferente.Id) ? usuarios[oferente.Id].Email : null,
            Telefono = usuarios.ContainsKey(oferente.Id) ? usuarios[oferente.Id].PhoneNumber : null,
            oferente.Alojamientos,
            oferente.Establecimientos
        }).ToList();

        return Ok(result);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("alojamiento")]
    public async Task<IActionResult> ListAlojamiento(CancellationToken ct)
    {
        var oferentes = await _db.Oferentes
            .Where(o => o.Tipo == TipoOferente.Alojamiento || o.Tipo == TipoOferente.Ambos)
            .Include(o => o.Alojamientos)
            .AsNoTracking()
            .ToListAsync(ct);

        // Obtener todos los usuarios de Identity en una sola consulta
        var oferenteIds = oferentes.Select(o => o.Id).ToList();
        var usuarios = await _userManager.Users
            .Where(u => oferenteIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Mapear a DTOs
        var result = oferentes.Select(oferente => new
        {
            oferente.Id,
            oferente.Nombre,
            oferente.Tipo,
            oferente.Estado,
            oferente.NumeroAlojamientos,
            Email = usuarios.ContainsKey(oferente.Id) ? usuarios[oferente.Id].Email : null,
            Telefono = usuarios.ContainsKey(oferente.Id) ? usuarios[oferente.Id].PhoneNumber : null,
            oferente.Alojamientos
        }).ToList();

        return Ok(result);
    }

    [Authorize(Roles = "Admin")]
    [HttpGet("gastronomia")]
    public async Task<IActionResult> ListGastronomia(CancellationToken ct)
    {
        var oferentes = await _db.Oferentes
            .Where(o => o.Tipo == TipoOferente.Gastronomia || o.Tipo == TipoOferente.Ambos)
            .Include(o => o.Establecimientos)
            .AsNoTracking()
            .ToListAsync(ct);

        // Obtener todos los usuarios de Identity en una sola consulta
        var oferenteIds = oferentes.Select(o => o.Id).ToList();
        var usuarios = await _userManager.Users
            .Where(u => oferenteIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        // Mapear a DTOs
        var result = oferentes.Select(oferente => new
        {
            oferente.Id,
            oferente.Nombre,
            oferente.Tipo,
            oferente.Estado,
            oferente.NumeroAlojamientos,
            Email = usuarios.ContainsKey(oferente.Id) ? usuarios[oferente.Id].Email : null,
            Telefono = usuarios.ContainsKey(oferente.Id) ? usuarios[oferente.Id].PhoneNumber : null,
            oferente.Establecimientos
        }).ToList();

        return Ok(result);
    }

    [Authorize(Roles = "Admin")]
    [HttpPut("{id}/tipo")]
    public async Task<IActionResult> CambiarTipo(string id, [FromBody] TipoOferente tipo, CancellationToken ct)
    {
        var oferente = await _db.Oferentes.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (oferente == null) return NotFound();

        oferente.Tipo = tipo;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ============ ENDPOINTS PARA EL OFERENTE ============

    /// <summary>
    /// Obtener perfil del oferente actual (autenticado)
    /// </summary>
    [Authorize(Roles = "Oferente")]
    [HttpGet("perfil")]
    public async Task<IActionResult> GetPerfil(CancellationToken ct)
    {
        var userId = _current.UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var oferente = await _db.Oferentes
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == userId, ct);

        if (oferente == null) return NotFound(new { message = "Oferente no encontrado" });

        var user = await _userManager.FindByIdAsync(userId);

        return Ok(new
        {
            oferente.Id,
            oferente.Nombre,
            oferente.Tipo,
            oferente.Estado,
            Email = user?.Email,
            Telefono = user?.PhoneNumber,
            oferente.Banco,
            oferente.NumeroCuenta,
            oferente.CLABE,
            oferente.TitularCuenta
        });
    }

    /// <summary>
    /// Actualizar perfil del oferente actual
    /// </summary>
    [Authorize(Roles = "Oferente")]
    [HttpPut("perfil")]
    public async Task<IActionResult> UpdatePerfil([FromBody] UpdatePerfilRequest request, CancellationToken ct)
    {
        var userId = _current.UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var oferente = await _db.Oferentes.FirstOrDefaultAsync(o => o.Id == userId, ct);
        if (oferente == null) return NotFound(new { message = "Oferente no encontrado" });

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound(new { message = "Usuario no encontrado" });

        // Actualizar nombre del negocio
        if (!string.IsNullOrWhiteSpace(request.Nombre))
        {
            oferente.Nombre = request.Nombre;
        }

        // Actualizar teléfono en Identity
        if (request.Telefono != null)
        {
            user.PhoneNumber = request.Telefono;
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                return BadRequest(new { message = "Error al actualizar teléfono", errors = updateResult.Errors });
            }
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            oferente.Id,
            oferente.Nombre,
            oferente.Tipo,
            oferente.Estado,
            Email = user.Email,
            Telefono = user.PhoneNumber,
            oferente.Banco,
            oferente.NumeroCuenta,
            oferente.CLABE,
            oferente.TitularCuenta
        });
    }

    /// <summary>
    /// Actualizar datos bancarios del oferente actual
    /// </summary>
    [Authorize(Roles = "Oferente")]
    [HttpPut("datos-bancarios")]
    public async Task<IActionResult> UpdateDatosBancarios([FromBody] UpdateDatosBancariosRequest request, CancellationToken ct)
    {
        var userId = _current.UserId;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        var oferente = await _db.Oferentes.FirstOrDefaultAsync(o => o.Id == userId, ct);
        if (oferente == null) return NotFound(new { message = "Oferente no encontrado" });

        oferente.Banco = request.Banco;
        oferente.NumeroCuenta = request.NumeroCuenta;
        oferente.CLABE = request.CLABE;
        oferente.TitularCuenta = request.TitularCuenta;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            oferente.Banco,
            oferente.NumeroCuenta,
            oferente.CLABE,
            oferente.TitularCuenta
        });
    }
}

public record UpdatePerfilRequest(string? Nombre, string? Telefono);
public record UpdateDatosBancariosRequest(string? Banco, string? NumeroCuenta, string? CLABE, string? TitularCuenta);
