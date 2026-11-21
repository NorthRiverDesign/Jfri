# Jfri: Just F*ing Run It

Point Jfri at a repository. It works out what the app is and what it needs in production,
shows what it costs at several hosting providers, and deploys it with one click: a hardened
server, HTTPS, a private database with backups, persistent storage and CI/CD.

Docker and Kubernetes made deployments portable, not easy. Most web apps already say what they
need: `composer.json`, a `.csproj`, `package.json`, `.env.example`, migrations folders. The
production-grade answers are well established too. Jfri connects the two.

## What it does

1. **Analyze** a repository (static reads only; nothing from the repo is ever executed).
   It finds the stack, runtime version, web entry point, database, cache, queue worker,
   scheduler, migrations, health check, persistent directories and settings. Every finding
   carries its evidence and a confidence. An existing `Dockerfile` always wins over inference.
   When Jfri can't recognise an app, it says what it saw instead of guessing.
2. **Plan** a production deployment you can review and adjust: runtime, domain, database disk,
   backups, extra environment.
3. **Estimate** the monthly cost on Hetzner Cloud, DigitalOcean, Vultr and Akamai (Linode).
   Vultr and Linode prices are live from their public APIs. Hetzner and DigitalOcean are live
   once you add a token, and otherwise come from a dated catalog.
4. **Deploy**: create a server through the provider's API (or use your own over SSH), harden
   it, build the image on it, run migrations, roll out, and check it answers over HTTPS.
5. **Keep deploying**: a generated GitHub Actions workflow runs your tests, then triggers Jfri
   through an HMAC-signed webhook. A plain GitHub push webhook works too.

### Stacks

| Stack | Recognised |
|---|---|
| PHP | Laravel (worker, Horizon, scheduler, Vite, storage), Symfony, WordPress, plain PHP |
| .NET | ASP.NET Core (Web SDK project in a solution, EF Core providers via project references) |
| Node.js | Next.js, Nuxt, SvelteKit, Remix/React Router, NestJS, Express, Fastify, Hono, Vite SPAs |
| Python | Django (+ Celery), Flask, FastAPI with pip, Poetry, uv or Pipenv |
| Others | Rails, Go, Spring Boot, static sites, anything with a Dockerfile |

Databases: PostgreSQL, MySQL, MariaDB, MongoDB, SQL Server, SQLite. Caches: Redis (run as Valkey).

### What "production-grade" means here

- Multi-stage images on official runtime images, matching the version the repo asks for,
  running as a **non-root** user.
- **Databases are private**: no public port, a generated password (kept stable across deploys),
  data on a persistent volume, nightly dumps with retention, and optional provider snapshots.
- **Secrets** are generated with a CSPRNG and only reach the server inside 0600 files or
  Kubernetes Secrets (k3s with secrets encryption at rest). They're scrubbed from logs. One
  exception: the cache password is passed to `valkey-server` as an argument, so root on the
  server can see it in the process list.
- **Release step**: migrations run once per deploy before new code takes traffic.
- **HTTPS** from the first deploy (Let's Encrypt via Traefik or Caddy). The address is
  `<ip>.sslip.io` until you point a domain at the server.
- **Servers are hardened**: a firewall allowing only 22/80/443, unattended security upgrades,
  fail2ban, swap on small machines and rotated container logs.
- **Kubernetes** (single-node k3s, the default): rolling updates, probes, resource limits,
  a disruption budget, Pod Security admission, default-deny network policies.
  **Docker Compose** is the lighter alternative for small servers.

## Run it

```sh
# With Docker, behind Caddy for HTTPS:
JFRI_DOMAIN=jfri.example.com docker compose up -d
docker compose logs jfri | grep "setup token"

# Or from source (.NET 10 SDK, git and OpenSSH client required):
dotnet run --project src/Jfri.Web        # http://localhost:5180
```

On first start Jfri prints a one-time **setup token** (also written to `setup-token` in its
data directory). Creating the admin account requires it, so nobody else can claim a fresh instance.

Configuration:

| Setting | Default |
|---|---|
| `JFRI_DATA_DIR` | `~/.local/share/jfri` (`/data` in the container) |
| `JFRI_TRUST_PROXY` | `false`; set `true` behind a reverse proxy |
| `ASPNETCORE_URLS` | `http://localhost:5180` |

The data directory holds the SQLite database, the Data Protection key ring that encrypts
stored tokens and keys, repository checkouts and deployment logs. Back it up, and protect it
like a password store.

## CLI

```sh
dotnet run --project src/Jfri.Cli -- analyze  ../my-app
dotnet run --project src/Jfri.Cli -- plan     ../my-app --runtime compose --out ./jfri-out
dotnet run --project src/Jfri.Cli -- estimate ../my-app
```

`plan --out` writes the Dockerfile, Kubernetes or Compose files and CI workflow, with secret
placeholders, so you can commit or run them without Jfri.

## Security model

- A single admin account (PBKDF2 hashes via ASP.NET Core Identity's hasher), rate-limited
  sign-in, SameSite=Strict cookies, antiforgery tokens on every form, and a strict CSP with no
  inline script.
- Provider tokens, SSH private keys, git tokens and generated app secrets are encrypted at rest
  with ASP.NET Core Data Protection. Tokens are never rendered back.
- Repositories: https URLs only, with no credentials in the URL. The token travels as an
  HTTP header through git's environment config. Clones are shallow, with submodules, hooks and
  symlinks disabled and a size cap. The analyzer never follows symlinks.
- SSH: one ed25519 key per server. The host key is pinned on first connect and verified
  strictly afterwards. Scripts go over stdin; non-root users run them through `sudo -n`.
- Webhooks: HMAC-SHA256 (`X-Hub-Signature-256`), compared in constant time.

## Development

```sh
dotnet build Jfri.slnx
dotnet test Jfri.slnx
dotnet tool restore && dotnet ef migrations add <Name> -p src/Jfri.Web -o Data/Migrations
```

| Project | Purpose |
|---|---|
| `src/Jfri.Core` | Analyzer, planner, generators, pricing, providers, SSH executor. No web dependencies. |
| `src/Jfri.Web` | ASP.NET Core Razor Pages UI, EF Core (SQLite), deployment worker, webhooks. |
| `src/Jfri.Cli` | `jfri analyze / plan / estimate`. |
| `tests/` | Fixture repositories per stack, generator, pricing, provider, executor and web tests. |

Adding a stack means adding an `IStackDetector` in `src/Jfri.Core/Analysis/Detectors`, a
Dockerfile recipe in `DockerfileGenerator` and a fixture repository with tests.

## Status

Jfri is an experiment.

- **Covered by tests:**
  - analysis on fixture repos for each stack
  - planning
  - generated artifacts (YAML parsed, shell checked with `bash -n`, Compose checked with
    `docker compose config`)
  - cost estimates
  - deployment step order against a fake server

  The web UI has been run against real public repositories, with live Vultr and Linode prices.
- **Not yet verified:**
  - **Provider clients:** tested only against hand-written responses. Hetzner's request
    shape matches its published OpenAPI spec. The Vultr and Linode create requests, and
    DigitalOcean's Ubuntu image slug, haven't been checked against a real account.
  - **Real deployments:** no deploy has run against a real server yet. The first deploys are
    beta, so watch the log.

Ideas for later:

- managed databases (DigitalOcean, etc.)
- off-server backups to S3-compatible storage
- multi-node clusters
- preview environments per pull request
- GitLab/Bitbucket OAuth

## License

MIT
