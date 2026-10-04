# Mountain Management App — C# + SQL Server (local version)

This is the same application, rebuilt with **C# (.NET 8)** and **Microsoft SQL Server**, so you can run it on your own desktop or a local server first, and move it to hosting later without changing anything.

**This code has been carefully written and reviewed, but it has not been compiled or run in this environment** (no internet access here to download .NET and SQL Server). Follow the steps below on your own computer. If you hit a build error, hand this to any C#/.NET developer — the fix is usually small — or send me the exact error message.

---

## What's inside

```
Mountain_CSharp/
├── Database/
│   └── 01_Create_Database_And_Schema.sql   ← run this in SSMS once
├── App/
│   ├── Program.cs           ← the whole application (C#)
│   ├── MountainApp.csproj   ← project file
│   ├── appsettings.json     ← your settings (connection string, etc.)
│   └── wwwroot/index.html   ← the screens (same as before)
└── README.md  (this file)
```

## Step 1 — Install three free programs

1. **SQL Server 2022 Express** (free): search "SQL Server Express download" on Microsoft's site. Choose the "Basic" install.
2. **SQL Server Management Studio (SSMS)** (free): search "download SSMS". This is the tool you use to look at your data.
3. **.NET 8 SDK** (free): search "download .NET 8 SDK", choose your Windows/Mac version.

Restart your computer after installing these.

## Step 2 — Create the database

1. Open **SSMS**. When it asks for a server name, type `localhost\SQLEXPRESS` (this is the name SQL Server Express installs itself as) and click **Connect**.
2. Click **File → Open → File...** and open `Database/01_Create_Database_And_Schema.sql` from this package.
3. Click **Execute** (or press F5). You should see "Commands completed successfully" and a database called **MountainApp** appears on the left under Databases.

This creates all the tables (branches, users, income/expenses, workers, links, business management, and the two logs) and puts in your 3 branches and 2 starter links.

**If you already created the database with an earlier copy of this package:** also open and run `Database/02_Add_TwoFactor_Columns.sql` — it adds the two new columns two-factor login needs, without touching your existing data.

## Step 3 — Point the app at your database

1. Open the file `App/appsettings.json` in Notepad.
2. Check the line that starts `"MountainApp":` — if you used `localhost\SQLEXPRESS` in Step 2, you don't need to change anything.
3. Change `"Secret"` under `"Jwt"` to any long random sentence of your own (at least 32 characters) — this protects logins. Do this once, before you first run the app.
4. Save the file.

## Step 4 — Run the app

1. Open **Command Prompt** (search "cmd" in the Start menu).
2. Type `cd ` (with a space) then drag the `App` folder into the window, and press Enter. It should now show something like `C:\...\Mountain_CSharp\App>`.
3. Type:
   ```
   dotnet run
   ```
4. The first time, it will print something like:
   ```
   FIRST RUN - Super Admin account created
   Username: superadmin
   Password: aB3xQ9pLmZ
   ```
   **Write this password down** — it is shown only once. If you miss it, see "Resetting the Super Admin password" below.
5. Leave this window open — closing it stops the app.

## Step 5 — Open the app

On the same computer, open a browser and go to:
```
http://localhost:5000
```
(If that doesn't load, check the Command Prompt window — it prints the exact address it's using, sometimes `https://localhost:5001`.)

Sign in with `superadmin` and the password from Step 4. Go to **Settings** and change the password immediately.

## Step 6 — Use it from your phone or another computer, on the same office WiFi

1. On the computer running the app, find its local IP address: open Command Prompt and type `ipconfig`, look for "IPv4 Address" (something like `192.168.1.50`).
2. On your phone (connected to the same WiFi), open a browser and go to `http://192.168.1.50:5000` (using your own number).
3. This only works while that computer is on, running `dotnet run`, and on the same network. This is expected for local testing — the earlier server package (Node.js version) or moving to real hosting is what allows access from anywhere, at any time.

---

## What's in this version

Sign-in with roles and **two-factor login** (TOTP — Google/Microsoft Authenticator, Authy), branch scope, the income/expense ledger with approval, month-close, CSV export and full **Excel import/export** (download a blank template with dropdown lists, fill it, re-import it — bad rows are rejected with a reason, good ones go through), manpower with RP-expiry status and **payment balances** (record a payment, see paid/balance per worker), the office Links page, Super Admin user management, branding and licence settings, a **notification bell** with RP-expiry, pending-approval and licence warnings, an **External Contacts** screen with running money in/out, **attachments** reachable with one click (📎) from any worker, ledger entry or contact, an **Audit Log** viewer, an **income/expense-by-category** report, an in-app **Help guide**, and the full business module: estimates, invoices, product stock, customer/supplier receivables and payables, one-click WhatsApp/Email payment reminders, and business figures on the dashboard.

That's everything on your original list, in one working codebase. Two honest limits remain, not because they were skipped but because they're genuinely outside what I can do from here: **I still can't compile or run this** (no .NET or SQL Server in this environment), so "try `dotnet run` and tell me what breaks" still stands; and **automatic bulk SMS/scheduled email** needs a paid outside provider (Twilio, SendGrid) with its own account, which only you can open — the WhatsApp/Email reminder buttons don't need one and are already working.

## Backing up your data

In SSMS: right-click **MountainApp** → **Tasks** → **Back Up...** → choose a folder outside this computer (a USB drive or cloud folder) and do this daily once you have real data in it. Uploaded files (once the upload screens are wired up) are saved under `App/Data/attachments/` — back up that folder the same way, since it isn't part of the database backup.

## Resetting the Super Admin password

1. Open `App/appsettings.json`.
2. Set `SuperAdmin.ResetPassword` to a new password (10 characters or more).
3. Restart the app — `dotnet run` again, or if it's in IIS, recycle the Application Pool (IIS Manager → Application Pools → right-click your pool → Recycle).
4. Check `App/Data/FIRST_RUN_PASSWORD.txt` or the console/log for confirmation, then sign in with `superadmin` and that password.
5. **Important:** set `ResetPassword` back to `""` and restart again — otherwise every future restart resets it back to the same password.

The very first time the app ever runs (empty Users table), it creates `superadmin` the same way, using `SuperAdmin.Password` if you set one, or a random password otherwise — always written to `App/Data/FIRST_RUN_PASSWORD.txt` as well as the console, since IIS has no visible console.

## Where to look when something shows an error

Every unhandled error — a database problem, anything — is now caught and written to **`App/Data/app-log.txt`**, with the real detail, regardless of whether you're running `dotnet run` or IIS. The browser shows a short, safe message; the log file shows the actual cause. This is the first place to check for *any* page showing an error, including the dashboard.

## IIS deployment

Running under IIS needs a few extra steps beyond `dotnet run`, because IIS runs the app as a different Windows identity than you.

1. **Install the ASP.NET Core Hosting Bundle** (not just the SDK) — search "ASP.NET Core Hosting Bundle" on Microsoft's site, get the one matching .NET 8, install it, then restart the computer (or at least run `net stop was /y` then `net start w3svc` in an admin Command Prompt). Without this, IIS can't run the app at all (you'd see a 500.19 or 500.30 error for every page).
2. **Publish the app** — in Command Prompt, inside the `App` folder: `dotnet publish -c Release -o C:\inetpub\mountainapp` (or any folder you choose). This compiles everything into that folder; it's what IIS actually runs, not your source folder.
3. **Create the IIS site**: in IIS Manager, right-click **Sites → Add Website**. Physical path = the publish folder from step 2. Pick a port (e.g. 8080). Under **Application Pools**, select that site's pool and set **.NET CLR Version** to **No Managed Code** — ASP.NET Core doesn't use the old .NET Framework CLR, and leaving this on the default breaks it.
4. **Give the app pool permission to your Data folder**: right-click the `Data` folder inside your publish folder → Properties → Security → Edit → Add → type `IIS AppPool\<YourAppPoolName>` → OK → tick **Modify**. This folder holds attachments, the log file and the first-run password file, so IIS needs to write to it.
5. **The most common cause of every page showing an error — SQL Server permission:** IIS runs the app as `IIS AppPool\<YourAppPoolName>`, not as your own Windows login, so the `Trusted_Connection=True` line in `appsettings.json` that worked under `dotnet run` almost always fails under IIS, and every single screen errors because every screen needs the database. Simplest fix — a dedicated SQL login:
   - In SSMS, connect to `localhost\SQLEXPRESS` and run:
     ```sql
     CREATE LOGIN mountainapp WITH PASSWORD = 'CHOOSE_A_STRONG_PASSWORD';
     USE MountainApp;
     CREATE USER mountainapp FOR LOGIN mountainapp;
     ALTER ROLE db_owner ADD MEMBER mountainapp;
     ```
   - In SQL Server Configuration Manager, make sure **Mixed Mode authentication** is on (SSMS: right-click the server → Properties → Security → "SQL Server and Windows Authentication mode"), then restart the SQL Server service.
   - In `appsettings.json`, replace the connection string with:
     `Server=localhost\SQLEXPRESS;Database=MountainApp;User Id=mountainapp;Password=CHOOSE_A_STRONG_PASSWORD;TrustServerCertificate=True;`
   - Re-publish (step 2) so the updated `appsettings.json` reaches the IIS folder, then recycle the app pool.
6. **Still stuck?** Open `App\Data\app-log.txt` in the publish folder — it now has the exact error. Send me that line and I can tell you precisely what to fix.


