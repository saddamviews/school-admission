# School Admission System

.NET 8 (minimal API, Npgsql) + PostgreSQL 16 + React 18 (Vite, TypeScript), all in Docker.

## চালানোর ধাপ
```bash
docker compose up --build
```
- Frontend: http://localhost:3000
- Backend API: http://localhost:8080
- PostgreSQL (host থেকে): localhost:5433

মেয়াদ ছোট করে পরীক্ষা করতে: `HOLD_DURATION_SECONDS=30 docker compose up --build` (PowerShell-এ `$env:HOLD_DURATION_SECONDS=30; docker compose up --build`)। PowerShell-এ মানটি terminal-এ থেকে যায়: পরে নতুন terminal খুলুন বা `Remove-Item Env:HOLD_DURATION_SECONDS` দিন।

বন্ধ করতে `docker compose down` (data থাকবে, `pgdata` volume-এ)। সব মুছতে `docker compose down -v`।

## Environment variable
| Variable | কোথায় | Default |
|---|---|---|
| `DB_CONNECTION` | backend | compose-এ `Host=db;...` |
| `HOLD_DURATION_SECONDS` | backend | `600` |
| `API_URL` | frontend (runtime) | `http://localhost:8080` |

## Test চালানো
Postgres চালু থাকতে হবে:
```bash
docker compose up -d db
cd backend/tests/Backend.Tests
dotnet test
```
**Frontend test** (Vitest + Testing Library, DB/Docker লাগে না):
```bash
cd frontend
npm install
npm test
```

Backend test-এর DB বদলাতে `TEST_DB_CONNECTION` দিন। Tests: concurrency (১০০ parallel request, ১০ seat), duplicate + cancel, confirm idempotency + expiry।

## Design সংক্ষেপে
- `POST /classes/{id}/holds` একটি transaction-এ class row `SELECT ... FOR UPDATE` করে, তারপর count দেখে insert করে। তাই overbooking হয় না।
- Expiry: সব count/read-এ `expires_at > now()` ধরা হয় (lazy), সাথে `BackgroundService` পুরোনো HELD-কে EXPIRED করে।
- বিস্তারিত অনুমান: `ASSUMPTIONS.md`।
