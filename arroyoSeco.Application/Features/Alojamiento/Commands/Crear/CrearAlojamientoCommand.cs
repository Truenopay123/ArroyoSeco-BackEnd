using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Domain.Entities.Alojamientos;
using arroyoSeco.Application.Common.Interfaces;
// Alias para evitar la colisi�n con el namespace Features.Alojamiento
using AlojamientoEntity = arroyoSeco.Domain.Entities.Alojamientos.Alojamiento;

namespace arroyoSeco.Application.Features.Alojamiento.Commands.Crear;

public class CrearAlojamientoCommand
{
    public string Nombre { get; set; } = null!;
    public string Ubicacion { get; set; } = null!;
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public string? Direccion { get; set; }
    public int MaxHuespedes { get; set; }
    public int Habitaciones { get; set; }
    public int Banos { get; set; }
    public decimal PrecioPorNoche { get; set; }
    public string? FotoPrincipal { get; set; }
    public List<string> FotosUrls { get; set; } = new();
    public List<string> Amenidades { get; set; } = new();
}

public class CrearAlojamientoCommandHandler
{
    private readonly IAppDbContext _context;
    private readonly ICurrentUserService _current;

    public CrearAlojamientoCommandHandler(IAppDbContext context, ICurrentUserService current)
    {
        _context = context;
        _current = current;
    }

    public async Task<int> Handle(CrearAlojamientoCommand request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Nombre))
            throw new ArgumentException("Nombre requerido");
        if (string.IsNullOrWhiteSpace(request.Ubicacion))
            throw new ArgumentException("Ubicaci�n requerida");
        if (request.PrecioPorNoche <= 0)
            throw new ArgumentException("PrecioPorNoche inv�lido");

        var oferente = await _context.Oferentes
            .FirstOrDefaultAsync(o => o.Id == _current.UserId, ct);
        if (oferente == null)
            throw new InvalidOperationException("Oferente no encontrado para el usuario actual");

        var alojamiento = new AlojamientoEntity
        {
            OferenteId = oferente.Id,
            Nombre = request.Nombre.Trim(),
            Ubicacion = request.Ubicacion.Trim(),
            Latitud = request.Latitud,
            Longitud = request.Longitud,
            Direccion = request.Direccion?.Trim(),
            MaxHuespedes = request.MaxHuespedes,
            Habitaciones = request.Habitaciones,
            Banos = request.Banos,
            PrecioPorNoche = request.PrecioPorNoche,
            FotoPrincipal = request.FotoPrincipal,
            Amenidades = request.Amenidades
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Fotos = request.FotosUrls.Select((url, i) =>
                new FotoAlojamiento { Url = url, Orden = i + 1 }).ToList()
        };

        _context.Alojamientos.Add(alojamiento);
        oferente.NumeroAlojamientos++;
        await _context.SaveChangesAsync(ct);

        return alojamiento.Id;
    }
}