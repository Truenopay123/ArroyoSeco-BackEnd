using System;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using arroyoSeco.Application.Common.Interfaces;

namespace arroyoSeco.Infrastructure.Services;

public class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;
    private readonly IHostEnvironment _env;
    private readonly IHttpClientFactory _httpClientFactory;

    public SmtpEmailService(
        IOptions<EmailOptions> options,
        ILogger<SmtpEmailService> logger,
        IHostEnvironment env,
        IHttpClientFactory httpClientFactory)
    {
        _options = options.Value;
        _logger = logger;
        _env = env;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("Intento de enviar correo sin destinatario");
            return false;
        }

        try
        {
            _logger.LogInformation("Iniciando envío de correo a {ToEmail} - Asunto: {Subject}", toEmail, subject);

            if (string.IsNullOrWhiteSpace(_options.SmtpHost) || _options.SmtpPort <= 0)
            {
                _logger.LogWarning("Configuración SMTP inválida: host/puerto requeridos");
                return await TryWriteOutboxAsync(toEmail, subject, htmlBody, ct);
            }

            if (string.IsNullOrWhiteSpace(_options.FromEmail))
            {
                _logger.LogError("Email remitente no está configurado (FromEmail vacío)");
                return false;
            }
            var portsToTry = GetPortsToTry();
            Exception? lastError = null;

            foreach (var port in portsToTry)
            {
                try
                {
                    await SendUsingPortAsync(port, toEmail, subject, htmlBody, ct);
                    _logger.LogInformation(
                        "Correo enviado exitosamente a {ToEmail} usando {Host}:{Port}",
                        toEmail,
                        _options.SmtpHost,
                        port);
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    if (IsTimeoutException(ex) && portsToTry.Count > 1)
                    {
                        _logger.LogWarning(
                            ex,
                            "Timeout SMTP en {Host}:{Port}. Se intentará puerto alternativo.",
                            _options.SmtpHost,
                            port);
                        continue;
                    }

                    break;
                }
            }

            _logger.LogError(
                lastError,
                "Excepción enviando correo a {ToEmail}. Host={Host}, PuertosIntentados={Ports}, SSL={EnableSsl}",
                toEmail,
                _options.SmtpHost,
                string.Join(',', portsToTry),
                _options.EnableSsl);

            if (await TrySendViaBrevoApiAsync(toEmail, subject, htmlBody, ct))
            {
                return true;
            }

            return await TryWriteOutboxAsync(toEmail, subject, htmlBody, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción enviando correo a {ToEmail}", toEmail);
            return await TryWriteOutboxAsync(toEmail, subject, htmlBody, ct);
        }
    }

    private async Task SendUsingPortAsync(
        int port,
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct)
    {
        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(new MailAddress(toEmail));

        using var client = new SmtpClient(_options.SmtpHost, port)
        {
            EnableSsl = _options.EnableSsl,
            UseDefaultCredentials = false,
            Timeout = _options.TimeoutMs > 0 ? _options.TimeoutMs : 30000
        };

        if (!string.IsNullOrWhiteSpace(_options.SmtpUsername) && !string.IsNullOrWhiteSpace(_options.SmtpPassword))
        {
            client.Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword);
        }

        await client.SendMailAsync(message, ct);
    }

    private List<int> GetPortsToTry()
    {
        var ports = new List<int> { _options.SmtpPort };

        if (_options.UsePort2525Fallback && _options.SmtpPort == 587)
        {
            ports.Add(2525);
        }

        return ports.Where(p => p > 0).Distinct().ToList();
    }

    private static bool IsTimeoutException(Exception ex)
    {
        if (ex is SmtpException smtpEx)
        {
            if (smtpEx.InnerException is SocketException socketEx && socketEx.SocketErrorCode == SocketError.TimedOut)
            {
                return true;
            }

            if (smtpEx.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ex is SocketException socket && socket.SocketErrorCode == SocketError.TimedOut;
    }

    private async Task<bool> TrySendViaBrevoApiAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("BREVO_API_KEY")
            ?? Environment.GetEnvironmentVariable("EMAIL_BREVO_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Brevo API fallback omitido: BREVO_API_KEY no configurado.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.FromEmail))
        {
            _logger.LogWarning("Brevo API fallback omitido: FromEmail no configurado.");
            return false;
        }

        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["sender"] = new Dictionary<string, string>
                {
                    ["name"] = string.IsNullOrWhiteSpace(_options.FromName) ? "Arroyo Seco" : _options.FromName,
                    ["email"] = _options.FromEmail
                },
                ["to"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["email"] = toEmail
                    }
                },
                ["subject"] = subject,
                ["htmlContent"] = htmlBody
            };

            var json = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("api-key", apiKey);
            req.Headers.Add("accept", "application/json");

            var client = _httpClientFactory.CreateClient(nameof(SmtpEmailService));
            using var response = await client.SendAsync(req, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Correo enviado por Brevo API a {ToEmail}", toEmail);
                return true;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "Brevo API respondió error {StatusCode}. Body: {Body}",
                (int)response.StatusCode,
                body);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error usando Brevo API fallback para {ToEmail}", toEmail);
            return false;
        }
    }

    private async Task<bool> TryWriteOutboxAsync(string toEmail, string subject, string htmlBody, CancellationToken ct)
    {
        if (!_env.IsDevelopment() || !_options.UseFileOutboxInDevelopment)
        {
            return false;
        }

        try
        {
            var outboxPath = string.IsNullOrWhiteSpace(_options.FileOutboxPath)
                ? "C:\\ArroyoSeco\\comprobantes\\emails"
                : _options.FileOutboxPath;

            Directory.CreateDirectory(outboxPath);

            var safeTo = string.Join("_", toEmail.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{safeTo}.html";
            var filePath = Path.Combine(outboxPath, fileName);

            var content = $"""
                <!doctype html>
                <html>
                <head><meta charset="utf-8"><title>{subject}</title></head>
                <body>
                <p><strong>To:</strong> {toEmail}</p>
                <p><strong>Subject:</strong> {subject}</p>
                <hr />
                {htmlBody}
                </body>
                </html>
                """;

            await File.WriteAllTextAsync(filePath, content, ct);
            _logger.LogWarning("Correo guardado en outbox de desarrollo: {FilePath}", filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar correo en outbox de desarrollo");
            return false;
        }
    }

    public async Task<bool> SendNotificationEmailAsync(
        string toEmail,
        string titulo,
        string mensaje,
        string? actionUrl = null,
        CancellationToken ct = default)
    {
        var htmlBody = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='UTF-8'>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #2c3e50; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #ecf0f1; padding: 20px; border-radius: 0 0 5px 5px; }}
        .button {{ display: inline-block; background-color: #27ae60; color: white; padding: 10px 20px; text-decoration: none; border-radius: 5px; margin-top: 15px; }}
        .footer {{ margin-top: 20px; font-size: 12px; color: #7f8c8d; text-align: center; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h1>{titulo}</h1>
        </div>
        <div class='content'>
            <p>{mensaje}</p>
            {(string.IsNullOrWhiteSpace(actionUrl) ? "" : $"<a href='{actionUrl}' class='button'>Ver más</a>")}
        </div>
        <div class='footer'>
            <p>© 2025 Arroyo Seco. Todos los derechos reservados.</p>
        </div>
    </div>
</body>
</html>";

        return await SendEmailAsync(toEmail, titulo, htmlBody, ct);
    }
}
