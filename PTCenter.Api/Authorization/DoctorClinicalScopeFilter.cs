using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using PTCenter.Application.Contracts.Appointments;
using PTCenter.Infrastructure.Persistence;

namespace PTCenter.Api.Authorization;

// Legacy clinical routes must not bypass the assigned-patient workspace.
public class DoctorClinicalScopeFilter(ApplicationDbContext db) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (!user.IsInRole("Doctor") || new[] { "Ceo", "Admin", "Owner", "Senior", "Receptionist" }.Any(user.IsInRole)) { await next(); return; }
        var actor = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var controller = context.RouteData.Values["controller"]?.ToString();
        var action = context.RouteData.Values["action"]?.ToString();
        if (controller is not ("Patients" or "Assessments" or "TreatmentPlans" or "TreatmentPlanSessions" or "Appointments" or "Bookings" or "CourseFinance" or "Billing" or "InsuranceDocuments" or "Receptionist")) { await next(); return; }
        var allowed = context.HttpContext.Request.Method == "GET";
        int? Number(string name) => context.ActionArguments.TryGetValue(name, out var v) && v is int i ? i : null;
        int? patientId = Number("patientId");
        var id = Number("id");
        if (controller == "Appointments" && action == "GetAll" && context.ActionArguments.Values.OfType<AppointmentQueryRequest>().FirstOrDefault() is { } query)
        { query.DoctorId = actor; await next(); return; }
        if (controller == "Patients" && action == "GetById") patientId = id;
        else if (controller == "Assessments" && id != null) patientId = await db.Assessments.Where(a => a.Id == id).Select(a => (int?)a.PatientId).SingleOrDefaultAsync();
        else if (controller == "TreatmentPlans" && id != null) patientId = await db.TreatmentPlans.Where(p => p.Id == id).Select(p => (int?)p.Assessment.PatientId).SingleOrDefaultAsync();
        else if (controller == "TreatmentPlanSessions")
        {
            if (id != null) patientId = await db.TreatmentPlanSessions.Where(s => s.Id == id).Select(s => (int?)s.TreatmentPlan.Assessment.PatientId).SingleOrDefaultAsync();
            else if (Number("treatmentPlanId") is int plan) patientId = await db.TreatmentPlans.Where(p => p.Id == plan).Select(p => (int?)p.Assessment.PatientId).SingleOrDefaultAsync();
        }
        else if (controller == "Appointments" || controller == "CourseFinance" && action == "Gate")
        {
            allowed &= id != null && await db.Appointments.AnyAsync(a => a.Id == id && a.DoctorId == actor);
            if (allowed) { await next(); return; }
        }
        else if (controller == "Bookings")
        {
            if (action is "Context" or "Profile") patientId = id;
            else allowed = false;
        }
        else if (controller == "InsuranceDocuments" && id != null) patientId = await db.InsuranceDocuments.Where(d => d.Id == id).Select(d => (int?)d.PatientId).SingleOrDefaultAsync();
        else if (controller is "Billing" or "CourseFinance" or "Receptionist") allowed = false;
        allowed &= patientId != null && (await db.TreatmentPlans.AnyAsync(p => p.DoctorId == actor && p.Assessment.PatientId == patientId) || await db.Appointments.AnyAsync(a => a.DoctorId == actor && a.PatientId == patientId));
        if (!allowed)
        {
            context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "Doctor.AccessDenied", Detail = "Use your assigned clinical workspace. This record or operation is not available to your Doctor account." }) { StatusCode = 403 };
            return;
        }
        await next();
    }
}
