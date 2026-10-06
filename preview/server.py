#!/usr/bin/env python3
"""
Live preview server for the accounting system.

This is NOT the ASP.NET application. It is a self-contained Python facsimile
that executes the *same* double-entry rules the real system enforces, so the
screens can be driven interactively:

  * create a draft (>=2 lines, one non-zero side per line, debits == credits,
    only postable accounts)
  * approve a draft, post an approved/draft entry
  * void a posted entry and generate its reversing entry
  * trial balance / income statement / balance sheet / GL detail computed
    from the live ledger

State is in memory and resets when the process restarts. Standard library only.
"""
import json
import re
from datetime import date, datetime, timedelta
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

PORT = 12000
TODAY = date(2026, 10, 5)


# ---------------------------------------------------------------------------
# Chart of accounts (mirrors database/05_SeedChartOfAccounts.sql)
# ---------------------------------------------------------------------------
# (code, name, type, normal)  type: 1 Asset 2 Liability 3 Equity 4 Revenue 5 Expense
# normal: 1 Debit 2 Credit
_COA = [
    ("1000", "ASSETS", 1, 1, False),
    ("1010", "Cash on Hand and at Bank", 1, 1, True),
    ("1020", "Petty Cash", 1, 1, True),
    ("1100", "Accounts Receivable", 1, 1, True),
    ("1200", "Inventory", 1, 1, True),
    ("1300", "Prepaid Expenses", 1, 1, True),
    ("1500", "Property, Plant & Equipment", 1, 1, True),
    ("1510", "Accumulated Depreciation", 1, 2, True),
    ("2000", "LIABILITIES", 2, 2, False),
    ("2100", "Accounts Payable", 2, 2, True),
    ("2200", "Sales Tax Payable", 2, 2, True),
    ("2300", "Accrued Liabilities", 2, 2, True),
    ("2400", "Payroll Liabilities", 2, 2, True),
    ("2500", "Long-Term Loans", 2, 2, True),
    ("3000", "EQUITY", 3, 2, False),
    ("3100", "Owner's Capital", 3, 2, True),
    ("3200", "Retained Earnings", 3, 2, True),
    ("3300", "Current Year Earnings", 3, 2, True),
    ("4000", "REVENUE", 4, 2, False),
    ("4100", "Sales Revenue", 4, 2, True),
    ("4200", "Service Revenue", 4, 2, True),
    ("4900", "Other Income", 4, 2, True),
    ("5000", "EXPENSES", 5, 1, False),
    ("5100", "Cost of Goods Sold", 5, 1, True),
    ("5200", "Salaries & Wages", 5, 1, True),
    ("5300", "Rent Expense", 5, 1, True),
    ("5400", "Utilities Expense", 5, 1, True),
    ("5500", "Office Supplies", 5, 1, True),
    ("5600", "Professional Fees", 5, 1, True),
    ("5900", "Depreciation Expense", 5, 1, True),
]

TYPE_NAME = {1: "Asset", 2: "Liability", 3: "Equity", 4: "Revenue", 5: "Expense"}
STATUS_NAME = {0: "Draft", 1: "Approved", 2: "Posted", 3: "Void"}

ACCOUNTS = []
_BY_CODE = {}
_BY_ID = {}
for i, (code, name, typ, normal, postable) in enumerate(_COA, start=1):
    acc = {"id": i, "code": code, "name": name, "type": typ,
           "normal": normal, "postable": postable}
    ACCOUNTS.append(acc)
    _BY_CODE[code] = acc
    _BY_ID[i] = acc


def acct(code):
    return _BY_CODE[code]["id"]


# ---------------------------------------------------------------------------
# RBAC - mirrors Services/Security/AuthorizationService.cs, Roles and Permission
# ---------------------------------------------------------------------------
ROLES = ["Administrator", "Accountant", "Approver", "Clerk", "Auditor"]

PERMISSIONS = ["ViewLedger", "CreateDraftJournal", "ApproveJournal", "PostJournal",
               "VoidJournal", "ManageMasterData", "ManageUsers", "ClosePeriod",
               "ViewAuditTrail"]

ROLE_MAP = {
    "Administrator": set(PERMISSIONS),
    "Accountant": {"ViewLedger", "CreateDraftJournal", "PostJournal",
                   "ManageMasterData", "ClosePeriod", "ViewAuditTrail"},
    # An approver can approve but cannot post: separation of duties.
    "Approver": {"ViewLedger", "ApproveJournal"},
    "Clerk": {"ViewLedger", "CreateDraftJournal"},
    "Auditor": {"ViewLedger", "ViewAuditTrail"},
}

DEFAULT_ROLE = "Administrator"


def has_permission(role, permission):
    return permission in ROLE_MAP.get(role, set())


# ---------------------------------------------------------------------------
# In-memory ledger
# ---------------------------------------------------------------------------
JOURNALS = []
_SEQ = {"JV": 0, "REV": 0}
_next_id = 1

# Append-only audit trail (mirrors sec.AuditLog). Rows are never updated or
# deleted; tamper attempts are refused the same way trg_AuditLog_Immutable does.
AUDIT = []
_next_audit_id = 1
AUDIT_TAMPER_MESSAGE = "The audit log is append-only and cannot be modified or deleted."


def _snapshot(j):
    """JSON scalar snapshot of a journal header, as the audit triggers store."""
    return json.dumps({
        "JournalId": j["id"], "VoucherNumber": j["voucher"],
        "TransactionDate": j["date"].isoformat(), "Status": STATUS_NAME[j["status"]],
        "TotalDebit": sum(l["debit"] for l in j["lines"]),
        "TotalCredit": sum(l["credit"] for l in j["lines"]),
    })


def _audit(schema, table, record_id, action, old, new, user):
    global _next_audit_id
    row = {
        "id": _next_audit_id,
        "schema": schema,
        "table": table,
        "recordId": str(record_id),
        "action": action,
        "oldValues": old,
        "newValues": new,
        "userId": user,
        "userName": user,
        "ipAddress": "127.0.0.1",
        "occurredAt": datetime.now(),
    }
    _next_audit_id += 1
    AUDIT.append(row)
    return row


def audit_tamper(audit_id):
    """Attempt to modify/delete an audit row; always refused (append-only)."""
    raise DomainError(AUDIT_TAMPER_MESSAGE)


def _add_journal(voucher, txn_date, reference, description, status, lines,
                 created_by="system", posted_by=None, void_reason=None):
    global _next_id
    j = {
        "id": _next_id,
        "voucher": voucher,
        "date": txn_date,
        "reference": reference or "",
        "description": description or "",
        "status": status,
        "createdBy": created_by,
        "createdAt": datetime(2026, 1, 1, 9, 0, 0),
        "postedBy": posted_by,
        "postedAt": datetime(2026, 1, 1, 9, 30, 0) if posted_by else None,
        "voidedBy": None,
        "voidedAt": None,
        "voidReason": void_reason,
        "lines": lines,
    }
    _next_id += 1
    JOURNALS.append(j)
    return j


def next_number(prefix):
    _SEQ[prefix] = _SEQ.get(prefix, 0) + 1
    return "%s-2026-%06d" % (prefix, _SEQ[prefix])


def seed():
    _SEQ["JV"] = 0
    # Opening balance (posted)
    _add_journal("JV-2026-000001", date(2026, 1, 1), "OPENING",
                 "Owner capital contribution", 2,
                 [{"accountId": acct("1010"), "debit": 100000.0, "credit": 0.0, "description": "Cash contribution"},
                  {"accountId": acct("3100"), "debit": 0.0, "credit": 100000.0, "description": "Owner capital"}],
                 created_by="a.clerk", posted_by="j.smith")
    _SEQ["JV"] = 1

    # February rent (posted)
    _add_journal("JV-2026-000002", date(2026, 2, 15), "RENT-FEB",
                 "February office rent", 2,
                 [{"accountId": acct("5300"), "debit": 1200.0, "credit": 0.0, "description": "Rent for February"},
                  {"accountId": acct("1010"), "debit": 0.0, "credit": 1200.0, "description": "Rent for February"}],
                 created_by="a.clerk", posted_by="j.smith")
    _SEQ["JV"] = 2

    # Credit sale on account (posted)
    _add_journal("JV-2026-000003", date(2026, 3, 1), "INV-1042",
                 "Invoice 1042 - Acme Trading", 2,
                 [{"accountId": acct("1100"), "debit": 5400.0, "credit": 0.0, "description": "Invoice 1042"},
                  {"accountId": acct("4100"), "debit": 0.0, "credit": 5400.0, "description": "Sales revenue"}],
                 created_by="a.clerk", posted_by="j.smith")
    _SEQ["JV"] = 3

    # A draft the user can approve/post live
    _add_journal("JV-2026-000004", date(2026, 3, 4), "UTIL-MAR",
                 "March utilities accrual", 0,
                 [{"accountId": acct("5400"), "debit": 860.0, "credit": 0.0, "description": "Electricity"},
                  {"accountId": acct("2300"), "debit": 0.0, "credit": 860.0, "description": "Accrued utilities"}],
                 created_by="a.clerk")
    _SEQ["JV"] = 4

    # An approved entry ready to post live
    _add_journal("JV-2026-000005", date(2026, 3, 6), "SUPPLIES",
                 "Office supplies purchase", 1,
                 [{"accountId": acct("5500"), "debit": 320.0, "credit": 0.0, "description": "Stationery"},
                  {"accountId": acct("2100"), "debit": 0.0, "credit": 320.0, "description": "Payable to supplier"}],
                 created_by="a.clerk")
    _SEQ["JV"] = 5

    # The database triggers would have captured every seeded row as an INSERT.
    for j in JOURNALS:
        _audit("gl", "JournalHeader", j["id"], "INSERT", None, _snapshot(j), j["createdBy"])


seed()


# ---------------------------------------------------------------------------
# Domain rules (the part that is genuinely "live")
# ---------------------------------------------------------------------------
class DomainError(Exception):
    pass


def money(v):
    return round(float(v or 0), 4)


def validate_lines(lines):
    if len(lines) < 2:
        raise DomainError("A journal entry requires at least two lines.")
    for ln in lines:
        d, c = money(ln.get("debit")), money(ln.get("credit"))
        if d < 0 or c < 0:
            raise DomainError("Amounts cannot be negative.")
        if d > 0 and c > 0:
            raise DomainError("A line cannot have both a debit and a credit.")
        if d == 0 and c == 0:
            raise DomainError("Every line must have either a debit or a credit.")
        account = _BY_ID.get(int(ln.get("accountId") or 0))
        if account is None or not account["postable"]:
            raise DomainError("One or more lines reference an invalid or non-postable account.")
    td = sum(money(l["debit"]) for l in lines)
    tc = sum(money(l["credit"]) for l in lines)
    if td != tc:
        raise DomainError("The entry is not balanced. Debits %.4f vs credits %.4f "
                          "(difference %.4f)." % (td, tc, abs(td - tc)))
    if td == 0:
        raise DomainError("A journal entry must have a non-zero amount.")
    return td, tc


def create_draft(payload, user):
    lines = payload.get("lines") or []
    validate_lines(lines)
    voucher = (payload.get("voucherNumber") or "").strip() or next_number("JV")
    txn = payload.get("transactionDate") or TODAY.isoformat()
    j = _add_journal(voucher, date.fromisoformat(txn), payload.get("reference"),
                     payload.get("description"), 0,
                     [{"accountId": int(l["accountId"]), "debit": money(l["debit"]),
                       "credit": money(l["credit"]), "description": l.get("description", "")}
                      for l in lines],
                     created_by=user)
    # Trigger equivalent: gl.trg_JournalHeader_Audit on INSERT.
    _audit("gl", "JournalHeader", j["id"], "INSERT", None, _snapshot(j), user)
    return j


def approve(jid, user):
    j = _get(jid)
    if j["status"] != 0:
        raise DomainError("Only a draft entry can be approved. This entry is %s."
                          % STATUS_NAME[j["status"]])
    old = _snapshot(j)
    j["status"] = 1
    _audit("gl", "JournalHeader", j["id"], "UPDATE", old, _snapshot(j), user)
    return j


def post(jid, user):
    j = _get(jid)
    if j["status"] in (2, 3):
        raise DomainError("Entry %s has already been posted or voided." % j["voucher"])
    validate_lines(j["lines"])
    old = _snapshot(j)
    j["status"] = 2
    j["postedBy"] = user
    j["postedAt"] = datetime.now()
    _audit("gl", "JournalHeader", j["id"], "UPDATE", old, _snapshot(j), user)
    return j


def void(jid, user, reason):
    j = _get(jid)
    if j["status"] != 2:
        raise DomainError("Only a posted entry can be voided. This entry is %s."
                          % STATUS_NAME[j["status"]])
    if not reason or not reason.strip():
        raise DomainError("A reason is required to void an entry.")
    old = _snapshot(j)
    j["status"] = 3
    j["voidedBy"] = user
    j["voidedAt"] = datetime.now()
    j["voidReason"] = reason
    _audit("gl", "JournalHeader", j["id"], "UPDATE", old, _snapshot(j), user)
    # Generate the reversing entry: debit/credit swapped, posted immediately.
    rev = _add_journal(next_number("REV"), j["date"], j["voucher"],
                       "Reversal of %s: %s" % (j["voucher"], reason), 2,
                       [{"accountId": l["accountId"], "debit": l["credit"],
                         "credit": l["debit"], "description": "Reversal: " + l["description"]}
                        for l in j["lines"]],
                       created_by=user, posted_by=user)
    _audit("gl", "JournalHeader", rev["id"], "INSERT", None, _snapshot(rev), user)
    return j, rev


def _get(jid):
    for j in JOURNALS:
        if j["id"] == int(jid):
            return j
    raise DomainError("Journal entry %s was not found." % jid)


# ---------------------------------------------------------------------------
# Reports
# ---------------------------------------------------------------------------
def _posted():
    # A void is implemented as a reversal, not a removal: the original entry
    # was posted to the ledger, so it must keep counting until its reversing
    # entry cancels it. Excluding voided entries (and including their reversals)
    # would double-count the reversal.
    return [j for j in JOURNALS if j["status"] in (2, 3)]


def trial_balance(from_date, to_date):
    rows = []
    for a in ACCOUNTS:
        if not a["postable"]:
            continue
        td = tc = 0.0
        for j in _posted():
            if from_date <= j["date"] <= to_date:
                for l in j["lines"]:
                    if l["accountId"] == a["id"]:
                        td += money(l["debit"])
                        tc += money(l["credit"])
        if td == 0 and tc == 0:
            continue
        rows.append({"code": a["code"], "name": a["name"],
                     "type": TYPE_NAME[a["type"]],
                     "debit": td, "credit": tc, "net": td - tc})
    total_debit = sum(r["debit"] for r in rows)
    total_credit = sum(r["credit"] for r in rows)
    return {"rows": rows, "totalDebit": total_debit, "totalCredit": total_credit,
            "balanced": abs(total_debit - total_credit) < 0.00005}


def income_statement(from_date, to_date):
    lines = []
    total_rev = total_exp = 0.0
    for a in ACCOUNTS:
        if not a["postable"] or a["type"] not in (4, 5):
            continue
        amount = 0.0
        for j in _posted():
            if from_date <= j["date"] <= to_date:
                for l in j["lines"]:
                    if l["accountId"] == a["id"]:
                        # Revenue is credit-positive, expense debit-positive.
                        amount += (money(l["credit"]) - money(l["debit"])) if a["type"] == 4 \
                            else (money(l["debit"]) - money(l["credit"]))
        if amount == 0:
            continue
        lines.append({"code": a["code"], "name": a["name"],
                      "section": "Revenue" if a["type"] == 4 else "Expense",
                      "amount": amount})
        if a["type"] == 4:
            total_rev += amount
        else:
            total_exp += amount
    return {"lines": lines, "totalRevenue": total_rev, "totalExpense": total_exp,
            "netIncome": total_rev - total_exp}


def balance_sheet(as_of):
    lines = []
    total_a = total_l = total_e = 0.0
    for a in ACCOUNTS:
        if not a["postable"] or a["type"] not in (1, 2, 3):
            continue
        amount = 0.0
        for j in _posted():
            if j["date"] <= as_of:
                for l in j["lines"]:
                    if l["accountId"] == a["id"]:
                        if a["type"] == 1:
                            amount += money(l["debit"]) - money(l["credit"])
                        else:
                            amount += money(l["credit"]) - money(l["debit"])
        if amount == 0:
            continue
        lines.append({"code": a["code"], "name": a["name"],
                      "section": TYPE_NAME[a["type"]], "amount": amount})
        if a["type"] == 1:
            total_a += amount
        elif a["type"] == 2:
            total_l += amount
        else:
            total_e += amount

    # Current period earnings roll into equity so the equation balances.
    inc = income_statement(date(2000, 1, 1), as_of)
    earnings = inc["netIncome"]
    return {"lines": lines, "totalAssets": total_a, "totalLiabilities": total_l,
            "totalEquity": total_e, "earnings": earnings,
            "balanced": abs(total_a - (total_l + total_e + earnings)) < 0.00005}


def gl_detail(account_id, from_date, to_date):
    opening = 0.0
    movements = []
    for j in _posted():
        for l in sorted(j["lines"], key=lambda x: x["accountId"]):
            if account_id and l["accountId"] != account_id:
                continue
            signed = money(l["debit"]) - money(l["credit"])
            if j["date"] < from_date:
                opening += signed
            elif j["date"] <= to_date:
                movements.append({
                    "code": _BY_ID[l["accountId"]]["code"],
                    "date": j["date"].isoformat(),
                    "voucher": j["voucher"],
                    "reference": j["reference"],
                    "description": l["description"],
                    "debit": money(l["debit"]),
                    "credit": money(l["credit"]),
                    "running": 0.0,
                })
    movements.sort(key=lambda m: (m["date"], m["voucher"]))
    running = opening
    for m in movements:
        running += m["debit"] - m["credit"]
        m["running"] = running
    return {"opening": opening, "movements": movements}


# ---------------------------------------------------------------------------
# HTML rendering
# ---------------------------------------------------------------------------
def esc(s):
    return (str(s or "").replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;").replace('"', "&quot;"))


def fmt(v):
    return "{:,.4f}".format(money(v))


HEAD = """<!DOCTYPE html>
<html lang="en"><head>
<meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
<title>{title} - Accounting System</title>
<link rel="stylesheet" href="/vendor/bootstrap.min.css" />
<link rel="stylesheet" href="/vendor/dataTables.bootstrap5.min.css" />
<link rel="stylesheet" href="/content/site.css" />
</head><body>
<header><nav class="navbar navbar-expand-lg navbar-dark bg-dark"><div class="container-fluid">
<a class="navbar-brand" href="/">Accounting System</a>
<button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target="#mainNav"><span class="navbar-toggler-icon"></span></button>
<div class="collapse navbar-collapse" id="mainNav">
<ul class="navbar-nav me-auto mb-2 mb-lg-0">
<li class="nav-item"><a class="nav-link{ja}" href="/journal/index.html">Journal Entries</a></li>
<li class="nav-item dropdown"><a class="nav-link dropdown-toggle{ra}" href="#" data-bs-toggle="dropdown">Reports</a>
<ul class="dropdown-menu">
<li><a class="dropdown-item" href="/reports/trial-balance.html">Trial Balance</a></li>
<li><a class="dropdown-item" href="/reports/income-statement.html">Income Statement</a></li>
<li><a class="dropdown-item" href="/reports/balance-sheet.html">Balance Sheet</a></li>
<li><a class="dropdown-item" href="/reports/general-ledger.html">General Ledger</a></li>
<li><a class="dropdown-item" href="/reports/audit-trail.html">Audit Trail</a></li>
</ul></li></ul>
<div class="dropdown">
<button class="btn btn-sm btn-outline-light dropdown-toggle" type="button" data-bs-toggle="dropdown">
{user} &middot; {role}</button>
<ul class="dropdown-menu dropdown-menu-end">
<li><h6 class="dropdown-header">Switch role (RBAC demo)</h6></li>
{roleitems}
</ul></div>
</div></div></nav></header>
<main class="container-fluid py-4">
<div class="alert alert-info alert-dismissible fade show" role="alert">
<strong>Live preview.</strong> A Python facsimile that executes the real double-entry rules in memory.
Create, approve, post and void entries and watch the reports recompute. Switch the role to see RBAC enforced.
State resets on restart.
<button type="button" class="btn-close" data-bs-dismiss="alert"></button></div>
"""

FOOT = """</main>
<footer class="border-top text-muted py-3 mt-4"><div class="container-fluid small">
Accounting System &middot; All amounts are recorded to four decimal places.</div></footer>
<script src="/vendor/jquery.min.js"></script>
<script src="/vendor/bootstrap.bundle.min.js"></script>
<script src="/vendor/jquery.dataTables.min.js"></script>
<script src="/vendor/dataTables.bootstrap5.min.js"></script>
<script src="/vendor/chart.umd.min.js"></script>
<script src="/scripts/app.js"></script>
</body></html>"""


def shell(title, body, active="", role=None, extra=""):
    role = role or DEFAULT_ROLE
    roleitems = "".join(
        '<li><a class="dropdown-item%s" href="/switch-role?role=%s">%s</a></li>'
        % (" active" if r == role else "", r, r) for r in ROLES)
    html = HEAD.format(title=title,
                       ja=" active" if active == "journal" else "",
                       ra=" active" if active == "reports" else "",
                       user="CONTOSO\\j.smith", role=role, roleitems=roleitems)
    return html + body + extra + FOOT


def forbidden(role, permission):
    body = """
<h2>Access denied</h2>
<p>Your account does not have permission to perform this action.</p>
<div class="alert alert-danger">
Role <strong>%s</strong> does not grant the <strong>%s</strong> permission.
</div>
<p class="text-muted small">This mirrors <code>[RequirePermission(Permission.%s)]</code>
in the application, which returns HTTP 403 for a user whose roles do not grant the permission.</p>
<a class="btn btn-primary" href="/">Return to the dashboard</a>""" % (esc(role), esc(permission), esc(permission))
    return shell("Forbidden", body, role=role)


def render_dashboard(role=None):
    body = """
<div class="p-5 mb-4 bg-light rounded-3">
<h1 class="display-6">Accounting System</h1>
<p class="lead">Double-entry general ledger, accounts receivable and accounts payable for Example Company Ltd.</p>
<p>Base currency: <strong>USD</strong></p></div>
<div class="row g-4">
<div class="col-md-4"><div class="card h-100"><div class="card-body">
<h5 class="card-title">Journal Entries</h5>
<p class="card-text">Create, approve and post balanced journal vouchers.</p>
<a class="btn btn-primary" href="/journal/index.html">Open journal</a></div></div></div>
<div class="col-md-4"><div class="card h-100"><div class="card-body">
<h5 class="card-title">Financial Reports</h5>
<p class="card-text">Trial balance, income statement, balance sheet and ledger detail.</p>
<a class="btn btn-primary" href="/reports/index.html">Open reports</a></div></div></div>
<div class="col-md-4"><div class="card h-100"><div class="card-body">
<h5 class="card-title">Create an entry</h5>
<p class="card-text">Start a new journal voucher with dynamic debit/credit lines.</p>
<a class="btn btn-success" href="/journal/create.html">New journal entry</a></div></div></div>
</div>"""
    return shell("Home", body, role=role)


def render_journal_index(role=None):
    body = """
<div class="d-flex justify-content-between align-items-center mb-3">
<h2>Journal Entries</h2>
<a class="btn btn-success" href="/journal/create.html">New Journal Entry</a></div>
<div class="row mb-3">
<div class="col-md-3"><label class="form-label" for="statusFilter">Status</label>
<select id="statusFilter" class="form-select">
<option value="">All</option><option value="0">Draft</option>
<option value="1">Approved</option><option value="2">Posted</option>
<option value="3">Void</option></select></div>
<div class="col-md-4"><label class="form-label" for="searchFilter">Search</label>
<input id="searchFilter" class="form-control" placeholder="Voucher, reference or description" /></div>
</div>
<table id="journalTable" class="table table-striped table-hover" style="width:100%">
<thead><tr><th>Voucher</th><th>Date</th><th>Reference</th><th>Status</th>
<th class="text-end">Debit</th><th class="text-end">Credit</th><th>Posted By</th>
<th class="text-end">Actions</th></tr></thead></table>"""
    return shell("Journal Entries", body, active="journal", role=role)


def render_journal_create(role=None):
    opts = "\n".join('<option value="%d">%s - %s</option>' % (a["id"], a["code"], esc(a["name"]))
                     for a in ACCOUNTS if a["postable"])
    row = """
<tr class="journal-line">
<td><select name="accountId" class="form-select form-select-sm account-select">
<option value="">-- select account --</option>%s</select></td>
<td><input name="description" class="form-control form-control-sm" /></td>
<td><input name="debit" value="0" type="number" step="0.0001" min="0" class="form-control form-control-sm text-end debit-input" /></td>
<td><input name="credit" value="0" type="number" step="0.0001" min="0" class="form-control form-control-sm text-end credit-input" /></td>
<td class="text-center"><button type="button" class="btn btn-sm btn-outline-danger remove-line">&times;</button></td>
</tr>""" % opts
    body = """
<h2>New Journal Entry</h2>
<div id="formAlert"></div>
<form id="journalForm" autocomplete="off">
<div class="card mb-3"><div class="card-body row g-3">
<div class="col-md-3"><label class="form-label">Voucher number</label>
<input id="VoucherNumber" class="form-control" placeholder="auto-generated" />
<div class="form-text">Leave blank to allocate the next number.</div></div>
<div class="col-md-3"><label class="form-label">Transaction date</label>
<input id="TransactionDate" class="form-control" type="date" value="%s" /></div>
<div class="col-md-3"><label class="form-label">Reference</label>
<input id="Reference" class="form-control" /></div>
<div class="col-md-3"><label class="form-label">Description</label>
<input id="Description" class="form-control" /></div>
</div></div>
<div class="card mb-3">
<div class="card-header d-flex justify-content-between align-items-center">
<span>Lines</span><button type="button" id="addLine" class="btn btn-sm btn-outline-primary">Add line</button></div>
<div class="table-responsive"><table class="table table-sm mb-0" id="linesTable">
<thead><tr><th style="width:32%%">Account</th><th style="width:26%%">Description</th>
<th style="width:16%%" class="text-end">Debit</th><th style="width:16%%" class="text-end">Credit</th>
<th style="width:10%%"></th></tr></thead>
<tbody id="linesBody">%s%s</tbody>
<tfoot>
<tr class="table-light"><th colspan="2" class="text-end">Totals</th>
<th class="text-end" id="totalDebit">0.0000</th><th class="text-end" id="totalCredit">0.0000</th><th></th></tr>
<tr><td colspan="5"><span id="balanceStatus" class="badge bg-secondary">Not balanced</span></td></tr>
</tfoot></table></div></div>
<div class="d-flex gap-2">
<button type="submit" class="btn btn-primary">Save draft</button>
<a class="btn btn-outline-secondary" href="/journal/index.html">Cancel</a></div>
</form>
<template id="lineTemplate">%s</template>""" % (TODAY.isoformat(), row, row, row)
    return shell("New Journal Entry", body, active="journal", role=role)


def render_details(jid, role=None):
    try:
        j = _get(jid)
    except DomainError:
        return shell("Not found", "<div class='alert alert-danger'>Journal entry not found.</div>", role=role)
    badge = {0: "secondary", 1: "info", 2: "success", 3: "danger"}[j["status"]]
    rows = ""
    for i, l in enumerate(j["lines"], start=1):
        a = _BY_ID[l["accountId"]]
        rows += ("<tr><td>%d</td><td>%s - %s</td><td>%s</td>"
                 "<td class='text-end'>%s</td><td class='text-end'>%s</td></tr>") % (
            i, a["code"], esc(a["name"]), esc(l["description"]),
            fmt(l["debit"]) if l["debit"] else "", fmt(l["credit"]) if l["credit"] else "")
    td = sum(l["debit"] for l in j["lines"])
    tc = sum(l["credit"] for l in j["lines"])
    voidinfo = ""
    if j["voidedAt"]:
        voidinfo = ("<dt class='col-sm-3'>Voided by</dt><dd class='col-sm-9'>%s &mdash; %s</dd>"
                    % (esc(j["voidedBy"]), esc(j["voidReason"])))
    body = """
<h2>Journal Entry %s</h2>
<dl class="row">
<dt class="col-sm-3">Status</dt><dd class="col-sm-9"><span class="badge bg-%s">%s</span></dd>
<dt class="col-sm-3">Transaction date</dt><dd class="col-sm-9">%s</dd>
<dt class="col-sm-3">Reference</dt><dd class="col-sm-9">%s</dd>
<dt class="col-sm-3">Description</dt><dd class="col-sm-9">%s</dd>
<dt class="col-sm-3">Created by</dt><dd class="col-sm-9">%s</dd>
%s</dl>
<table class="table table-sm table-striped">
<thead><tr><th>#</th><th>Account</th><th>Description</th>
<th class="text-end">Debit</th><th class="text-end">Credit</th></tr></thead>
<tbody>%s</tbody>
<tfoot><tr class="table-secondary fw-bold"><td colspan="3" class="text-end">Totals</td>
<td class="text-end">%s</td><td class="text-end">%s</td></tr></tfoot></table>
<a class="btn btn-outline-secondary" href="/journal/index.html">Back to list</a>""" % (
        esc(j["voucher"]), badge, STATUS_NAME[j["status"]], j["date"].isoformat(),
        esc(j["reference"]), esc(j["description"]), esc(j["createdBy"]), voidinfo,
        rows, fmt(td), fmt(tc))
    return shell("Journal " + j["voucher"], body, active="journal", role=role)


def render_reports_index(role=None):
    # The audit trail card is only shown to roles that may view it.
    audit_card = ""
    if has_permission(role, "ViewAuditTrail"):
        audit_card = ('<div class="col-md-6 col-lg-3"><div class="card h-100"><div class="card-body">'
                      '<h5 class="card-title">Audit Trail</h5><p class="card-text small">Append-only log of '
                      'every change to a financial record.</p>'
                      '<a class="btn btn-primary btn-sm" href="/reports/audit-trail.html">Open</a>'
                      '</div></div></div>')
    body = """
<h2>Financial Reports</h2><div class="row g-4">
<div class="col-md-6 col-lg-3"><div class="card h-100"><div class="card-body">
<h5 class="card-title">Trial Balance</h5><p class="card-text small">Debit and credit totals per account with a proof line.</p>
<a class="btn btn-primary btn-sm" href="/reports/trial-balance.html">Open</a></div></div></div>
<div class="col-md-6 col-lg-3"><div class="card h-100"><div class="card-body">
<h5 class="card-title">Income Statement</h5><p class="card-text small">Revenue and expenses for a period, with net income.</p>
<a class="btn btn-primary btn-sm" href="/reports/income-statement.html">Open</a></div></div></div>
<div class="col-md-6 col-lg-3"><div class="card h-100"><div class="card-body">
<h5 class="card-title">Balance Sheet</h5><p class="card-text small">Assets, liabilities and equity as of a date.</p>
<a class="btn btn-primary btn-sm" href="/reports/balance-sheet.html">Open</a></div></div></div>
<div class="col-md-6 col-lg-3"><div class="card h-100"><div class="card-body">
<h5 class="card-title">General Ledger</h5><p class="card-text small">Account movements with a running balance.</p>
<a class="btn btn-primary btn-sm" href="/reports/general-ledger.html">Open</a></div></div></div>
%s
</div>""" % audit_card
    return shell("Reports", body, active="reports", role=role)


def _daterange(qs):
    f = qs.get("fromDate", ["2026-01-01"])[0]
    t = qs.get("toDate", [TODAY.isoformat()])[0]
    try:
        return date.fromisoformat(f), date.fromisoformat(t)
    except ValueError:
        return date(2026, 1, 1), TODAY


def render_trial_balance(qs, role=None):
    f, t = _daterange(qs)
    r = trial_balance(f, t)
    rows = "".join(
        "<tr><td>%s</td><td>%s</td><td>%s</td><td class='text-end'>%s</td>"
        "<td class='text-end'>%s</td><td class='text-end'>%s</td></tr>" % (
            x["code"], esc(x["name"]), x["type"], fmt(x["debit"]), fmt(x["credit"]), fmt(x["net"]))
        for x in r["rows"])
    alert = ("<div class='alert alert-success'>The trial balance is in balance.</div>"
             if r["balanced"] else
             "<div class='alert alert-danger'>The trial balance does not balance. Debits %s vs credits %s.</div>"
             % (fmt(r["totalDebit"]), fmt(r["totalCredit"])))
    body = """
<h2>Trial Balance</h2>
<form method="get" class="row g-3 align-items-end mb-3">
<div class="col-auto"><label class="form-label">From</label>
<input name="fromDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><label class="form-label">To</label>
<input name="toDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><button class="btn btn-primary">Run</button>
<button type="button" class="btn btn-outline-secondary" onclick="window.print()">Print</button></div>
</form>%s
<table class="table table-sm table-striped"><thead><tr><th>Code</th><th>Account</th><th>Type</th>
<th class="text-end">Debit</th><th class="text-end">Credit</th><th class="text-end">Net</th></tr></thead>
<tbody>%s</tbody>
<tfoot><tr class="table-secondary fw-bold"><td colspan="3" class="text-end">Control totals</td>
<td class="text-end">%s</td><td class="text-end">%s</td><td></td></tr></tfoot></table>""" % (
        f.isoformat(), t.isoformat(), alert, rows, fmt(r["totalDebit"]), fmt(r["totalCredit"]))
    return shell("Trial Balance", body, active="reports", role=role)


def render_income_statement(qs, role=None):
    f, t = _daterange(qs)
    r = income_statement(f, t)
    rows = "".join("<tr><td>%s</td><td>%s</td><td>%s</td><td class='text-end'>%s</td></tr>"
                   % (x["code"], esc(x["name"]), x["section"], fmt(x["amount"]))
                   for x in r["lines"])
    body = """
<h2>Income Statement</h2>
<form method="get" class="row g-3 align-items-end mb-3">
<div class="col-auto"><label class="form-label">From</label>
<input name="fromDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><label class="form-label">To</label>
<input name="toDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><button class="btn btn-primary">Run</button>
<button type="button" class="btn btn-outline-secondary" onclick="window.print()">Print</button></div></form>
<div class="row"><div class="col-lg-7">
<table class="table table-sm table-striped">
<thead><tr><th>Code</th><th>Account</th><th>Section</th><th class="text-end">Amount</th></tr></thead>
<tbody>%s</tbody>
<tfoot>
<tr class="fw-bold"><td colspan="3" class="text-end">Total revenue</td><td class="text-end">%s</td></tr>
<tr class="fw-bold"><td colspan="3" class="text-end">Total expense</td><td class="text-end">%s</td></tr>
<tr class="table-secondary fw-bold"><td colspan="3" class="text-end">Net income</td><td class="text-end">%s</td></tr>
</tfoot></table></div>
<div class="col-lg-5"><canvas id="plChart" height="220"></canvas></div></div>""" % (
        f.isoformat(), t.isoformat(), rows, fmt(r["totalRevenue"]), fmt(r["totalExpense"]),
        fmt(r["netIncome"]))
    extra = """<script>
    new Chart(document.getElementById('plChart'),{type:'bar',data:{labels:['Revenue','Expense','Net income'],
    datasets:[{data:[%f,%f,%f],backgroundColor:['#198754','#dc3545','#0d6efd']}]},
    options:{responsive:true,plugins:{legend:{display:false}},scales:{y:{beginAtZero:true}}}});</script>""" % (
        r["totalRevenue"], r["totalExpense"], r["netIncome"])
    return shell("Income Statement", body, active="reports", role=role, extra=extra)


def render_balance_sheet(qs, role=None):
    a = qs.get("asOfDate", [TODAY.isoformat()])[0]
    try:
        as_of = date.fromisoformat(a)
    except ValueError:
        as_of = TODAY
    r = balance_sheet(as_of)
    rows = "".join("<tr><td>%s</td><td>%s</td><td>%s</td><td class='text-end'>%s</td></tr>"
                   % (x["code"], esc(x["name"]), x["section"], fmt(x["amount"]))
                   for x in r["lines"])
    rows += ("<tr><td></td><td>Current period earnings</td><td>Equity</td>"
             "<td class='text-end'>%s</td></tr>" % fmt(r["earnings"]))
    alert = ("<div class='alert alert-success'>Assets = Liabilities + Equity. The accounting equation holds.</div>"
             if r["balanced"] else
             "<div class='alert alert-warning'>The accounting equation does not hold as of this date.</div>")
    body = """
<h2>Balance Sheet</h2>
<form method="get" class="row g-3 align-items-end mb-3">
<div class="col-auto"><label class="form-label">As of</label>
<input name="asOfDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><button class="btn btn-primary">Run</button>
<button type="button" class="btn btn-outline-secondary" onclick="window.print()">Print</button></div></form>
%s
<div class="row"><div class="col-lg-7">
<table class="table table-sm table-striped">
<thead><tr><th>Code</th><th>Account</th><th>Section</th><th class="text-end">Amount</th></tr></thead>
<tbody>%s</tbody>
<tfoot>
<tr class="fw-bold"><td colspan="3" class="text-end">Total assets</td><td class="text-end">%s</td></tr>
<tr class="fw-bold"><td colspan="3" class="text-end">Total liabilities</td><td class="text-end">%s</td></tr>
<tr class="fw-bold"><td colspan="3" class="text-end">Total equity</td><td class="text-end">%s</td></tr>
<tr class="table-secondary fw-bold"><td colspan="3" class="text-end">Liabilities + equity</td>
<td class="text-end">%s</td></tr></tfoot></table></div>
<div class="col-lg-5"><canvas id="bsChart" height="220"></canvas></div></div>""" % (
        as_of.isoformat(), alert, rows, fmt(r["totalAssets"]), fmt(r["totalLiabilities"]),
        fmt(r["totalEquity"]), fmt(r["totalLiabilities"] + r["totalEquity"] + r["earnings"]))
    extra = """<script>
    new Chart(document.getElementById('bsChart'),{type:'doughnut',data:{labels:['Assets','Liabilities','Equity'],
    datasets:[{data:[%f,%f,%f],backgroundColor:['#0d6efd','#dc3545','#198754']}]},
    options:{responsive:true}});</script>""" % (
        r["totalAssets"], r["totalLiabilities"], r["totalEquity"] + r["earnings"])
    return shell("Balance Sheet", body, active="reports", role=role, extra=extra)


def render_general_ledger(qs, role=None):
    f, t = _daterange(qs)
    acc_id = qs.get("accountId", [""])[0]
    acc_id = int(acc_id) if acc_id.isdigit() else None
    r = gl_detail(acc_id, f, t)
    label = "All accounts"
    if acc_id:
        a = _BY_ID[acc_id]
        label = "%s - %s" % (a["code"], a["name"])
    opts = "\n".join('<option value="%d"%s>%s - %s</option>'
                     % (a["id"], " selected" if a["id"] == acc_id else "", a["code"], esc(a["name"]))
                     for a in ACCOUNTS if a["postable"])
    mov = "".join(
        "<tr><td>%s</td><td>%s</td><td>%s</td><td>%s</td><td>%s</td>"
        "<td class='text-end'>%s</td><td class='text-end'>%s</td><td class='text-end'>%s</td></tr>" % (
            m["code"], m["date"], esc(m["voucher"]), esc(m["reference"]), esc(m["description"]),
            fmt(m["debit"]) if m["debit"] else "", fmt(m["credit"]) if m["credit"] else "",
            fmt(m["running"])) for m in r["movements"])
    body = """
<h2>General Ledger Detail</h2>
<p class="text-muted">Account: <strong>%s</strong></p>
<form method="get" class="row g-3 align-items-end mb-3">
<div class="col-auto"><label class="form-label">Account</label>
<select name="accountId" class="form-select"><option value="">All accounts</option>
%s</select></div>
<div class="col-auto"><label class="form-label">From</label>
<input name="fromDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><label class="form-label">To</label>
<input name="toDate" type="date" class="form-control" value="%s" /></div>
<div class="col-auto"><button class="btn btn-primary">Run</button>
<button type="button" class="btn btn-outline-secondary" onclick="window.print()">Print</button></div></form>
<table class="table table-sm"><thead><tr><th>Account</th><th class="text-end">Opening balance</th></tr></thead>
<tbody><tr><td>%s</td><td class="text-end">%s</td></tr></tbody></table>
<table id="glTable" class="table table-sm table-striped" style="width:100%%">
<thead><tr><th>Account</th><th>Date</th><th>Voucher</th><th>Reference</th><th>Description</th>
<th class="text-end">Debit</th><th class="text-end">Credit</th><th class="text-end">Running balance</th></tr></thead>
<tbody>%s</tbody></table>""" % (esc(label), opts, f.isoformat(), t.isoformat(),
                                esc(label), fmt(r["opening"]), mov)
    return shell("General Ledger", body, active="reports", role=role)


def render_audit(qs, role=None):
    table = qs.get("table", [""])[0]
    action = qs.get("action", [""])[0]
    search = (qs.get("search", [""])[0] or "").strip().lower()
    rows = list(AUDIT)
    if table:
        rows = [r for r in rows if r["table"] == table]
    if action:
        rows = [r for r in rows if r["action"] == action]
    if search:
        rows = [r for r in rows
                if search in r["recordId"].lower()
                or search in r["userId"].lower()
                or search in (r["newValues"] or "").lower()
                or search in (r["oldValues"] or "").lower()]
    rows.sort(key=lambda r: r["id"], reverse=True)

    def badge(a):
        cls = {"INSERT": "success", "UPDATE": "primary", "DELETE": "danger"}.get(a, "secondary")
        return '<span class="badge text-bg-%s">%s</span>' % (cls, a)

    def snapshot_cell(v):
        if not v:
            return "<span class='text-muted'>—</span>"
        return ("<details><summary class='small text-primary'>view</summary>"
                "<code class='small'>%s</code></details>" % esc(v))

    body_rows = "".join(
        "<tr><td>%d</td><td><span class='text-muted'>%s.</span>%s</td><td>%s</td><td>%s</td>"
        "<td>%s</td><td>%s</td><td>%s</td><td>%s</td>"
        "<td>%s</td></tr>" % (
            r["id"], esc(r["schema"]), esc(r["table"]), esc(r["recordId"]), badge(r["action"]),
            r["occurredAt"].strftime("%Y-%m-%d %H:%M:%S"), esc(r["userId"]),
            snapshot_cell(r["oldValues"]), snapshot_cell(r["newValues"]), esc(r["ipAddress"]))
        for r in rows)

    tables = sorted({r["table"] for r in AUDIT})
    top = "\n".join('<option value="%s"%s>%s</option>' % (esc(t), " selected" if t == table else "", esc(t))
                    for t in tables)
    acts = "\n".join('<option value="%s"%s>%s</option>' % (a, " selected" if a == action else "", a)
                     for a in ("INSERT", "UPDATE", "DELETE"))
    body = """
<h2>Audit Trail</h2>
<p class="text-muted">Append-only. Every change to a financial record is captured with its
before/after image. Rows cannot be updated or deleted.</p>
<form method="get" class="row g-3 align-items-end mb-3">
<div class="col-auto"><label class="form-label">Table</label>
<select name="table" class="form-select"><option value="">All tables</option>%s</select></div>
<div class="col-auto"><label class="form-label">Action</label>
<select name="action" class="form-select"><option value="">All actions</option>%s</select></div>
<div class="col-auto"><label class="form-label">Search</label>
<input name="search" class="form-control" value="%s" placeholder="record, user or values" /></div>
<div class="col-auto"><button class="btn btn-primary">Filter</button>
<button type="button" class="btn btn-outline-secondary" onclick="window.print()">Print</button>
<button type="button" class="btn btn-outline-danger" id="auditTamper">Try to modify a row</button></div></form>
<div id="tamperResult"></div>
<div class="alert alert-secondary small mb-3">%d audit row(s). The trail is immutable:
an UPDATE or DELETE against <code>sec.AuditLog</code> is refused by
<code>sec.trg_AuditLog_Immutable</code>.</div>
<table id="auditTable" class="table table-sm table-striped" style="width:100%%">
<thead><tr><th>#</th><th>Table</th><th>Record</th><th>Action</th><th>When (UTC)</th><th>User</th>
<th>Old values</th><th>New values</th><th>IP</th></tr></thead>
<tbody>%s</tbody></table>""" % (top, acts, esc(search), len(rows), body_rows)
    return shell("Audit Trail", body, active="reports", role=role)


# ---------------------------------------------------------------------------
# HTTP
# ---------------------------------------------------------------------------
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, body, ctype="text/html; charset=utf-8"):
        data = body.encode("utf-8") if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _json(self, obj, code=200):
        self._send(code, json.dumps(obj), "application/json; charset=utf-8")

    def _role(self):
        from http.cookies import SimpleCookie
        cookie = self.headers.get("Cookie")
        if cookie:
            c = SimpleCookie()
            c.load(cookie)
            if "role" in c and c["role"].value in ROLES:
                return c["role"].value
        return DEFAULT_ROLE

    def _require(self, role, permission):
        # Mirror of [RequirePermission]: a user whose roles do not grant the
        # permission is denied, exactly as AuthorizeCore returns false.
        return has_permission(role, permission)

    def _deny(self, role, permission):
        self._send(403, forbidden(role, permission))

    def do_GET(self):
        u = urlparse(self.path)
        path, qs = u.path, parse_qs(u.query)
        role = self._role()
        try:
            # Role switcher (stands in for signing in as a different user).
            if path == "/switch-role":
                new = qs.get("role", [DEFAULT_ROLE])[0]
                if new not in ROLES:
                    new = DEFAULT_ROLE
                self.send_response(302)
                self.send_header("Location", "/")
                self.send_header("Set-Cookie", "role=%s; Path=/; Max-Age=86400" % new)
                self.end_headers()
                return

            pages = {
                "/journal/index.html": ("ViewLedger", lambda: render_journal_index(role)),
                "/journal/create.html": ("CreateDraftJournal", lambda: render_journal_create(role)),
                "/journal/details.html": ("ViewLedger", lambda: render_details(qs.get("id", ["0"])[0], role)),
                "/reports/index.html": ("ViewLedger", lambda: render_reports_index(role)),
                "/reports/trial-balance.html": ("ViewLedger", lambda: render_trial_balance(qs, role)),
                "/reports/income-statement.html": ("ViewLedger", lambda: render_income_statement(qs, role)),
                "/reports/balance-sheet.html": ("ViewLedger", lambda: render_balance_sheet(qs, role)),
                "/reports/general-ledger.html": ("ViewLedger", lambda: render_general_ledger(qs, role)),
                "/reports/audit-trail.html": ("ViewAuditTrail", lambda: render_audit(qs, role)),
            }

            if path in ("/", "/index.html"):
                return self._send(200, render_dashboard(role))
            if path in pages:
                perm, fn = pages[path]
                if not self._require(role, perm):
                    return self._deny(role, perm)
                return self._send(200, fn())
            if path == "/api/journal/list":
                if not self._require(role, "ViewLedger"):
                    return self._json({"success": False,
                                       "message": "The current user does not have the 'ViewLedger' permission."}, 403)
                return self._api_list(qs)
            if path == "/api/accounts":
                return self._json([{"id": a["id"], "code": a["code"], "name": a["name"]}
                                   for a in ACCOUNTS if a["postable"]])
            if path.startswith("/vendor/") or path.startswith("/content/") or path.startswith("/scripts/"):
                return self._static(path)
            self._send(404, shell("Not found", "<div class='alert alert-danger'>Not found.</div>", role=role))
        except DomainError as ex:
            self._json({"success": False, "message": str(ex)}, 400)
        except Exception as ex:  # pragma: no cover
            self._json({"success": False, "message": "Server error: %s" % ex}, 500)

    def do_POST(self):
        u = urlparse(self.path)
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b"{}"
        try:
            payload = json.loads(raw or b"{}")
        except ValueError:
            payload = {}
        user = "j.smith"
        role = self._role()
        # Each write endpoint carries the same permission as its controller action.
        required = {
            "/api/journal/create": "CreateDraftJournal",
            "/api/journal/approve": "ApproveJournal",
            "/api/journal/post": "PostJournal",
            "/api/journal/void": "VoidJournal",
            "/api/audit/tamper": "ViewAuditTrail",
        }
        try:
            if u.path in required:
                perm = required[u.path]
                if not self._require(role, perm):
                    return self._json({"success": False,
                                       "message": "The current user does not have the '%s' permission." % perm}, 403)
                if u.path == "/api/journal/create":
                    j = create_draft(payload, user)
                    return self._json({"success": True, "id": j["id"],
                                       "message": "Draft journal %s saved." % j["voucher"]})
                if u.path == "/api/journal/approve":
                    approve(payload.get("id"), user)
                    return self._json({"success": True, "message": "Journal entry approved."})
                if u.path == "/api/journal/post":
                    post(payload.get("id"), user)
                    return self._json({"success": True, "message": "Journal entry posted."})
                if u.path == "/api/journal/void":
                    j, rev = void(payload.get("id"), user, payload.get("reason"))
                    return self._json({"success": True,
                                       "message": "Journal entry voided and reversed by %s." % rev["voucher"]})
                if u.path == "/api/audit/tamper":
                    # Demonstrates append-only enforcement: the write is always refused.
                    try:
                        audit_tamper(payload.get("id"))
                    except DomainError as ex:
                        return self._json({"success": False, "message": str(ex)}, 400)
            self._json({"success": False, "message": "Unknown endpoint."}, 404)
        except DomainError as ex:
            self._json({"success": False, "message": str(ex)}, 400)

    def _api_list(self, qs):
        def g(k, d=None):
            return qs.get(k, [d])[0]
        draw = int(g("draw", 0) or 0)
        start = max(int(g("start", 0) or 0), 0)
        length = min(max(int(g("length", 25) or 25), 1), 200)
        status = g("status", "") or ""
        search = (g("search", "") or "").strip().lower()

        rows = [j for j in JOURNALS]
        if status.isdigit():
            rows = [j for j in rows if j["status"] == int(status)]
        if search:
            rows = [j for j in rows if search in j["voucher"].lower()
                    or search in j["reference"].lower()
                    or search in j["description"].lower()]
        rows.sort(key=lambda j: (j["date"], j["id"]), reverse=True)
        total = len(rows)
        page = rows[start:start + length]
        data = [{
            "JournalId": j["id"], "VoucherNumber": j["voucher"],
            "TransactionDate": j["date"].isoformat(), "Reference": j["reference"],
            "Status": STATUS_NAME[j["status"]],
            "TotalDebit": sum(l["debit"] for l in j["lines"]),
            "TotalCredit": sum(l["credit"] for l in j["lines"]),
            "PostedBy": j["postedBy"] or "",
        } for j in page]
        self._json({"draw": draw, "recordsTotal": total, "recordsFiltered": total, "data": data})

    def _static(self, path):
        import os
        # Serve assets relative to this file so the preview runs from any checkout.
        safe = path.lstrip("/")
        full = os.path.join(os.path.dirname(os.path.abspath(__file__)), safe)
        if not os.path.isfile(full):
            return self._send(404, "not found", "text/plain")
        ctype = "text/plain"
        if full.endswith(".css"):
            ctype = "text/css"
        elif full.endswith(".js"):
            ctype = "application/javascript"
        with open(full, "rb") as fh:
            self._send(200, fh.read(), ctype)


if __name__ == "__main__":
    srv = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    print("live preview on http://0.0.0.0:%d" % PORT)
    srv.serve_forever()
