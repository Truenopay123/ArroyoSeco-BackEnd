using System;

namespace arroyoSeco.Application.Features.Reservas.Commands.Crear;

public class CrearReservaCommand
{
    public int AlojamientoId { get; set; }
    public DateTime FechaEntrada { get; set; }
    public DateTime FechaSalida { get; set; }
    public string? Sexo { get; set; }
    public DateTime? FechaNacimiento { get; set; }
    public string? LugarOrigen { get; set; }
    public bool AceptaPoliticaDatos { get; set; }
}