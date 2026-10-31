using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using PTCenter.Application.Interfaces;
using PTCenter.Application.Services;
using PTCenter.Domain.Entities;
using PTCenter.Infrastructure.Options;
using PTCenter.Infrastructure.Persistence;
using PTCenter.Infrastructure.Services;
using System.Security.Claims;
using System.Text;

namespace PTCenter.Infrastructure;

public static class InfrastructureExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddInfrastructureExtensions(IConfiguration configuration)
        {

            services
                .AddServices()
                .AddAuthConfig(configuration)
                .AddDataBaseConfiguration(configuration);
            
            return services;
        }

        private IServiceCollection AddServices()
        {
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IPatientService, PatientService>();
            services.AddScoped<IInsuranceCompanyService, InsuranceCompanyService>();
            services.AddScoped<IInsuranceDocumentService, InsuranceDocumentService>();
            services.AddScoped<ITreatmentPlanService, TreatmentPlanService>();
            services.AddScoped<ITreatmentPlanSessionService, TreatmentPlanSessionService>();
            services.AddScoped<IAssessmentService, AssessmentService>();
            services.AddScoped<IAppointmentService, AppointmentService>();
            services.AddScoped<BookingService>();
            services.AddScoped<DoctorWorkspaceService>();
            services.AddScoped<ReassessmentService>();
            services.AddScoped<IReceptionistService, ReceptionistService>();
            services.AddScoped<IBillingService, BillingService>();
            services.AddScoped<IRoomService, RoomService>();
            return services;
        }

        private IServiceCollection AddDataBaseConfiguration(IConfiguration configuration)
        {
            services.AddDbContext<ApplicationDbContext>(x => x.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));
            
            return services;
        }
        
        private IServiceCollection AddAuthConfig(IConfiguration configuration)
        {
            services.AddIdentity<ApplicationUser, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            services.AddScoped<IAuthService, AuthService>();
            services.AddSingleton<IJwtProvider, JwtProvider>();
            services.AddTransient<IEmailSender, EmailSender>();
            

            services.AddOptions<JwtOptions>()
                .BindConfiguration(JwtOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            var jwtSettings = configuration
                .GetRequiredSection(JwtOptions.SectionName)
                .Get<JwtOptions>()!;


            services.AddAuthentication(options =>
                {
                    // For [Authorize] know That We Use Bearer
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(o =>
                {
                    o.SaveToken = true;
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(jwtSettings.Key)
                        ),

                        ValidateIssuer = true,
                        ValidIssuer = jwtSettings.Issuer,

                        ValidateAudience = true,
                        ValidAudience = jwtSettings.Audience,

                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero,

                        RoleClaimType = ClaimTypes.Role
                    };


                });


            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequiredLength = 8;
                options.SignIn.RequireConfirmedEmail = false;
                options.User.RequireUniqueEmail = false;
            });

            return services;
        }
    }
}
