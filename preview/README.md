# Live preview server

A self-contained, interactive preview of the accounting system. It is **not**
the ASP.NET application; it is a Python facsimile that executes the same
double-entry rules, so the screens can be driven end to end without IIS or SQL
Server Express.

## What it does

- **Double-entry rules** (`validate_lines`): at least two lines, one non-zero
  side per line, no negatives, only postable accounts, and debits must equal
  credits before anything is saved.
- **Lifecycle**: create a draft, approve it, post it, and void a posted entry
  (which generates a reversing entry, as the real system does).
- **Reports computed from the live ledger**: trial balance, income statement,
  balance sheet and general ledger detail, with a date range.
- **Audit trail**: an append-only log (`sec.AuditLog` in the real schema).
  Every create, approve, post and void writes a row with the before/after
  image, the user and the timestamp. The trail cannot be updated or deleted;
  the page has a "Try to modify a row" button that shows the refusal.
- **RBAC**: the same role/permission matrix as
  `Services/Security/AuthorizationService.cs`. Switch role from the navbar to see
  a permission denied (HTTP 403) page, or an action refused.

Role matrix (mirrors the application):

| Role | View | Create draft | Approve | Post | Void | Audit trail |
|---|---|---|---|---|---|---|
| Administrator | yes | yes | yes | yes | yes | yes |
| Accountant | yes | yes | no | yes | no | yes |
| Approver | yes | no | yes | no | no | no |
| Clerk | yes | yes | no | no | no | no |
| Auditor | yes | no | no | no | no | yes |

## Run it

```bash
python3 server.py            # serves on http://0.0.0.0:12000
```

Standard library only, no dependencies. State is in memory and resets when the
process restarts.

## Layout

```
server.py          the live server (rules, reports, RBAC, HTTP)
content/site.css   the application stylesheet
scripts/app.js     client behaviour (dynamic grid, DataTables, AJAX actions)
vendor/            Bootstrap, jQuery, DataTables, Chart.js (vendored)
```
