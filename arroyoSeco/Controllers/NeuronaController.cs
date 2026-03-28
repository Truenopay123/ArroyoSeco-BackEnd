using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using arroyoSeco.Services;

namespace arroyoSeco.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class NeuronaController : ControllerBase
{
    private readonly INeuronaCambioService _neuronaCambioService;
    private readonly ILogger<NeuronaController> _logger;

    public NeuronaController(INeuronaCambioService neuronaCambioService, ILogger<NeuronaController> logger)
    {
        _neuronaCambioService = neuronaCambioService;
        _logger = logger;
    }

    public sealed record CalcularCambioRequest(decimal PrecioPesosMxn, decimal PagoDolares);

    public sealed record CalcularCambioResponse(
        decimal PagoConvertidoMxn,
        decimal ValorRealApiMxn,
        decimal CambioMxn,
        decimal TipoCambio);

    [HttpPost("calcular-cambio")]
    public async Task<IActionResult> CalcularCambio([FromBody] CalcularCambioRequest request, CancellationToken cancellationToken)
    {
        if (request.PrecioPesosMxn <= 0)
        {
            return BadRequest(new { message = "precioPesosMxn debe ser mayor a 0." });
        }

        if (request.PagoDolares <= 0)
        {
            return BadRequest(new { message = "pagoDolares debe ser mayor a 0." });
        }

        try
        {
            var result = await _neuronaCambioService.CalcularCambioAsync(
                request.PrecioPesosMxn,
                request.PagoDolares,
                cancellationToken);

            return Ok(new CalcularCambioResponse(
                PagoConvertidoMxn: result.PagoConvertidoMxn,
                ValorRealApiMxn: result.ValorRealApiMxn,
                CambioMxn: result.CambioMxn,
                TipoCambio: result.TipoCambio));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "No se pudo conectar con el microservicio de neurona.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "El servicio de conversión de moneda no está disponible en este momento."
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "La neurona devolvió una respuesta inválida o falló al procesar la solicitud.");
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = "El servicio de neurona no devolvió una respuesta válida.",
                detail = ex.Message
            });
        }
    }
}