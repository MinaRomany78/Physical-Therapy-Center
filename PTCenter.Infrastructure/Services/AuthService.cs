using Google.Apis.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PTCenter.Application.Abstractions;
using PTCenter.Application.Contracts.Authentication;
using PTCenter.Domain.Entities.Enums;
using PTCenter.Infrastructure.Persistence;
using System.Security.Cryptography;
using static System.Net.WebRequestMethods;

namespace PTCenter.Infrastructure.Services;

public class AuthService(UserManager<ApplicationUser> userManager,SignInManager<ApplicationUser> signInManager,IJwtProvider jwtProvider,ApplicationDbContext context,IEmailSender emailSender,IConfiguration configuration) : IAuthService 
{
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly SignInManager<ApplicationUser> _signInManager  = signInManager;
    private readonly IJwtProvider _jwtProvider = jwtProvider;
    private readonly ApplicationDbContext _context =context;
    private readonly IEmailSender _emailSender = emailSender;
    private readonly IConfiguration _configuration=configuration;

    public async Task<Result<AuthResponse>> GetTokenAsync(LoginRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.UserName)
        ?? await _userManager.FindByEmailAsync(request.UserName);
        if (user is null)
            return Result.Failure<AuthResponse>(UserErrors.InvalidCredentials);
        if (!user.IsActive) return Result.Failure<AuthResponse>(UserErrors.LockedUser);

        // منع تسجيل الدخول إذا لم يتم تأكيد البريد الإلكتروني
        if (!user.EmailConfirmed)
            return Result.Failure<AuthResponse>(UserErrors.EmailNotConfirmed);

        var result = await _signInManager.PasswordSignInAsync(user, request.Password, false, true);

        if (result.Succeeded)
        {
            var userRoles = await _userManager.GetRolesAsync(user);
            var jwtResult = _jwtProvider.GenerateToken(user, userRoles);

            var response = new AuthResponse(
                user.Id,
                user.UserName!,
                jwtResult.token,
                "",
                jwtResult.expiresIn,
                "User logged in successfully."
            );

            return Result.Success(response);
        }

        var error = result.IsLockedOut
            ? UserErrors.LockedUser
            : UserErrors.InvalidCredentials;

        return Result.Failure<AuthResponse>(error);
    }
    public async Task<Result> RegisterAsync(RegisterRequest request)
    {
        if ((await _userManager.FindByNameAsync(request.UserName) ??
            await _userManager.FindByEmailAsync(request.Email)) is not null)
        {
            return Result.Failure(UserErrors.UserAlreadyExists);
        }

        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            var user = new ApplicationUser
            {
                UserName = request.UserName,
                Email = request.Email,
                PhoneNumber = request.PhoneNumber,
                Gender = request.Gender,
                FullName = request.FullName,
                EmailConfirmed = false
            };

            var createResult = await _userManager.CreateAsync(user, request.Password);

            if (!createResult.Succeeded)
                return Result.Failure(UserErrors.RegistrationFailed);

            var roleResult = await _userManager.AddToRoleAsync(user, "Patient");

            if (!roleResult.Succeeded)
            {
                await _userManager.DeleteAsync(user);
                return Result.Failure(UserErrors.FailedToAssignRole);
            }

            var patient = new Patient
            {
                ApplicationUserId = user.Id
            };

            _context.Patients.Add(patient);

            var otp = RandomNumberGenerator
                .GetInt32(100000, 1000000)
                .ToString();

            _context.OTPVerifies.Add(new OTPVerify
            {
                ApplicationUserId = user.Id,
                OtpCode = otp,
                Type = OtpType.Register,

                CreatedAt = DateTime.UtcNow,
                IsUsed = false
            });

            await _context.SaveChangesAsync();

            
                 var emailSent = await _emailSender.SendEmailAsync(
                    user.Email!,
                    "Mediqova - Verify Your Email",
                    $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                <meta charset="UTF-8">
                <title>Mediqova Verification</title>
                </head>

                <body style="margin:0;padding:0;background:#f4f7fb;font-family:'Segoe UI',Arial,sans-serif;">

                <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f7fb;padding:40px 0;">
                <tr>
                <td align="center">

                <table width="600" cellpadding="0" cellspacing="0"
                style="background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 10px 30px rgba(0,0,0,.08);">

                <!-- Header -->
                <tr>
                <td style="background:linear-gradient(135deg,#0F4C81,#1E88E5);padding:40px;text-align:center;">

                <div style="
                width:72px;
                height:72px;
                margin:auto;
                background:#ffffff;
                border-radius:50%;
                line-height:72px;
                font-size:34px;">
                🏥
                </div>

                <h1 style="margin:20px 0 8px;color:white;font-size:32px;font-weight:bold;">
                Mediqova
                </h1>

                <p style="margin:0;color:#dbeafe;font-size:15px;">
                Powered by Clinitech Solutions
                </p>

                </td>
                </tr>

                <!-- Body -->
                <tr>
                <td style="padding:45px;">

                <h2 style="margin-top:0;color:#111827;">
                Welcome to Mediqova 👋
                </h2>

                <p style="font-size:16px;color:#4b5563;line-height:1.8;">
                Thank you for creating your account.
                To complete your registration, please verify your email address using the verification code below.
                </p>

                <div style="
                margin:35px auto;
                width:280px;
                background:#eff6ff;
                border:2px dashed #1E88E5;
                border-radius:14px;
                padding:22px;
                text-align:center;">

                <div style="
                font-size:42px;
                font-weight:bold;
                letter-spacing:12px;
                color:#0F4C81;">
                {otp}
                </div>

                </div>

                <p style="
                text-align:center;
                color:#dc2626;
                font-weight:600;
                font-size:15px;">
                ⏱ This verification code will expire in 5 minutes.
                </p>

                <p style="
                margin-top:35px;
                font-size:15px;
                color:#6b7280;
                line-height:1.8;">
                If you didn't create a Mediqova account, you can safely ignore this email.
                No further action is required.
                </p>

                </td>
                </tr>

                <!-- Footer -->
                <tr>
                <td style="
                background:#f9fafb;
                padding:25px;
                text-align:center;
                border-top:1px solid #e5e7eb;">

                <p style="
                margin:0;
                font-size:16px;
                font-weight:600;
                color:#111827;">
                Clinitech Solutions
                </p>

                <p style="
                margin:8px 0 0;
                font-size:13px;
                color:#6b7280;">
                © 2026 Mediqova. All rights reserved.
                </p>

                </td>
                </tr>

                </table>

                </td>
                </tr>
                </table>

                </body>
                </html>
                """

            );

            if (!emailSent)
            {
                await transaction.RollbackAsync();
                await _userManager.DeleteAsync(user);

                return Result.Failure(UserErrors.EmailSendingFailed);
            }

            await transaction.CommitAsync();

            return Result.Success("User registered successfully. OTP has been sent to your email.");
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
    public async Task<Result<AuthResponse>> VerifyRegisterAsync(VerifyRegisterRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.EmailORUserName)
               ?? await _userManager.FindByEmailAsync(request.EmailORUserName);

        if (user is null)
            return Result.Failure<AuthResponse>(UserErrors.InvalidCredentials);

        var otpRecord = await _context.OTPVerifies
            .Where(x => x.ApplicationUserId == user.Id && !x.IsUsed && x.Type == OtpType.Register)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();

        if (otpRecord is null)
            return Result.Failure<AuthResponse>(UserErrors.NoOtpFound);

        if ((DateTime.UtcNow - otpRecord.CreatedAt).TotalMinutes > 5)
            return Result.Failure<AuthResponse>(UserErrors.OtpExpired);

        if (otpRecord.OtpCode != request.OTP)
            return Result.Failure<AuthResponse>(UserErrors.OtpNotVerified);

        otpRecord.IsUsed = true;
        user.EmailConfirmed = true;

        var updateResult = await _userManager.UpdateAsync(user);

        if (!updateResult.Succeeded)
            return Result.Failure<AuthResponse>(UserErrors.FailedToUpdateUser);

        await _context.SaveChangesAsync();

        var roles = await _userManager.GetRolesAsync(user);

        var jwtResult = _jwtProvider.GenerateToken(user, roles);

        var response = new AuthResponse(
            user.Id,
            user.UserName!,
            jwtResult.token,
            string.Empty,
            jwtResult.expiresIn,
            "Email verified successfully."
        );

        return Result.Success(response);
    }
    public async Task<Result> ResendOtpAsync(ResendOtpRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.EmailORUserName)
               ?? await _userManager.FindByEmailAsync(request.EmailORUserName);

        if (user is null)
            return Result.Failure(UserErrors.InvalidCredentials);

        // لو الإيميل متفعل بالفعل
        if (user.EmailConfirmed)
            return Result.Failure(UserErrors.EmailAlreadyConfirmed);

        // إلغاء صلاحية أي OTP قديم
        var oldOtps = await _context.OTPVerifies
            .Where(x => x.ApplicationUserId == user.Id && !x.IsUsed && x.Type == OtpType.Register)
            .ToListAsync();

        foreach (var otp in oldOtps)
            otp.IsUsed = true;

        // إنشاء OTP جديد
        var newOtp = RandomNumberGenerator
            .GetInt32(100000, 1000000)
            .ToString();

        _context.OTPVerifies.Add(new OTPVerify
        {
            ApplicationUserId = user.Id,
            OtpCode = newOtp,
            Type = OtpType.Register,

            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        });

        await _context.SaveChangesAsync();

        var emailSent = await _emailSender.SendEmailAsync(
            user.Email!,
            "Mediqova - OTP Verification",
            $"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Mediqova Verification</title>
</head>

<body style="margin:0;padding:0;background:#f4f7fb;font-family:Segoe UI,Arial,sans-serif;">

<table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f7fb;padding:40px 0;">
<tr>
<td align="center">

<table width="600" cellpadding="0" cellspacing="0"
style="background:#ffffff;border-radius:18px;overflow:hidden;
box-shadow:0 10px 30px rgba(0,0,0,.08);">

<!-- Header -->
<tr>
<td style="background:linear-gradient(135deg,#0F4C81,#1E88E5);padding:35px;text-align:center;">

<div style="
width:72px;
height:72px;
margin:auto;
background:white;
border-radius:50%;
line-height:72px;
font-size:34px;">
🏥
</div>

<h1 style="color:white;margin:20px 0 5px;font-size:30px;">
Mediqova
</h1>

<p style="color:#dbeafe;margin:0;font-size:15px;">
Powered by Clinitech Solutions
</p>

</td>
</tr>

<!-- Body -->
<tr>
<td style="padding:40px;">

<h2 style="margin-top:0;color:#111827;">
OTP Verification
</h2>

<p style="font-size:16px;color:#4b5563;line-height:1.8;">
Hello,
</p>

<p style="font-size:16px;color:#4b5563;line-height:1.8;">
You requested a new verification code for your <strong>Mediqova</strong> account.
Please use the code below to complete your verification.
</p>

<div style="
margin:35px auto;
width:270px;
background:#eef6ff;
border:2px dashed #1E88E5;
border-radius:14px;
padding:22px;
text-align:center;">

<span style="
font-size:42px;
font-weight:bold;
letter-spacing:12px;
color:#0F4C81;">
{newOtp}
</span>

</div>

<p style="text-align:center;color:#dc2626;font-weight:600;font-size:15px;">
⏱ This verification code will expire in 5 minutes.
</p>

<hr style="border:none;border-top:1px solid #e5e7eb;margin:35px 0;">

<p style="font-size:14px;color:#6b7280;line-height:1.8;">
If you didn't request this verification code, you can safely ignore this email.
Your account will remain secure.
</p>

</td>
</tr>

<!-- Footer -->
<tr>
<td style="background:#f9fafb;padding:25px;text-align:center;">

<p style="margin:0;font-size:15px;font-weight:600;color:#111827;">
Clinitech Solutions
</p>

<p style="margin:8px 0 0;color:#6b7280;font-size:13px;">
© 2026 Mediqova. All rights reserved.
</p>

</td>
</tr>

</table>

</td>
</tr>
</table>

</body>
</html>
"""
        );

        if (!emailSent)
            return Result.Failure(UserErrors.EmailSendingFailed);

        return Result.Success("A new OTP has been sent to your email.");
    }
    public async Task<Result<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdToken))
            return Result.Failure<AuthResponse>(UserErrors.InvalidGoogleToken);

        GoogleJsonWebSignature.Payload payload;

        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(
                request.IdToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[]
                    {
                    _configuration["GoogleAuthSettings:ClientId"]
                    }
                });
        }
        catch
        {
            return Result.Failure<AuthResponse>(UserErrors.InvalidGoogleToken);
        }

        var user = await _userManager.FindByEmailAsync(payload.Email);

        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = payload.Email.Split('@')[0],
                Email = payload.Email,
                FullName = payload.Name,
                EmailConfirmed = true
            };

            var createResult = await _userManager.CreateAsync(user);

            if (!createResult.Succeeded)
                return Result.Failure<AuthResponse>(UserErrors.RegistrationFailed);

            var roleResult = await _userManager.AddToRoleAsync(user, "Patient");

            if (!roleResult.Succeeded)
            {
                await _userManager.DeleteAsync(user);
                return Result.Failure<AuthResponse>(UserErrors.FailedToAssignRole);
            }

            _context.Patients.Add(new Patient
            {
                ApplicationUserId = user.Id
            });

            await _context.SaveChangesAsync();
        }

        var roles = await _userManager.GetRolesAsync(user);

        var jwt = _jwtProvider.GenerateToken(user, roles);

        var response = new AuthResponse(
            user.Id,
            user.UserName!,
            jwt.token,
            "",
            jwt.expiresIn,
            "Google login successful."
        );

        return Result.Success(response);
    }
    public async Task<Result> VerifyForgotPasswordOtpAsync(VerifyForgotPasswordRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.EmailOrUserName)
               ?? await _userManager.FindByEmailAsync(request.EmailOrUserName);

        if (user is null)
            return Result.Failure(UserErrors.UserNotFound);

        var otpRecord = await _context.OTPVerifies
            .Where(x => x.ApplicationUserId == user.Id
                     && !x.IsUsed
                     && x.Type == OtpType.ForgotPassword)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();

        if (otpRecord is null)
            return Result.Failure(UserErrors.NoOtpFound);

        if ((DateTime.UtcNow - otpRecord.CreatedAt).TotalMinutes > 5)
            return Result.Failure(UserErrors.OtpExpired);

        if (otpRecord.OtpCode != request.OTP)
            return Result.Failure(UserErrors.OtpNotVerified);
        otpRecord.IsUsed = true;
        await _context.SaveChangesAsync();

        return Result.Success("OTP verified successfully.");
    }
    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.EmailOrUserName)
               ?? await _userManager.FindByEmailAsync(request.EmailOrUserName);

        if (user is null)
            return Result.Failure(UserErrors.UserNotFound);

        var otpRecord = await _context.OTPVerifies
            .Where(x => x.ApplicationUserId == user.Id
                     && !x.IsUsed
                     && x.Type == OtpType.ForgotPassword)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();

        if (otpRecord is null)
            return Result.Failure(UserErrors.NoOtpFound);

        if ((DateTime.UtcNow - otpRecord.CreatedAt).TotalMinutes > 5)
            return Result.Failure(UserErrors.OtpExpired);

        if (otpRecord.OtpCode != request.OTP)
            return Result.Failure(UserErrors.OtpNotVerified);

        otpRecord.IsUsed = true;

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);

        var result = await _userManager.ResetPasswordAsync(
            user,
            token,
            request.NewPassword);

        if (!result.Succeeded)
            return Result.Failure(UserErrors.FailedToUpdateUser);

        await _context.SaveChangesAsync();

        return Result.Success("Password changed successfully.");
    }
    public async Task<Result> ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        var user = await _userManager.FindByNameAsync(request.EmailOrUserName)
               ?? await _userManager.FindByEmailAsync(request.EmailOrUserName);

        if (user is null)
            return Result.Failure(UserErrors.UserNotFound);

        var oldOtps = await _context.OTPVerifies
            .Where(x => x.ApplicationUserId == user.Id
                     && !x.IsUsed
                     && x.Type == OtpType.ForgotPassword)
            .ToListAsync();

        foreach (var otp in oldOtps)
            otp.IsUsed = true;

        var code = RandomNumberGenerator
            .GetInt32(100000, 1000000)
            .ToString();

        _context.OTPVerifies.Add(new OTPVerify
        {
            ApplicationUserId = user.Id,
            OtpCode = code,
            Type = OtpType.ForgotPassword,
            CreatedAt = DateTime.UtcNow,
            IsUsed = false
        });

        await _context.SaveChangesAsync();

        var emailResult = await _emailSender.SendEmailAsync(
       user.Email!,
       "Mediqova - Reset Your Password",
       $"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Reset Password</title>
</head>

<body style="margin:0;padding:0;background:#f4f7fb;font-family:'Segoe UI',Arial,sans-serif;">

<table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f7fb;padding:40px 0;">
<tr>
<td align="center">

<table width="600" cellpadding="0" cellspacing="0"
style="background:#ffffff;border-radius:18px;overflow:hidden;box-shadow:0 10px 30px rgba(0,0,0,.08);">

<!-- Header -->
<tr>
<td style="background:linear-gradient(135deg,#0F4C81,#1E88E5);padding:40px;text-align:center;">

<div style="
width:72px;
height:72px;
margin:auto;
background:#ffffff;
border-radius:50%;
line-height:72px;
font-size:34px;">
🔒
</div>

<h1 style="margin:20px 0 8px;color:white;font-size:32px;font-weight:bold;">
Mediqova
</h1>

<p style="margin:0;color:#dbeafe;font-size:15px;">
Powered by Clinitech Solutions
</p>

</td>
</tr>

<!-- Body -->
<tr>
<td style="padding:45px;">

<h2 style="margin-top:0;color:#111827;">
Reset Your Password
</h2>

<p style="font-size:16px;color:#4b5563;line-height:1.8;">
We received a request to reset the password for your <strong>Mediqova</strong> account.
</p>

<p style="font-size:16px;color:#4b5563;line-height:1.8;">
Use the verification code below to continue:
</p>

<div style="
margin:35px auto;
width:280px;
background:#eff6ff;
border:2px dashed #1E88E5;
border-radius:14px;
padding:22px;
text-align:center;">

<div style="
font-size:42px;
font-weight:bold;
letter-spacing:12px;
color:#0F4C81;">
{code}
</div>

</div>

<p style="
text-align:center;
color:#dc2626;
font-weight:600;
font-size:15px;">
⏱ This reset code will expire in 5 minutes.
</p>

<p style="
margin-top:35px;
font-size:15px;
color:#6b7280;
line-height:1.8;">
If you didn't request a password reset, you can safely ignore this email.
Your password will remain unchanged unless you complete the reset process.
</p>

</td>
</tr>

<!-- Footer -->
<tr>
<td style="
background:#f9fafb;
padding:25px;
text-align:center;
border-top:1px solid #e5e7eb;">

<p style="
margin:0;
font-size:16px;
font-weight:600;
color:#111827;">
Clinitech Solutions
</p>

<p style="
margin:8px 0 0;
font-size:13px;
color:#6b7280;">
© 2026 Mediqova. All rights reserved.
</p>

</td>
</tr>

</table>

</td>
</tr>
</table>

</body>
</html>
"""
   );

        if (!emailResult)
            return Result.Failure(UserErrors.EmailSendingFailed);

        return Result.Success("OTP has been sent successfully.");
    }

}
