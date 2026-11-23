using System.Net;
using System.Net.Mail;

namespace KitBuilder.Api.Services;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody);
    Task SendNewUserNotificationAsync(string userName, string userEmail, string loginMethod);
    Task SendWelcomeEmailAsync(string userName, string userEmail);
    Task<(int sent, int failed)> SendNewsletterAsync(string subject, string htmlBody, IEnumerable<(string Email, string Name)> recipients);
    string BuildNewsletterHtml(string subject, string htmlBody, string? recipientName = null);
    Task SendInviteEmailAsync(string toEmail, string inviterName, string inviteUrl);
    Task SendUpgradeRequestEmailAsync(string orgName, string plan, string requesterName, string requesterEmail, string? message);
}

public class EmailService : IEmailService
{

    // The address this app is served from (env App__PublicUrl), used in links it sends out.

    private static string PublicUrl => (Environment.GetEnvironmentVariable("App__PublicUrl") ?? "http://localhost").TrimEnd('/');

    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendNewUserNotificationAsync(string userName, string userEmail, string loginMethod)
    {
        var adminEmail = _configuration["Email:AdminEmail"] ?? "";
        var subject = $"GenPrompt - New User Registered: {userName}";
        var body = $@"
<html>
<body style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;"">
    <div style=""background: linear-gradient(135deg, #0d9488, #475569); padding: 24px; border-radius: 12px 12px 0 0; text-align: center;"">
        <h1 style=""color: white; margin: 0; font-size: 24px;"">New User Registration</h1>
    </div>
    <div style=""background: #f8fafc; padding: 24px; border: 1px solid #e2e8f0; border-top: none; border-radius: 0 0 12px 12px;"">
        <p style=""color: #0f172a; font-size: 16px;"">A new user has registered on <strong>GenPrompt</strong>:</p>
        <table style=""width: 100%; border-collapse: collapse; margin: 16px 0;"">
            <tr>
                <td style=""padding: 8px 12px; color: #64748b; font-weight: 600;"">Name:</td>
                <td style=""padding: 8px 12px; color: #0f172a;"">{userName}</td>
            </tr>
            <tr style=""background: #ffffff;"">
                <td style=""padding: 8px 12px; color: #64748b; font-weight: 600;"">Email:</td>
                <td style=""padding: 8px 12px; color: #0f172a;"">{userEmail}</td>
            </tr>
            <tr>
                <td style=""padding: 8px 12px; color: #64748b; font-weight: 600;"">Method:</td>
                <td style=""padding: 8px 12px; color: #0f172a;"">{loginMethod}</td>
            </tr>
            <tr style=""background: #ffffff;"">
                <td style=""padding: 8px 12px; color: #64748b; font-weight: 600;"">Time:</td>
                <td style=""padding: 8px 12px; color: #0f172a;"">{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</td>
            </tr>
        </table>
    </div>
</body>
</html>";

        await SendEmailAsync(adminEmail, subject, body);
    }

    public async Task SendUpgradeRequestEmailAsync(string orgName, string plan, string requesterName, string requesterEmail, string? message)
    {
        var adminEmail = _configuration["Email:AdminEmail"] ?? "";
        var enc = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var subject = $"GenPrompt — Upgrade request: {orgName}";
        var body = $@"
<html><body style=""font-family:-apple-system,Arial,sans-serif;max-width:560px;margin:0 auto;color:#111827"">
  <div style=""background:linear-gradient(135deg,#fa7e1e,#d62976 55%,#833ab4);padding:20px 24px;border-radius:12px 12px 0 0"">
    <h2 style=""color:#fff;margin:0;font-size:18px"">Upgrade request</h2>
  </div>
  <div style=""padding:22px 24px;border:1px solid #e5e7eb;border-top:none"">
    <p style=""margin:0 0 8px""><strong>Organization:</strong> {enc(orgName)}</p>
    <p style=""margin:0 0 8px""><strong>Current plan:</strong> {enc(plan)}</p>
    <p style=""margin:0 0 8px""><strong>Requested by:</strong> {enc(requesterName)} &lt;{enc(requesterEmail)}&gt;</p>
    <p style=""margin:12px 0 4px""><strong>Message:</strong></p>
    <p style=""margin:0;white-space:pre-wrap;color:#334155"">{(string.IsNullOrWhiteSpace(message) ? "(no message)" : enc(message))}</p>
    <hr style=""border:0;border-top:1px solid #e5e7eb;margin:18px 0"">
    <p style=""color:#94a3b8;font-size:13px;margin:0"">Change their plan/seats in Admin → Business insights.</p>
  </div>
</body></html>";
        await SendEmailAsync(adminEmail, subject, body);
    }

    public async Task SendWelcomeEmailAsync(string userName, string userEmail)
    {
        var subject = "Welcome to GenPrompt — your AI prompt operating system";
        var body = $@"
<html>
<body style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; background-color: #f8fafc;"">
    <div style=""background: linear-gradient(135deg, #fa7e1e, #d62976 55%, #833ab4); padding: 32px; border-radius: 12px 12px 0 0; text-align: center;"">
        <h1 style=""color: white; margin: 0; font-size: 28px;"">Welcome to GenPrompt</h1>
        <p style=""color: rgba(255,255,255,0.9); margin: 8px 0 0; font-size: 14px; letter-spacing: 0.5px;"">YOUR AI PROMPT OPERATING SYSTEM</p>
    </div>
    <div style=""background: #ffffff; padding: 32px; border: 1px solid #e2e8f0; border-top: none;"">
        <p style=""color: #0f172a; font-size: 18px; margin-top: 0;"">Hi {userName},</p>
        <p style=""color: #475569; font-size: 15px; line-height: 1.6;"">
            Thanks for joining GenPrompt! You've got the full toolkit — go from a rough idea to a
            polished prompt, and turn documents, calls, and messy data into finished work, all in one place.
        </p>
        <h3 style=""color: #0f172a; font-size: 16px; margin-top: 24px;"">Here's what you can do:</h3>
        <ul style=""color: #475569; font-size: 15px; line-height: 1.9; padding-left: 20px;"">
            <li><strong>Prompt Studio</strong> — describe your intent, get a clear structured prompt, then refine and A/B test variants for ChatGPT, Claude, Gemini &amp; Perplexity</li>
            <li><strong>Skills &amp; Personas</strong> — apply 40+ ready-made AI skill packs and expert personas</li>
            <li><strong>Workflows &amp; Agents</strong> — chain prompts into workflows and run autonomous AI agents</li>
            <li><strong>Snappdf</strong> — convert, merge, split, compress, OCR &amp; edit PDFs</li>
            <li><strong>Signal</strong> — turn call &amp; meeting recordings (or transcripts) into summaries and structured insights</li>
            <li><strong>Prism</strong> — auto-clean, map &amp; transform messy CSV, Excel, JSON &amp; PDF data</li>
            <li><strong>Prompt Library</strong> — save, organize, and reuse your best prompts anywhere</li>
        </ul>
        <div style=""text-align: center; margin: 32px 0;"">
            <a href=""{PublicUrl}/dashboard"" style=""background: linear-gradient(135deg, #fa7e1e, #d62976 55%, #833ab4); color: white; padding: 14px 32px; border-radius: 8px; text-decoration: none; font-weight: 700; font-size: 16px; display: inline-block;"">Go to Dashboard</a>
        </div>
        <p style=""color: #94a3b8; font-size: 13px; text-align: center;"">
            Running a team? <a href=""{PublicUrl}/signup-business"" style=""color: #d62976;"">Create an organization</a> for shared seats, roles, and API access.
        </p>
        <p style=""color: #94a3b8; font-size: 13px; text-align: center;"">
            Questions? Reply to this email or visit our <a href=""{PublicUrl}/contact"" style=""color: #d62976;"">Contact page</a>.
        </p>
    </div>
    <div style=""text-align: center; padding: 16px; color: #94a3b8; font-size: 12px;"">
        &copy; {DateTime.UtcNow.Year} GenPrompt. All rights reserved.
    </div>
</body>
</html>";

        await SendEmailAsync(userEmail, subject, body);
    }

    public async Task SendInviteEmailAsync(string toEmail, string inviterName, string inviteUrl)
    {
        var subject = $"{inviterName} invited you to GenPrompt";
        var body = $@"
<html>
<body style=""font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; background: #f8fafc;"">
  <div style=""background: linear-gradient(135deg, #0d9488, #475569); padding: 32px; border-radius: 12px 12px 0 0; text-align: center;"">
    <h1 style=""color: white; margin: 0; font-size: 26px;"">You're invited to GenPrompt ⚡</h1>
  </div>
  <div style=""background: #fff; padding: 32px; border: 1px solid #e2e8f0; border-top: none; border-radius: 0 0 12px 12px;"">
    <p style=""color: #0f172a; font-size: 16px; margin-top: 0;"">Hey there,</p>
    <p style=""color: #475569; font-size: 15px; line-height: 1.6;"">
      <strong>{inviterName}</strong> invited you to try <strong>GenPrompt</strong> — the free AI prompt management platform for ChatGPT, Claude, Gemini, and more.
    </p>
    <ul style=""color: #475569; font-size: 14px; line-height: 2; padding-left: 20px;"">
      <li>Build structured AI prompts in seconds</li>
      <li>Browse 40+ pre-built AI skill packs</li>
      <li>A/B test prompt variants and track results</li>
      <li>Save and reuse your best prompts anywhere</li>
    </ul>
    <div style=""text-align: center; margin: 32px 0;"">
      <a href=""{inviteUrl}"" style=""background: #0d9488; color: white; padding: 14px 36px; border-radius: 8px; text-decoration: none; font-weight: 700; font-size: 16px; display: inline-block;"">Accept Invite — It's Free</a>
    </div>
    <p style=""color: #94a3b8; font-size: 12px; text-align: center;"">No credit card required. Free forever.</p>
  </div>
  <div style=""text-align: center; padding: 16px; color: #94a3b8; font-size: 12px;"">
    &copy; {DateTime.UtcNow.Year} GenPrompt &mdash; <a href=""{PublicUrl}"" style=""color: #0d9488;"">this app</a>
  </div>
</body>
</html>";
        await SendEmailAsync(toEmail, subject, body);
    }

    public async Task<(int sent, int failed)> SendNewsletterAsync(string subject, string htmlBody, IEnumerable<(string Email, string Name)> recipients)
    {
        int sent = 0, failed = 0;
        foreach (var (email, name) in recipients)
        {
            // Wrap the pasted HTML into a full, branded, email-client-safe document
            // (a bare fragment renders unstyled in most inboxes), personalising {{name}}.
            var richEmail = BuildNewsletterHtml(subject, htmlBody, name);
            try
            {
                await SendEmailAsync(email, subject, richEmail);
                sent++;
            }
            catch
            {
                failed++;
            }
        }
        return (sent, failed);
    }

    /// <summary>
    /// Turn pasted newsletter HTML into a rich, ready-to-send email.
    /// Replaces the {{name}} token, then — if the body is a bare HTML fragment —
    /// wraps it in a branded, responsive, inline-styled document so it renders
    /// consistently across email clients. Content that is already a complete
    /// document (starts with &lt;!doctype&gt; or &lt;html&gt;) is sent exactly as written.
    /// </summary>
    public string BuildNewsletterHtml(string subject, string htmlBody, string? recipientName = null)
    {
        var greeting = string.IsNullOrWhiteSpace(recipientName) ? "there" : recipientName.Trim();
        var body = (htmlBody ?? string.Empty).Replace("{{name}}", greeting);

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            // Author supplied a full document — respect it verbatim.
            return body;
        }

        var safeSubject = System.Net.WebUtility.HtmlEncode(subject ?? string.Empty);
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <title>{safeSubject}</title>
</head>
<body style=""margin:0;padding:24px;background:#f8fafc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#334155;line-height:1.7"">
  <div style=""max-width:640px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;box-shadow:0 4px 18px rgba(15,23,42,0.08)"">
    <div style=""background:linear-gradient(135deg,#fa7e1e,#d62976 55%,#833ab4);padding:22px 32px"">
      <span style=""color:#ffffff;font-size:20px;font-weight:800;letter-spacing:-0.3px"">GenPrompt</span>
    </div>
    <div style=""padding:32px;font-size:15px;color:#334155;line-height:1.7"">
      {body}
    </div>
    <div style=""background:#f9fafb;border-top:1px solid #eef2f7;padding:18px 32px;text-align:center"">
      <p style=""color:#94a3b8;font-size:12px;margin:0"">
        &copy; {DateTime.UtcNow.Year} GenPrompt &mdash;
        <a href=""{PublicUrl}"" style=""color:#d62976;text-decoration:none"">this app</a>
      </p>
    </div>
  </div>
</body>
</html>";
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        var smtpHost = _configuration["Email:SmtpHost"] ?? "smtp.gmail.com";
        var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
        var smtpUser = _configuration["Email:SmtpUser"] ?? "";
        var smtpPass = _configuration["Email:SmtpPassword"] ?? "";
        var fromEmail = _configuration["Email:FromEmail"] ?? smtpUser;
        var fromName = _configuration["Email:FromName"] ?? "GenPrompt";

        if (string.IsNullOrEmpty(smtpUser) || string.IsNullOrEmpty(smtpPass))
        {
            _logger.LogWarning("SMTP not configured. Email not sent to {To}: {Subject}", toEmail, subject);
            return;
        }

        try
        {
            using var client = new SmtpClient(smtpHost, smtpPort)
            {
                Credentials = new NetworkCredential(smtpUser, smtpPass),
                EnableSsl = true
            };

            var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true
            };
            message.To.Add(toEmail);

            await client.SendMailAsync(message);
            _logger.LogInformation("Email sent to {To}: {Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}: {Subject}", toEmail, subject);
        }
    }
}
