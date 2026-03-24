using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace arroyoSeco.Hubs;

/// <summary>
/// Real-time hub para actualizaciones de precios de alojamientos.
/// Los clientes se conectan para recibir notificaciones cuando los precios cambian.
/// Ejemplo: Oferente cambia precio en laptop → Clientes ven cambio en cel sin refrescar
/// </summary>
[Authorize]
public class PriceUpdateHub : Hub
{
    /// <summary>
    /// Cuando el cliente se conecta, se une a un grupo por alojamiento para recibir updates específicos
    /// </summary>
    public async Task JoinAlojamientoGroup(int alojamientoId)
    {
        var groupName = $"alojamiento_{alojamientoId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        Console.WriteLine($"Cliente conectado al grupo {groupName}");
    }

    /// <summary>
    /// Cuando el cliente se desconecta
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Console.WriteLine($"Cliente desconectado: {Context.ConnectionId}");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// El cliente puede solicitar el precio actual si es necesario (fallback)
    /// </summary>
    public string GetConnectionId() => Context.ConnectionId;
}
