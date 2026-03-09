using System;
using System.Net;
using System.Net.Mail;
using System.IO;
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

    public SmtpEmailService(
        IOptions<EmailOptions> options,
        ILogger<SmtpEmailService> logger,
        IHostEnvironment env)
    {
        _options = options.Value;
        _logger = logger;
        _env = env;
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

            using var message = new MailMessage
            {
                From = new MailAddress(_options.FromEmail, _options.FromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(new MailAddress(toEmail));

            using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
            {
                EnableSsl = _options.EnableSsl,
                UseDefaultCredentials = false
            };

            if (!string.IsNullOrWhiteSpace(_options.SmtpUsername) && !string.IsNullOrWhiteSpace(_options.SmtpPassword))
            {
                client.Credentials = new NetworkCredential(_options.SmtpUsername, _options.SmtpPassword);
            }

            await client.SendMailAsync(message, ct);
            _logger.LogInformation("Correo enviado exitosamente a {ToEmail}", toEmail);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Excepción enviando correo a {ToEmail}", toEmail);
            return await TryWriteOutboxAsync(toEmail, subject, htmlBody, ct);
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
