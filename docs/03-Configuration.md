# 3. Configuration (`web.config`)

The application reads exactly one connection string, by name:

```csharp
ConfigurationManager.ConnectionStrings["AccountingDbConnection"].ConnectionString
```

See `src/AccountingSystem.Data/Context/DbConnectionFactory.cs`. Nothing is
hard-coded, so the same build runs against LocalDB, `.\SQLEXPRESS`, or a full
SQL Server instance by changing configuration only.

## 3.1 Connection string options

Only one `<add name="AccountingDbConnection" ... />` may be active. The others
are commented out in `Web.config`.

### Option 1 - Windows Authentication (recommended)

Connecting to `.\SQLEXPRESS` with Integrated Security. No password is stored.

```xml
<add name="AccountingDbConnection"
     connectionString="Data Source=.\SQLEXPRESS;Initial Catalog=AccountingSystemDB;Integrated Security=True;MultipleActiveResultSets=True;Application Name=AccountingSystem;Connect Timeout=30;Encrypt=False;TrustServerCertificate=True"
     providerName="System.Data.SqlClient" />
```

`Integrated Security=True` is equivalent to `Integrated Security=SSPI`. The
process identity of the web application authenticates - the IIS application pool
identity in production, or your Visual Studio account under IIS Express.

### Option 1b - LocalDB

For developer machines without a SQLEXPRESS instance:

```xml
<add name="AccountingDbConnection"
     connectionString="Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=AccountingSystemDB;Integrated Security=True;MultipleActiveResultSets=True;Application Name=AccountingSystem;Connect Timeout=30"
     providerName="System.Data.SqlClient" />
```

### Option 2 - SQL Server Authentication

Connecting to `.\SQLEXPRESS` with a dedicated, least-privilege SQL login:

```xml
<add name="AccountingDbConnection"
     connectionString="Data Source=.\SQLEXPRESS;Initial Catalog=AccountingSystemDB;User ID=AccountingUser;Password=YourSecurePassword;MultipleActiveResultSets=True;Application Name=AccountingSystem;Connect Timeout=30;Encrypt=False;TrustServerCertificate=True"
     providerName="System.Data.SqlClient" />
```

**Do not commit the real password.** Choose one of:

- **Encrypt the section** (DPAPI or RSA protected configuration):

  ```bat
  aspnet_regiis -pe "connectionStrings" -app "/AccountingSystem" ^
      -prov "RsaProtectedConfigurationProvider"
  ```

  Decrypt for editing with `-pd` instead of `-pe`. The encrypted value is
  machine- or key-container-specific and is not portable to another server, so
  re-encrypt after deployment.

- **Inject at deploy time** from a secret store or an IIS application-pool
  environment variable, and reference it from a build/publish step.
- **Use a Windows service account** instead, and switch to Option 1.

### Option 3 - Remote instance, encrypted channel

```xml
<add name="AccountingDbConnection"
     connectionString="Data Source=tcp:sqlsrv01.example.internal,1433;Initial Catalog=AccountingSystemDB;Integrated Security=SSPI;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True;Application Name=AccountingSystem;Connect Timeout=30"
     providerName="System.Data.SqlClient" />
```

Set `Encrypt=True` with `TrustServerCertificate=False` only when the server
presents a certificate the client trusts. `TrustServerCertificate=True`
disables certificate validation and should be limited to development.

## 3.2 Connection string key reference

| Keyword | Purpose | Guidance |
|---------|---------|----------|
| `Data Source` | Server/instance | `.\SQLEXPRESS` or `(localdb)\MSSQLLocalDB`. |
| `Initial Catalog` | Database | `AccountingSystemDB`. |
| `Integrated Security` | Windows auth | `True` or `SSPI`. |
| `User ID` / `Password` | SQL auth | Only with Option 2; keep secret. |
| `MultipleActiveResultSets` | MARS | `True` - the reporting repository reads multiple result sets. |
| `Application Name` | Auditability | Shows in SQL Server activity monitors. |
| `Connect Timeout` | Login timeout | 30 seconds. |
| `Encrypt` / `TrustServerCertificate` | Channel security | `Encrypt=True;TrustServerCertificate=False` in production. |

## 3.3 Entity Framework configuration

`Web.config` declares the EF6 section and the SQL Server provider:

```xml
<configSections>
  <section name="entityFramework"
           type="System.Data.Entity.Internal.ConfigFile.EntityFrameworkSection, EntityFramework, Version=6.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
           requirePermission="false" />
</configSections>
...
<entityFramework>
  <defaultConnectionFactory type="System.Data.Entity.Infrastructure.SqlConnectionFactory, EntityFramework" />
  <providers>
    <provider invariantName="System.Data.SqlClient"
              type="System.Data.Entity.SqlServer.SqlProviderServices, EntityFramework.SqlServer" />
  </providers>
</entityFramework>
```

`AccountingDbContext` is constructed from the resolved connection string and sets
`Database.SetInitializer<AccountingDbContext>(null)`, so EF never creates,
migrates or drops a schema. Schema changes are applied through the versioned SQL
scripts in `database/`.

## 3.4 Application settings

| Key | Default | Meaning |
|-----|---------|---------|
| `Accounting:CompanyName` | Example Company Ltd | Shown in the UI. |
| `Accounting:BaseCurrency` | USD | Reporting currency label. |
| `Accounting:AllowPostingToClosedPeriod` | false | Must remain false for compliance. |
| `Accounting:RequireApprovalBeforePosting` | true | Enforce the Draft -> Approved -> Posted flow. |
| `Accounting:JournalNumberPrefix` | JV | Voucher prefix. |
| `Accounting:DefaultArAccountCode` | 1100 | AR control account. |
| `Accounting:DefaultApAccountCode` | 2100 | AP control account. |
| `Accounting:DefaultTaxAccountCode` | 2200 | Sales tax payable. |
| `Accounting:DefaultBankAccountCode` | 1010 | Bank/cash account. |
| `Accounting:RoleMap:<Role>` | empty | Windows group(s) mapped to each application role. See [Security](04-Security-and-Compliance.md#mapping-windows-groups-to-application-roles). |
| `Accounting:DefaultRoleForAuthenticatedUsers` | empty | Development fallback role. Leave empty in production. |

## 3.5 Security headers and hardening

`Web.config` sets these response headers:

```xml
<customHeaders>
  <remove name="X-Powered-By" />
  <add name="X-Content-Type-Options" value="nosniff" />
  <add name="X-Frame-Options" value="SAMEORIGIN" />
  <add name="Referrer-Policy" value="strict-origin-when-cross-origin" />
  <add name="Content-Security-Policy"
       value="default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; ..." />
</customHeaders>
```

Other hardening already present:

- `<compilation debug="false" />` - no debug symbols or verbose errors in production.
- `<customErrors mode="On" ... />` - clients never see a stack trace.
- `<httpRuntime enableVersionHeader="false" />` - no ASP.NET version header.
- `<httpCookies httpOnlyCookies="true" sameSite="Lax" />` - script-inaccessible cookies.
- `<authentication mode="Windows" />` with `<deny users="?" />` - authenticated by default.
- `<requestFiltering>` blocks dangerous upload extensions.
- `Global.asax.cs` removes `Server`, `X-AspNet-Version` and `X-AspNetMvc-Version`
  headers and adds HSTS over HTTPS.

### About `requireSSL` on cookies

`httpCookies requireSSL` is set to `false` in the committed file so the
anti-forgery cookie works under IIS Express over HTTP. **Set it to `true` once
the site is served over HTTPS in every environment.** The CSP allows inline
scripts because the Razor views embed small configuration scripts; to remove
`'unsafe-inline'`, move those inline scripts to external files with a nonce.

## 3.6 Binding redirects

The `<runtime>/<assemblyBinding>` block redirects MVC, Razor, WebPages and
Newtonsoft.Json to the versions the project references. Keep these in step when
you update NuGet packages.
