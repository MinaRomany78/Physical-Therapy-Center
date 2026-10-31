# Booking workflow

## Current reception acceptance fixes - 2026-10-31

See [RECEPTION_ACCEPTANCE_FIXES.md](RECEPTION_ACCEPTANCE_FIXES.md). This supersedes older exclusive-room and indefinite-future occupancy notes: planned windows use saved room capacity and half-open Doctor interval limits, actual Start additionally rechecks live overdue treatments/room places, and the same Doctor still requires different rooms. Booking respects saved allocation/approval capacity and Senior FrequencyPerWeek in a Saturday-Friday Cairo clinic week. No historical prescriptions or financial agreements were backfilled. CEO/Admin/Owner now own insurer accounts/claims/settlements; Reception keeps patient-share collections. Configured schema/model match with no pending migrations and no new migration required; restarted local API reads/permissions are verified.

## Authorized Doctor migration applied - 2026-10-31

Only `20261031164400_DoctorClinicalWorkspace` was applied to freshly confirmed `db56823.public.databaseasp.net / db56823` after exact forward-SQL review. History and clinical schema are verified with zero pending migrations; existing record counts and financial totals are unchanged. Current-source clinical/profile/dashboard/invoice service reads pass against SQL Server. See [application report](Database/ReviewedMigrations/20261031164400_DoctorClinicalWorkspace.APPLIED.md). This supersedes the pending-migration notes below. No shared test records, application restart, push or deployment.

## Doctor lifecycle and capacity update - 2026-10-31

See [DOCTOR_WORKSPACE.md](DOCTOR_WORKSPACE.md) for the current clinical lifecycle and concurrency rules. Ordinary Doctor appointments permit up to two overlapping treatments in different rooms, using a whole-interval sweep shared by preview/bulk commit/reschedule/start and transaction serialization. Senior assessments stay exclusive. Arrival alone does not start treatment; Doctor End finalizes notes/progress once; Reception departure releases the room without completing again. Working-hours policy and ordinary patient payment gates remain intact. Internal Senior reassessment has no appointment or financial component. New migration 20261031164400_DoctorClinicalWorkspace is reviewed and **not applied**; older schema-status notes below are historical.

## Current finance status — 2026-10-31

See [COURSE_FINANCE.md](COURSE_FINANCE.md) for the completed course-finance model, check-in gate, insurer ledger, safe legacy reuse and final verification. It supersedes the original batch-package financial policy and missing-insurer-UI notes retained below. **52 backend tests and 87 focused frontend tests pass**; the Angular wizard/collection/insurance flow also passed against the matching rebuilt local SQLite-backed ASP.NET test host. Working-hours policy is unchanged.

Read-only SQL Server recheck: `db56823.public.databaseasp.net / db56823` (`S13`), latest applied `20261029124607_BookingWorkflow`; sole pending `20261030184822_CourseFinancialAgreements`. The new migration is reviewed but **unapplied**. SQL Server runtime migration/populated finance/concurrency checks remain separate from SQLite/browser evidence and require explicit authorization for the new migration. The main API was not restarted; no push/deploy occurred.

The older policy, prerequisite and test-count statements below are retained as implementation history. In particular, the earlier applied-migration note's “none pending” describes the state before the new course-finance migration was introduced.

## Room availability and checkout

`POST /api/Rooms/availability` is an authenticated staff read that accepts 1–200 `startsAt` values and returns active rooms free for every selected 60-minute interval. Overlapping Pending/InProgress reservations exclude the room. An InProgress appointment without ActualCheckOut continues to occupy its room even after its planned end; checkout releases that occupancy. Completed/cancelled/no-show appointments do not hold a reservation, and checkout never reactivates a maintenance room.

Booking preview and commit enforce these rules independently of the dropdown, including overdue occupancy and immediate release of completed reservations. Room list/detail responses retain the existing StatusRoom contract and now derive ongoing occupancy from checkout state. The frontend refreshes rooms and availability only after confirmed appointment mutations. No database migration is required. All 33 backend relational/HTTP tests and 58 focused frontend tests pass.

## Staff working-hours setup

The CEO Staff Management Add User and Edit User forms own working days/time ranges. `POST /api/Users/Add` and `POST /api/Users/Update?userId=...` accept `workingHours`, a list of `dayOfWeek` (Sunday 0 through Saturday 6), `startTime` and `endTime` windows in Cairo time. User responses and the staff directory return the saved hours. Existing Users authorization (Admin, Owner, Ceo) controls changes.

User details, roles and working hours save in one database transaction. Invalid days, reversed windows and overlapping windows are rejected before changing the user. Editing supports changing days/time ranges and removing windows; identical saves retain existing rows, removed windows are soft-deleted. Omitting `workingHours` preserves existing hours for older callers; explicitly sending `[]` clears the active schedule. Patient-only accounts cannot have staff working hours. No hours are inferred or seeded, and no schema migration is required.

Booking has no working-hours editor, read request, or save action, and its former working-hours routes have been removed. Booking previews distinguish `WorkingHoursNotConfigured` from a time outside an existing schedule and direct staff to CEO Staff Management for setup. The full 60-minute appointment must still fit inside a saved window; conflict and absence checks remain in effect. Restart the local API after updating to load the new Users contract. Verification: 30 backend tests and 27 focused staff/booking frontend tests pass; the production frontend build is warning-free.

**Status update (2026-10-30):** Following the user's confirmation of Mina's approval and a backup, `20261029124607_BookingWorkflow` was applied to the confirmed `db56823.public.databaseasp.net / db56823` target. No migrations remain pending; the invoice and today-sessions service reads succeed. All 25 backend and 58 focused frontend booking checks pass. The SQL Server idempotent backfill compilation issue was corrected before successful application. See [MIGRATION_VERIFICATION.md](MIGRATION_VERIFICATION.md) for the review and execution evidence. Earlier migration-gate notes below are historical.

Implemented together with the sibling `physical-therapy-project-Admin-Panel` repository. No production database, push, or deployment is part of verification.

## Behavior

- Booking starts with a paginated patient directory containing only ID, name, phone and gender. Patient-specific clinical and insurance information is fetched after selection. Eligible clinicians and room candidates are then loaded; time availability is checked by the preview and revalidated when saving.
- Assessment completion is explicit (`Assessment.CompletedAt`). The existing `Assessment.Status` enum is severity (Low/Medium/High), not completion. An empty cache, a severity value, or the existence of a treatment plan never establishes clinical completion.
- Before a completed assessment, only one initial assessment appointment is allowed, with a same-gender Senior. Cash/self-paying patients explicitly choose Free or Paid; Paid requires an agreed price. Insurance assessment is waived under `InsuranceAssessmentTemporaryPolicy`, including while approval is pending. Both liabilities are zero, the invoice is Waived, and no payment is created.
- After completion, treatment requires the patient's saved active plan. Its assigned Doctor is preferred; eligible alternatives must also be Doctors of the patient's gender. Single and bulk bookings use the same server validation and transaction.
- Insured treatment uses persisted insurance approval, a company, a known copay percentage and remaining approved-session capacity. Unknown funding, prices, percentages and approval limits are not replaced with defaults. Existing insurer-funded appointments count toward approval use; completed appointments continue to consume approval.
- Planned appointments are 60 minutes. Requests contain starts, not a manually chosen end. Actual attendance is recorded only by explicit staff check-in/check-out. An overdue appointment remains occupied until checkout; there is no one-hour automatic checkout.
- Bulk previews include every generated interval and patient, clinician, room, working-hours and absence conflicts. Any invalid interval rejects the whole batch. Cairo working hours are compared against UTC appointment instants. Database timestamps are serialized with their UTC designator.
- Planned, booked and completed counts are separate. Missing unbooked session records for a saved legacy plan are materialized by the backend inside the booking transaction; the frontend never creates synthetic session IDs.

## Financial agreement and persistence

`BookingBatch` stores the durable retry identity, request hash, selected-session pricing agreement, discount, payer liabilities, waiver reason and installment schedule. A package price covers the appointments selected in that batch; it does not silently cover every session of the clinical plan.

Each booked appointment has one invoice. Actual positive deposits create payment records allocated against patient liabilities. Zero deposit creates no payment. Installment arrangements are promises, not receipts. Later collections allocate actual payments against the account's existing invoices; they do not create another set of charges.

`PaidAmount` represents actual patient receipts. Insurer liability is separate from money received. Session payment status becomes Paid only when actual payments cover the whole invoice, including any insurer liability. Package selection, appointment creation and a paid copay alone do not mark all treatment sessions paid.

Booking, invoices, deposits and session allocation are saved in one serializable transaction. Collections use their own serializable transaction and retry key. Unique operation/payment keys and request comparison protect retries; a changed request cannot reuse a committed identity. Rescheduling updates the existing appointment/session, retaining invoice and payment identities and balances.

Timeouts, unreadable success responses and commit/response uncertainty are not reported as success. Clients retain opaque operation identities and query committed state before retrying. A retry uses the same identity. Staff can reconstruct an interrupted request after reload without generating a new identity for an unresolved operation.

Generic legacy session writes cannot set payment coverage, attendance, or booked dates outside the workflow. Invoice flags cannot erase a booking agreement or claim payment without coverage. Cancellation preserves the existing financial obligation; no cancellation fee, refund or retrospective pricing policy has been invented.

## Contracts

All booking endpoints require authenticated staff. Clinical completion requires the assigned Senior or an authorized supervisor. Existing JWT authentication, application roles and frontend interceptors/guards are retained.

| Method/path under `/api/Bookings` | Purpose |
| --- | --- |
| `GET patients` | Minimal paginated selection directory |
| `GET patients/{id}` | Assessments, completion, plans, funding and approval capacity |
| `GET patients/{id}/clinicians?planId=...` | Same-gender eligible Seniors or Doctors; prefer assigned Doctor |
| `GET patients/{id}/clinicians?treatment=true` | Doctor candidates for the Senior's clinical plan form |
| `GET clinicians/workload` | Actual ongoing Doctor workload |
| `POST preview` | Full schedule, conflict and financial preview; no writes |
| `POST /` | Atomic, idempotent booking and financial persistence |
| `GET operations/{key}` | Reconcile a committed booking |
| `PUT appointments/{id}/schedule` | Reschedule while preserving financial identities |
| `PUT appointments/{id}/status` | Explicit check-in, checkout, cancellation or no-show transition |
| `POST assessments/complete` | Persist completed assessment and treatment plan together |
| `POST assessments/{id}/complete` | Authorized sign-off of an existing saved clinical assessment |
| `GET accounts` | Agreements, installment arrangements and actual balances |
| `POST accounts/{key}/payments` | Idempotent patient collection against existing invoices |
| `GET accounts/{key}/payments/{operationKey}` | Reconcile a collection |

The existing appointment/receptionist mutation routes delegate to the same workflow, rather than providing an eligibility or financial-validation bypass. Legacy invoice payments now require a stable `IdempotencyKey`, explicit method and positive currency amount. Their history exposes the operation identity for reconciliation.

Treatment plan reads support a `patientId` filter. Session reads require their documented `treatmentPlanId`; clients do not request a nonexistent global session list.

## Migration

`20261029124607_BookingWorkflow` adds BookingBatches, assessment completion/appointment association, actual attendance timestamps, booking allocation/funding fields, payment retry keys, conflict indexes and uniqueness constraints. Its designer and model snapshot are included.

- Old assessment completion and actual attendance are not guessed or backfilled from severity or planned times. An assigned Senior can explicitly sign off a genuine saved legacy assessment before its plan becomes eligible.
- Existing insurer liability is evidence for the funding-flag backfill. Approval capacity also conservatively includes legacy treatment appointments for an insurance patient, avoiding accidental extra allocation.
- A preflight rejects Senior IDs longer than the Identity identifier limit before the indexed column is narrowed; it does not truncate them silently.
- The design-time factory uses an explicitly local, design-only SQL Server connection string and never loads production credentials.

Migration execution against an approved SQL Server test database is still required. Automated checks compare the current SQL Server model to the snapshot and generate the idempotent SQL script without opening a database connection. No migration was applied to production or to the user's running local backend database.

## Local verification

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test PTCenter.Tests/PTCenter.Tests.csproj --artifacts-path .tools/verification
```

The separate artifacts path avoids locking or replacing DLLs used by a running local API. Database tests use isolated SQLite databases: relational constraints and transactions are real, and authenticated HTTP tests use the real JWT bearer handler with a test-only signing key. Temporary HTTP-test databases are removed after disposal. Neither configured application connection strings nor production endpoints are used.

25 backend tests pass, including concurrent HTTP retry, no partial writes on conflicts, approval/session limits, waived assessments, package/discount/deposit/installments, payment replay, insurer/patient separation, rescheduling, actual occupancy, clinical sign-off, authentication, unknown funding, legacy completed-session capacity and migration consistency.

Remaining verification: SQL Server migration execution and provider-specific concurrent-load testing, plus an interactive browser walkthrough against a separately migrated test API. Existing dependency advisory warnings for `System.Security.Cryptography.Pkcs` were not changed by this workflow.

## Runtime verification gate (2026-10-29)

Subsequent read-only inspection is recorded in [DATABASE_TARGET_AUDIT.md](DATABASE_TARGET_AUDIT.md). User/machine/process environment checks and current-user secrets inspection found no alternate connection; SQL Server/LocalDB service, registry and installation checks found no instance. At approximately 23:00 Cairo time the API was no longer listening on 7223/5013; no stop/start was performed by the agent. The configured remote target still lacks evidence of test isolation. The user's latest instruction explicitly prohibits database creation and migrations pending Mina's confirmation.

The repository and Debug output `appsettings.json` both select server `db56823.public.databaseasp.net`, database `db56823`. Development settings contain no connection override, the API project has no UserSecretsId, and the inspection shell has no connection override. This remote database has **not** been confirmed as isolated test data. The running API on port 7223 is inaccessible for process startup/environment inspection, so its effective process-level overrides remain unknown. No connection was opened to this database and no migration was applied. No local SQL Server service or LocalDB installation was found.

The Release API build passes and the EF CLI successfully generated `.tools/booking-runtime-idempotent.sql` without opening a database connection. This validates tooling and SQL generation only; it is not SQL Server execution evidence. The existing Debug backend and Angular processes were left running.

After the operator confirms a separate test server/database and securely configures `ConnectionStrings__DefaultConnection` to that target, the exact migration command from this repository root is:

```powershell
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__DefaultConnection)) {
    throw 'Configure and confirm the isolated test SQL Server connection first.'
}
& '.tools/dotnet-ef.exe' database update 20261029124607_BookingWorkflow `
    --project PTCenter.Infrastructure/PTCenter.Infrastructure.csproj `
    --startup-project PTCenter.Api/PTCenter.Api.csproj `
    --context ApplicationDbContext --configuration Release --no-build `
    --connection "$env:ConnectionStrings__DefaultConnection"
if ($LASTEXITCODE -ne 0) { throw 'Migration failed; do not start runtime verification.' }
```

Do not omit `--connection`: the design-time factory deliberately targets a separate local design-only database, not the API's runtime target. Do not populate the variable from the current unconfirmed remote connection. The command above has not been executed. If source changes after the prepared build, rebuild Release before using `--no-build`.

After successful migration and coordinated stopping of the existing backend, start Release with the **same explicit confirmed test connection** inherited from that shell, `--no-launch-profile`, and the existing local URLs. Verify the connected SQL Server/database and migration history before authenticating synthetic test users and opening the Angular booking workflow. Keep the default repository connection unchanged. Runtime/browser writes remain blocked until the isolated target is confirmed.

### Financial policy boundaries

- Package scope is the selected booking batch. It does not buy or settle unselected future plan sessions. This is the implemented scope, not evidence of a separately approved clinic-wide package policy.
- Cancelling a pending appointment changes attendance status only and preserves its full saved invoice/liability, deposits and installment agreement. It creates no automatic credit, refund or replacement booking. Existing allocation remains linked to its original session. There is no implemented credit/refund adjustment flow; the retained balance must not be described as a newly approved cancellation-fee policy.
- Insurer receipts are actual positive `POST /api/Billing/payments` records with `method: "Insurance"` (numeric value 4), an invoice ID and a stable idempotency key. The backend limits them to the invoice's remaining insurer liability. Other methods reduce patient liability. `PaidAmount` and `RemainingAmount` in invoice/account responses describe the patient side; insurer receipts remain identifiable in the invoice payment history and do not inflate patient-paid totals.
- Invoice and linked session payment status become Paid only when actual patient plus insurer receipts cover the invoice total. Partial insurer receipt or fully paid copay alone leaves them Pending. Waived zero-liability assessment invoices remain Waived without a payment record.
- Current Angular Billing collection controls are patient-only and exclude Insurance. There is no insurer-receipt entry control yet, no separate insurer settlement/balance summary, and the receipt request does not capture a remittance reference or distinguish the insurer's bank transfer rail. The backend receipt endpoint is implemented; an end-to-end insurer collection UI must not be claimed complete.
