using System;
using System.Collections.Generic;
using arroyoSeco.Domain.Entities.Alojamientos;
using arroyoSeco.Domain.Entities.Gastronomia;
using arroyoSeco.Domain.Entities.Enums;

namespace arroyoSeco.Domain.Entities.Usuarios;

public class Oferente
{
    public string Id { get; set; } = null!;
    public string Nombre { get; set; } = null!;
    public TipoOferente Tipo { get; set; } = TipoOferente.Ambos;
    public string Estado { get; set; } = "Pendiente"; // Pendiente, Activo, Inactivo, Suspendido
    public int NumeroAlojamientos { get; set; }

    // Datos bancarios para pago por transferencia
    public string? Banco { get; set; }
    public string? NumeroCuenta { get; set; }
    public string? CLABE { get; set; }
    public string? TitularCuenta { get; set; }

    public ICollection<Alojamiento> Alojamientos { get; set; } = new List<Alojamiento>();
    public ICollection<Establecimiento> Establecimientos { get; set; } = new List<Establecimiento>();
}