using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using arroyoSeco.Application.Common.Interfaces;
using arroyoSeco.Domain.Entities.Gastronomia;

namespace arroyoSeco.Application.Features.Gastronomia.Commands.Crear;

public class CrearReservaGastronomiaCommand
{
    public int EstablecimientoId { get; set; }
    public DateTime Fecha { get; set; }
    public int NumeroPersonas { get; set; }
    public int? MesaId { get; set; }
}

public class CrearReservaGastronomiaCommandHandler
{
    private readonly IAppDbContext _context;
    private readonly ICurrentUserService _current;
    private readonly INotificationService _notifications;
    private readonly IFolioGenerator _folio;

    public CrearReservaGastronomiaCommandHandler(IAppDbContext context, ICurrentUserService current, INotificationService notifications, IFolioGenerator folio)
    {
        _context = context;
        _current = current;
        _notifications = notifications;
        _folio = folio;
    }

    public async Task<int> Handle(CrearReservaGastronomiaCommand request, CancellationToken ct = default)
    {
        if (request.NumeroPersonas <= 0)
            throw new ArgumentException("Número de personas inválido");

        var est = await _context.Establecimientos.FirstOrDefaultAsync(e => e.Id == request.EstablecimientoId, ct);
        if (est == null) throw new InvalidOperationException("Establecimiento no encontrado");

        Mesa? mesa = null;
        if (request.MesaId.HasValue)
        {
            mesa = await _context.Mesas.FirstOrDefaultAsync(m => m.Id == request.MesaId && m.EstablecimientoId == est.Id, ct);
            if (mesa == null) throw new InvalidOperationException("Mesa no encontrada");
            if (!mesa.Disponible) throw new InvalidOperationException("Mesa no disponible");
        }

        var folio = await _folio.NextReservaFolioAsync(ct);

        var reserva = new ReservaGastronomia
        {
            Folio = folio,
            UsuarioId = _current.UserId,
            EstablecimientoId = est.Id,
            MesaId = mesa?.Id,
            Fecha = request.Fecha,
            NumeroPersonas = request.NumeroPersonas,
            Estado = "Pendiente",
            Total = 0
        };

        _context.ReservasGastronomia.Add(reserva);
        await _context.SaveChangesAsync(ct);

        await _notifications.PushAsync(
            est.OferenteId,
            "Nueva Reserva",
            $"Reserva para {request.NumeroPersonas} personas el {request.Fecha:dd/MM/yyyy HH:mm}",
            "ReservaGastronomia",
            $"/gastronomia/{est.Id}/reservas/{reserva.Id}",
            ct);

        return reserva.Id;
    }
}
