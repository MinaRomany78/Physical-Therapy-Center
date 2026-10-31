# Doctor clinical workspace - 2026-10-31

## Authorized migration applied - 2026-10-31

The user's subsequent database-migration request authorized only the delivered DoctorClinicalWorkspace migration on the previously confirmed target. Following a fresh target/history/generated-SQL check, `20261031164400_DoctorClinicalWorkspace` was applied to `db56823.public.databaseasp.net / db56823`. History, zero pending migrations and clinical schema are verified; existing counts/financial totals are unchanged. Current-source clinical/profile/dashboard/invoice SQL Server reads pass. See [application report](Database/ReviewedMigrations/20261031164400_DoctorClinicalWorkspace.APPLIED.md). This update supersedes the unapplied/approval blocker below, which describes the implementation stage. No shared test records, application restart, push or deployment.

## Lifecycle and invariants

Appointments retain Reception ActualCheckIn/ActualCheckOut and gain nullable TreatmentStartedAt/EndedAt and treatment actor fields. Arrival alone leaves the treatment session Pending. Doctor Start requires saved arrival, assigned access, current clinical eligibility, capacity and patient coverage. Doctor End atomically finalizes TreatmentSessionRecord, ends treatment, marks session completed, records invoice delivery and increments plan completion once. Checkout requires explicit clinical end for ordinary treatment, records departure and releases room occupancy; it does not complete or deliver again. Initial assessment handling remains separate. Existing completed clinical state cannot be reset by a new arrival.

No historical treatment times are inferred from attendance or scheduled times. Legacy completed sessions remain read-only with unknown actual treatment/author times and existing notes as fallback. Final clinical records cannot be overwritten through old session mutation routes.

ClinicalCapacity evaluates half-open intervals across the whole candidate window. Doctor capacity is two with distinct available rooms; Senior assessments remain exclusive. Patient/room conflicts, actual overdue occupancy, working hours, absences and gender eligibility remain authoritative. Preview, bulk commit, reschedule and actual Start share these rules. ClinicMutationLock uses a transaction-owned SQL Server sp_getapplock on PTCenter:Scheduling; serializable SQLite transactions serialize test writers. Lock/conflict/deadlock failures return actionable conflicts rather than simulated success.

Notes use a numeric optimistic version, saved author/time, immutable finalization and exact payload replay. Draft writes never complete sessions. Concurrent stale edits return 409. End saves all final fields and progress in the same transaction. Patient coverage uses consumed clinical sessions plus the upcoming session; insurer outstanding is kept separate. New arrivals alone are not counted as delivered treatment, and existing legacy completed/in-progress clinical status remains safe. No payment amounts, allocations, agreements or insurer approvals are changed by reassessment/rescheduling.

## Internal Senior review

The latest user policy replaces the original attachment's priced/scheduled reassessment proposal. Request -> InReview -> Completed is an internal same-visit clinical flow, with reasoned cancellation. There is no reassessment appointment, price, fee, invoice, payment or claim and no insurance/pricing prerequisite. Gender-matching eligible Seniors can claim pending requests directly. A pending request stays visible until someone reviews or cancels it.

Request operation keys and a unique active-plan index prevent duplicate active requests. Versions/transaction locking protect competing claims and completion; completion retries require matching original payload. Owner/reviewer authorization is checked before replay. Completion creates a separate Assessment and ReAssessment history record. Revise optionally creates a new linked plan/sessions while preserving the old plan, bookings and money. ReviewRequired surfaces for original/revised plans when appropriate. Continue/Pause/Discharge recommendations do not silently change bookings, payments or clinical holds.

## API and permissions

| Endpoint | Permission |
| --- | --- |
| GET /api/Doctor/schedule?date=YYYY-MM-DD | Doctor's own schedule; explicit supported supervisor access may select doctorId. All active overdue treatments remain visible. |
| GET /api/Doctor/appointments/{id} | Assigned Doctor or supported supervisor; real assessment/plan/previous notes/document metadata. |
| POST /api/Doctor/appointments/{id}/start | Assigned Doctor/supervisor, persisted arrival, financial/clinical/capacity recheck. |
| PUT /api/Doctor/appointments/{id}/notes | Assigned Doctor/supervisor, active treatment, expectedVersion, draft fields. |
| POST /api/Doctor/appointments/{id}/end | Assigned Doctor/supervisor, versioned atomic final notes + clinical completion. |
| GET /api/Doctor/appointments/{id}/documents/{documentId} | Scoped assigned clinical document download. |
| GET /api/ReassessmentRequests and /{id} | Assigned Doctor; eligible Senior/request reviewer; authorized Reception/supervisors. Detail loads history on demand. |
| POST /api/ReassessmentRequests/appointments/{id} | Assigned Doctor or supported supervisor; reason/notes/recommendation/operationKey. |
| POST /api/ReassessmentRequests/{id}/start | Eligible Senior/supported supervisor; versioned claim. |
| POST /api/ReassessmentRequests/{id}/complete | Reviewing Senior/supported supervisor; versioned clinical decision. |
| POST /api/ReassessmentRequests/{id}/cancel | Requester/reviewer or authorized operational staff, with reason/version. |

Existing JWT authentication remains mandatory. New clinical endpoint 401/403 and business errors use ProblemDetails. Legacy clinical reads are scoped for Doctor-only accounts; global/admin writes to prescriptions, assessments, attendance, payments and finance are denied. Existing staff-account administration stays restricted; no Users/GetAll workaround. Explicit multi-role operational/supervisor permissions are retained. Reception can record arrival/departure but cannot impersonate Doctor treatment start/end. Assigned Seniors retain initial assessment attendance responsibility.

## Validation

- Full backend: **177 passed, 0 failed**, current source. Command: `dotnet test PTCenter.Tests/PTCenter.Tests.csproj --artifacts-path .tools/doctor-tests -p:NuGetAudit=false`. Artifacts path avoids the user-owned API's locked build output. Log `.tools/doctor-final-backend-tests.log`.
- Tests cover assigned scope and wrong-role denial with real Identity/JWT; whole-interval peaks and partial/bulk conflicts; actual overdue treatment; concurrent start/booking/reschedule/claim attempts; exact patient shortfall vs insurer balance; draft conflicts/replay/immutable finals; distinct arrival/start/end/departure and exactly-once progress; financially independent unapproved-insurance review and traceable revised plans; historical notes/unknown timestamps.
- Relational/HTTP fixtures use temporary SQLite databases and EnsureCreated. Offline SQL Server model and generated forward-script checks pass. No claim is made that SQL Server migrations/app locks were executed locally.
- Actual Angular/Chrome workflow uses matching rebuilt ASP.NET TestServer, isolated SQLite and real Doctor/Senior/Reception logins. HTTP interception only redirects browser traffic to this local backend, not mocked business responses. Verified reload draft, two concurrent treatments, exact EGP 100 patient shortfall, insurer balance independence, same-visit review, unchanged financial records/bookings across review, new-plan trace and separate exactly-once completion/departure. No shared test records.
- Frontend: **62 focused tests pass**, both TypeScript projects pass, warning-free production build. Full suite: **302 passed / 25 pre-existing failures**, documented with visual evidence in sibling frontend DOCTOR_WORKSPACE.md.

## Migration and runtime boundary

New complete migration/designer/snapshot: **20261031164400_DoctorClinicalWorkspace**. See [review](Database/ReviewedMigrations/20261031164400_DoctorClinicalWorkspace.REVIEW.md) and [SQL](Database/ReviewedMigrations/20261031164400_DoctorClinicalWorkspace.sql).

SELECT-only current-source recheck: server `db56823.public.databaseasp.net`, database `db56823`, identity S13; latest applied `20261031122754_AppointmentScheduleAudit`; sole pending new Doctor migration; model/snapshot match; new appointment treatment columns absent. Logs `.tools/doctor-target-before.log` and `.tools/doctor-target-after.log`. No credentials logged.

**Not applied.** Prior approvals are for other named migrations. Explicit approval for this migration and a fresh target/history/SQL review are needed before shared execution. Matching schema/backend/frontend activation is still required; leave the shared user-owned API running its existing compatible build. No shared database mutations, push or deployment were performed. SQL Server migration/runtime locking verification requires an authorized isolated target; no local SQL Server tools/instance were available. Down drops new clinical data structures and must not be treated as safe after use.
