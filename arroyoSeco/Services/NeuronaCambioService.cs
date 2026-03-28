using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace arroyoSeco.Services;

public sealed class NeuronaServiceOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5001";
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed record NeuronaCambioResultado(
    decimal PagoConvertidoMxn,
    decimal ValorRealApiMxn,
    decimal CambioMxn,
    decimal TipoCambio);

public interface INeuronaCambioService
{
    Task<NeuronaCambioResultado> CalcularCambioAsync(decimal precioPesosMxn, decimal pagoDolares, CancellationToken cancellationToken = default);
}

public sealed class NeuronaCambioService : INeuronaCambioService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NeuronaCambioService> _logger;

    public NeuronaCambioService(
        HttpClient httpClient,
        ILogger<NeuronaCambioService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<NeuronaCambioResultado> CalcularCambioAsync(decimal precioPesosMxn, decimal pagoDolares, CancellationToken cancellationToken = default)
    {
        if (_httpClient.BaseAddress is null)
        {
            throw new InvalidOperationException("NeuronaService:BaseUrl no está configurada con una URL absoluta válida.");
        }

        var payload = new CalcularMicroservicioRequest(precioPesosMxn, pagoDolares);
        using var response = await _httpClient.PostAsJsonAsync("/calcular", payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "La neurona respondió con error. StatusCode={StatusCode}. Detail={Detail}",
                (int)response.StatusCode,
                detail);

            throw new InvalidOperationException(
                $"El microservicio de neurona devolvió {(int)response.StatusCode}: {detail}");
        }

        var result = await response.Content.ReadFromJsonAsync<CalcularMicroservicioResponse>(cancellationToken: cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException("El microservicio de neurona devolvió una respuesta vacía.");
        }

        return new NeuronaCambioResultado(
            PagoConvertidoMxn: RedondearMoneda((decimal)result.PagoConvertido),
            ValorRealApiMxn: RedondearMoneda((decimal)result.ValorRealApiMxn),
            CambioMxn: RedondearMoneda((decimal)result.CambioMxn),
            TipoCambio: RedondearTipoCambio((decimal)result.TipoCambio));
    }

    private static decimal RedondearMoneda(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal RedondearTipoCambio(decimal value)
        => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private sealed record CalcularMicroservicioRequest(
        [property: JsonPropertyName("precio_mxn")] decimal PrecioMxn,
        [property: JsonPropertyName("pago_usd")] decimal PagoUsd);

    private sealed record CalcularMicroservicioResponse(
        [property: JsonPropertyName("cambio_mxn")] double CambioMxn,
        [property: JsonPropertyName("pago_convertido")] double PagoConvertido,
        [property: JsonPropertyName("valor_real_api_mxn")] double ValorRealApiMxn,
        [property: JsonPropertyName("tipo_cambio")] double TipoCambio);
}