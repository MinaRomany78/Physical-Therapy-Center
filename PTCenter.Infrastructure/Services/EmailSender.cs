using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;

namespace PTCenter.Infrastructure.Services;

/// <summary>Transport secrets come from deployment configuration or local user secrets.</summary>
public class EmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task<bool> SendEmailAsync(string email, string subject, string htmlMessage)
    {
        var host = configuration["Email:Host"]; var user = configuration["Email:Username"];
        var password = configuration["Email:Password"]; var sender = configuration["Email:SenderAddress"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(sender)) return false;
        try
        {
            using var client = new SmtpClient(host, configuration.GetValue("Email:Port",587)) {
                EnableSsl = true, UseDefaultCredentials = false, Credentials = new NetworkCredential(user,password)
            };
            using var message = new MailMessage { From = new MailAddress(sender), Subject = subject, Body = htmlMessage, IsBodyHtml = true };
            message.To.Add(email); await client.SendMailAsync(message); return true;
        }
        catch (SmtpException) { return false; }
    }
}
