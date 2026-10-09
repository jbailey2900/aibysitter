# Self-hosting

Reference deployment: Windows Server, IIS in-process, behind Cloudflare. Other ASP.NET Core hosts work; the steps below cover IIS.

## Configuration

Set on IIS as environment variables (`__` replaces `:`). Nothing secret goes in `appsettings*.json`.

| Variable | Required | Value |
|---|---|---|
| `AllowedHosts` | Yes | Your host names, `;`-separated. `appsettings.Production.json` ships `aibysitting.net;www.aibysitting.net`. |
| `DataProtection__KeysPath` | Yes | Folder for Data Protection keys. Without it, keys are ephemeral and Lint forms loaded before a restart return 400. |
| `Serilog__WriteTo__0__Args__path` | No | Rolling log file path. Default `C:\Logs\aibysitter\aibysitter-.log`. |
| `ForwardedHeaders__KnownNetworks__<n>` | No | Proxy CIDR ranges trusted for `X-Forwarded-For`. Default: Cloudflare ranges in `appsettings.json`. Overrides apply per index and do not shorten the list. |
| `RateLimiting__Lint__PermitLimit` | No | Lint POSTs per IP per window. Default 20. |
| `RateLimiting__Lint__WindowSeconds` | No | Window length. Default 60. |
| `RateLimiting__Badge__PermitLimit` | No | Badge requests per IP per window. Default 60. |
| `RateLimiting__Badge__WindowSeconds` | No | Window length. Default 60. |
| `ConnectionStrings__Aibysitter` | For score history | SQL Server connection string, for example `Server=.;Database=Aibysitter;Integrated Security=true;TrustServerCertificate=true`. |
| `GitHub__AppId` | App only | App ID or Client ID. |
| `GitHub__WebhookSecret` | App only | Webhook secret. |
| `GitHub__PrivateKeyPath` | App only | Path to the App's private key PEM. |
| `ReviewQueue__Path` | App only, recommended | Folder for queued review jobs. Without it, jobs are kept in memory and lost on restart. |

Without the three `GitHub__*` values the site runs and `POST /github/webhook` returns 503.

Without `ConnectionStrings__Aibysitter` the site runs with score history off: nothing is stored, the lint page shows no history, and badges render from live fetches.

## Database (score history)

- SQL Server 2022. One table, `dbo.ScoreHistory`.
- Create the database and logins once with [`deploy/create-database.sql`](../deploy/create-database.sql), as a sysadmin. Set `@RunnerAccount` at the top.
  - Runner service account: `db_owner`, applies migrations.
  - App pool identity: `db_datareader`, `db_datawriter`.
- The app never creates or migrates the database. Migrations run from the deploy workflow.
- Migrations are additive: the running version keeps working after a migration is applied.
- Retention: a daily job in the app deletes rows older than 400 days and all but the newest 500 per repository and file.

## Server

- .NET 10 Hosting Bundle; restart IIS after install.
- .NET 10 SDK, if the server builds (deploy workflow below).
- Log folder exists; the app pool identity has Modify on it.
- Data Protection keys folder exists; the app pool identity has Modify on it. On Windows, keys are encrypted with machine-scope DPAPI.
- GitHub App private key: the app pool identity has Read on the PEM. Replacing the file needs no recycle.
- Review queue folder exists; the app pool identity has Modify on it.

## App pool and site

- App pool: No Managed Code, Integrated pipeline.
- Site: HTTPS binding per host name, SNI.
- `ASPNETCORE_ENVIRONMENT` unset (Production).

## Behind Cloudflare

- DNS records proxied.
- SSL/TLS mode Full (strict), with a Cloudflare Origin CA certificate on the IIS bindings.
- Firewall: inbound 443 limited to Cloudflare IP ranges.
- WAF/bot rules: skip `/github/webhook`, or GitHub deliveries are challenged.
- Recommended: per-hostname Authenticated Origin Pulls with your own client certificate. Without it, any Cloudflare zone pointed at the origin IP passes the firewall and skips this zone's WAF rules. Zone-level Authenticated Origin Pulls uses a certificate shared by all Cloudflare zones and does not prevent this.
  1. Create a client certificate and private key, with a CA you control.
  2. Upload them under **SSL/TLS → Origin Server → Authenticated Origin Pulls** as a per-hostname certificate for each host name, and turn the setting on for those host names.
  3. On the server, import the CA certificate into **Local Computer → Trusted Root Certification Authorities**.
  4. In IIS, **SSL Settings** for the site: **Require SSL**, client certificates **Require**.
  5. Accept only that CA: import it into **Local Computer → Client Authentication Issuers** as well, and set `sslctlstorename=ClientAuthIssuer` on each host name's binding with `netsh http update sslcert hostnameport=<host>:443`. Without this, IIS accepts a client certificate from any trusted CA.

Behind a different proxy: replace the `ForwardedHeaders:KnownNetworks` list in `appsettings.json` with that proxy's ranges. With no proxy, the default list can stay; `X-Forwarded-For` is honored only from listed ranges.

## GitHub App

Register an App with:

- Permissions: Checks read/write, Pull requests read, Contents read, Metadata read.
- Event: Pull request.
- Webhook URL: `https://<host>/github/webhook`, with a secret.

Verify: a webhook ping returns 200 `pong`.

Review queue, with `ReviewQueue__Path` set:

- Each job is a file, `<delivery ID>.json`, written before the webhook returns 202 and deleted when its check run completes.
- On start, saved jobs are queued again, oldest first. A review interrupted by a recycle or crash resumes.
- A job interrupted 3 times has its check run closed as `Review failed`.
- Unreadable job files move to `failed/` in the folder and are logged.
- Redelivering a webhook re-queues its saved job against the existing check run. If the job is being reviewed, the redelivery does nothing.

Without `ReviewQueue__Path`, a review lost to a recycle or crash leaves the `Aibysitter review` check in `queued`. Recover by redelivering the webhook from the App's advanced settings or pushing a new commit.

## Publishing

Folder publish: `dotnet publish src/Aibysitter.Web -p:PublishProfile=FolderProfile`. Output: `src/Aibysitter.Web/bin/Release/net10.0/publish/`.

Copy with `app_offline.htm` in place, then remove it.

## Deploy workflow

`.github/workflows/deploy.yml` runs after CI succeeds on `main`, on a self-hosted Windows runner.

- Runner labels: `self-hosted`, `windows`, and the value of repo variable `RUNNER_LABEL`.
- Repo variable `SITE_PATH`: the IIS site's physical path.
- Repo secret `DB_CONNECTION`: connection string the runner uses to apply migrations, for example `Server=.;Database=Aibysitter;Integrated Security=true;TrustServerCertificate=true`.
- Migrations: the workflow builds an EF Core migrations bundle and applies it before taking the site offline. A failed migration fails the workflow and leaves the current site serving.
- Runner service account: Modify on `SITE_PATH`.
- The health check resolves `aibysitting.net` to 127.0.0.1; change the host name in the workflow for another domain.
- On failure, `app_offline.htm` stays in place. Fix and re-run.

For a public fork, require approval for fork pull request workflows (Settings → Actions → General).

## Smoke checks

- `/health` returns `Healthy`.
- Lint page: paste a rules file; findings render.
- Responses include `Content-Security-Policy`, `X-Content-Type-Options`, `Referrer-Policy`, `Strict-Transport-Security`.
- Unknown `Host` header returns 400.
- Log lines show client IPs, not proxy IPs.
- More than `PermitLimit` Lint POSTs from one IP within the window returns 429.
