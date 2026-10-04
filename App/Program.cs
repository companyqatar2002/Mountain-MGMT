using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

string ConnStr = builder.Configuration.GetConnectionString("MountainApp")
    ?? throw new Exception("Connection string 'MountainApp' is missing from appsettings.json");
string JwtSecret = builder.Configuration["Jwt:Secret"]!;
if (string.IsNullOrWhiteSpace(JwtSecret) || JwtSecret.Length < 32)
    throw new Exception("Set a Jwt:Secret of at least 32 characters in appsettings.json before running.");

SqlConnection Db() => new SqlConnection(ConnStr);
string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");
static decimal R2(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);
static bool DateOk(string? d) => !string.IsNullOrWhiteSpace(d) && DateTime.TryParse(d, out _) &&
    System.Text.RegularExpressions.Regex.IsMatch(d, @"^\d{4}-\d{2}-\d{2}$");
static bool IsUrl(string? u) => Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "https" || x.Scheme == "http");

// ---- Rate limiting for login (10 attempts / 15 minutes per IP)
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(15), PermitLimit = 10 }));
    o.RejectionStatusCode = 429;
});

// The shared front-end (wwwroot/index.html) expects snake_case field names
// (e.g. branch_id, receipt_no) in every JSON response, so every response
// in this file uses that convention, applied automatically here.
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

var app = builder.Build();

// A log file, because IIS gives you no visible console. Every line this
// app writes with Console.WriteLine also lands here, so "check the log
// file" always works, whether you're running `dotnet run` or IIS.
var LogDir = Path.Combine(app.Environment.ContentRootPath, "Data");
Directory.CreateDirectory(LogDir);
var LogFile = Path.Combine(LogDir, "app-log.txt");
void Log2File(string msg)
{
    try { File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n"); } catch { /* never let logging crash the app */ }
}
void Announce(string msg) { Console.WriteLine(msg); Log2File(msg); }

// Every /api request that throws is caught here instead of producing a
// blank error: the browser gets a real {error:...} message, and the full
// detail (including the .NET exception) is written to Data/app-log.txt —
// open that file first whenever a screen "shows an error".
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        Log2File($"UNHANDLED on {ctx.Request.Method} {ctx.Request.Path}: {ex}");
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.ContentType = "application/json";
            var msg = ex is SqlException
                ? "Cannot reach the SQL Server database. Check Data/app-log.txt and the connection string in appsettings.json."
                : "Something went wrong on the server. Check Data/app-log.txt for the detail.";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = msg }));
        }
    }
});

app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

// =====================================================================
// First-run setup: create the Super Admin account if none exists, and
// an on-demand password reset if SuperAdmin:ResetPassword is set. The
// password is written to Data/FIRST_RUN_PASSWORD.txt AS WELL AS the
// console, because IIS gives you no console to read it from.
// =====================================================================
try
{
    using var db = Db();
    db.Open();
    var hasUsers = db.ExecuteScalar<int>("SELECT COUNT(*) FROM Users") > 0;
    if (!hasUsers)
    {
        var pw = builder.Configuration["SuperAdmin:Password"];
        if (string.IsNullOrWhiteSpace(pw))
            pw = Convert.ToBase64String(RandomNumberGenerator()).Replace("+", "").Replace("/", "").Substring(0, 12);
        var hash = BCrypt.Net.BCrypt.HashPassword(pw, workFactor: 12);
        db.Execute("INSERT INTO Users(Username, FullName, PasswordHash, Role) VALUES ('superadmin','Super Admin',@h,'superadmin')", new { h = hash });
        var note = $"FIRST RUN - Super Admin account created\nUsername: superadmin\nPassword: {pw}\nSign in and change this password immediately, then delete this file.";
        Announce("========================================================\n " + note.Replace("\n", "\n ") + "\n========================================================");
        File.WriteAllText(Path.Combine(LogDir, "FIRST_RUN_PASSWORD.txt"), note);
    }
    else
    {
        // Recovery path: if the password was lost (common after moving to
        // IIS, which hides the console), set SuperAdmin:ResetPassword in
        // appsettings.json to any password (10+ characters), restart the
        // app (recycle the IIS app pool), sign in with it, then REMOVE
        // that line from appsettings.json so a later restart can't reset
        // it again.
        var reset = builder.Configuration["SuperAdmin:ResetPassword"];
        if (!string.IsNullOrWhiteSpace(reset))
        {
            if (reset.Length < 10)
                Announce("SuperAdmin:ResetPassword is set but is shorter than 10 characters - ignored.");
            else
            {
                var hash = BCrypt.Net.BCrypt.HashPassword(reset, workFactor: 12);
                var n = db.Execute("UPDATE Users SET PasswordHash=@h, Active=1 WHERE Username='superadmin'", new { h = hash });
                var note = n > 0
                    ? $"SUPERADMIN PASSWORD RESET\nUsername: superadmin\nPassword: {reset}\nNow remove SuperAdmin:ResetPassword from appsettings.json and restart again."
                    : "SuperAdmin:ResetPassword was set, but no user named 'superadmin' was found to reset.";
                Announce("========================================================\n " + note.Replace("\n", "\n ") + "\n========================================================");
                File.WriteAllText(Path.Combine(LogDir, "FIRST_RUN_PASSWORD.txt"), note);
            }
        }
    }
}
catch (Exception ex)
{
    // Do NOT let a database problem stop the app from starting at all —
    // that used to produce an opaque IIS 500.30 with no clue why. Instead
    // the app comes up, every page can load, and each API call that needs
    // the database fails with the clear message above until this is fixed.
    Announce("STARTUP WARNING - could not reach the database to check/create the Super Admin account: " + ex.Message);
    Announce("The app will still start. Fix the connection string in appsettings.json (see README 'IIS troubleshooting'), then restart.");
}
static byte[] RandomNumberGenerator()
{
    var b = new byte[9];
    System.Security.Cryptography.RandomNumberGenerator.Fill(b);
    return b;
}

// =====================================================================
// Auth helpers
// =====================================================================
string MakeToken(int userId)
{
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        claims: new[] { new Claim("id", userId.ToString()) },
        expires: DateTime.UtcNow.AddHours(12),
        signingCredentials: creds);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

int? VerifyToken(HttpRequest req)
{
    var h = req.Headers.Authorization.ToString();
    if (!h.StartsWith("Bearer ")) return null;
    var tok = h["Bearer ".Length..];
    try
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var principal = handler.ValidateToken(tok, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = key
        }, out _);
        return int.Parse(principal.FindFirst("id")!.Value);
    }
    catch { return null; }
}

async Task Log(SqlConnection db, int? userId, string action, string detail, HttpContext ctx, bool ok = true)
{
    await db.ExecuteAsync(
        "INSERT INTO ActivityLog(UserId, Action, Detail, Ip, Ua, Ok) VALUES(@u,@a,@d,@ip,@ua,@ok)",
        new { u = userId, a = action, d = detail, ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "", ua = (ctx.Request.Headers.UserAgent.ToString() ?? "").Left(200), ok });
}
async Task Audit(SqlConnection db, int userId, string tbl, string recId, object? oldVal, object? newVal)
{
    await db.ExecuteAsync(
        "INSERT INTO AuditLog(UserId, Tbl, RecId, OldVal, NewVal) VALUES(@u,@t,@r,@o,@n)",
        new { u = userId, t = tbl, r = recId,
              o = oldVal == null ? null : System.Text.Json.JsonSerializer.Serialize(oldVal),
              n = newVal == null ? null : System.Text.Json.JsonSerializer.Serialize(newVal) });
}
async Task<string> Get(SqlConnection db, string key) =>
    await db.ExecuteScalarAsync<string?>("SELECT [Value] FROM Settings WHERE [Key]=@k", new { k = key }) ?? "";
async Task Put(SqlConnection db, string key, string val) =>
    await db.ExecuteAsync(@"MERGE Settings AS t USING (SELECT @k K, @v V) AS s ON t.[Key]=s.K
        WHEN MATCHED THEN UPDATE SET [Value]=s.V WHEN NOT MATCHED THEN INSERT([Key],[Value]) VALUES(s.K,s.V);",
        new { k = key, v = val });

// A small "current user" record passed to each handler.
async Task<Me?> CurrentUser(HttpRequest req, SqlConnection db)
{
    var id = VerifyToken(req);
    if (id == null) return null;
    var u = await db.QuerySingleOrDefaultAsync(
        "SELECT Id, Username, FullName, Role, Theme, Lang, TwoFactorOn FROM Users WHERE Id=@id AND Active=1", new { id });
    if (u == null) return null;
    List<int> branches;
    if ((string)u.Role == "superadmin")
        branches = (await db.QueryAsync<int>("SELECT Id FROM Branches")).ToList();
    else
        branches = (await db.QueryAsync<int>("SELECT BranchId FROM UserBranches WHERE UserId=@id", new { id })).ToList();
    return new Me(u.Id, u.Username, u.FullName, u.Role, u.Theme, u.Lang, u.TwoFactorOn, branches);
}

IResult Bad(string msg, int code = 400) => Results.Json(new { error = msg }, statusCode: code);

// =====================================================================
// AUTH
// =====================================================================
app.MapPost("/api/login", async (HttpContext ctx, LoginBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var name = (b.Username ?? "").ToLower().Trim();
    if (name.Length > 0) name = name[..Math.Min(40, name.Length)];
    var u = await db.QuerySingleOrDefaultAsync(
        "SELECT * FROM Users WHERE Username=@name AND Active=1", new { name });
    bool ok = u != null && BCrypt.Net.BCrypt.Verify(b.Password ?? "", (string)u.PasswordHash);
    if (ok && (bool)u!.TwoFactorOn)
    {
        bool codeOk = TotpHelper.Verify((string)u.TotpSecret, b.Code ?? "");
        await Log(db, (int)u.Id, "login", codeOk ? "success" : "failed-2fa:" + name, ctx, codeOk);
        if (!codeOk) return Results.Json(new { error = "Enter the 6-digit code from your authenticator app", need2fa = true }, statusCode: 401);
        return Results.Json(new { token = MakeToken((int)u.Id), role = (string)u.Role, name = (string)u.FullName });
    }
    await Log(db, u == null ? null : (int)u.Id, "login", ok ? "success" : "failed:" + name, ctx, ok);
    if (!ok) return Bad("Wrong username or password", 401);
    return Results.Json(new { token = MakeToken((int)u!.Id), role = (string)u.Role, name = (string)u.FullName });
}).RequireRateLimiting("login");

app.MapGet("/api/me", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var me = await CurrentUser(req, db);
    if (me == null) return Bad("Sign in required", 401);
    return Results.Json(new
    {
        id = me.Id, name = me.FullName, role = me.Role, branches = me.Branches,
        theme = me.Theme, lang = me.Lang, two_factor_on = me.TwoFactorOn,
        company = await Get(db, "company_name"), license_end = await Get(db, "license_end")
    });
});

app.MapPatch("/api/me", async (HttpRequest req, MeBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var me = await CurrentUser(req, db);
    if (me == null) return Bad("Sign in required", 401);
    if (b.Theme is "light" or "dark" or "system")
        await db.ExecuteAsync("UPDATE Users SET Theme=@v WHERE Id=@id", new { v = b.Theme, id = me.Id });
    if (b.Lang is "en" or "ar")
        await db.ExecuteAsync("UPDATE Users SET Lang=@v WHERE Id=@id", new { v = b.Lang, id = me.Id });
    if (!string.IsNullOrEmpty(b.Password))
    {
        var hash = await db.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id = me.Id });
        if (b.Password.Length < 10 || !BCrypt.Net.BCrypt.Verify(b.Old ?? "", hash))
            return Bad("Old password wrong or new password shorter than 10 characters");
        var nh = BCrypt.Net.BCrypt.HashPassword(b.Password, workFactor: 12);
        await db.ExecuteAsync("UPDATE Users SET PasswordHash=@h WHERE Id=@id", new { h = nh, id = me.Id });
    }
    return Results.Json(new { ok = true });
});

// =====================================================================
// TWO-FACTOR LOGIN (TOTP — works with Google Authenticator, Microsoft
// Authenticator, Authy, etc. No paid service or outside account needed.)
// =====================================================================
app.MapPost("/api/me/2fa/setup", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var me = await CurrentUser(req, db);
    if (me == null) return Bad("Sign in required", 401);
    var secret = TotpHelper.GenerateSecret();
    await db.ExecuteAsync("UPDATE Users SET TotpSecret=@s WHERE Id=@id", new { s = secret, id = me.Id });
    var uri = $"otpauth://totp/MountainApp:{Uri.EscapeDataString(me.Username)}?secret={secret}&issuer=MountainApp&digits=6&period=30";
    return Results.Json(new { secret, otpauth_uri = uri });
});
app.MapPost("/api/me/2fa/enable", async (HttpRequest req, TotpCodeBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var me = await CurrentUser(req, db);
    if (me == null) return Bad("Sign in required", 401);
    var secret = await db.ExecuteScalarAsync<string?>("SELECT TotpSecret FROM Users WHERE Id=@id", new { id = me.Id });
    if (string.IsNullOrEmpty(secret) || !TotpHelper.Verify(secret, b.Code ?? "")) return Bad("Wrong or expired code — try the newest code shown in your app");
    await db.ExecuteAsync("UPDATE Users SET TwoFactorOn=1 WHERE Id=@id", new { id = me.Id });
    await Log(db, me.Id, "2fa-enabled", "", req.HttpContext);
    return Results.Json(new { ok = true });
});
app.MapPost("/api/me/2fa/disable", async (HttpRequest req, PasswordCheckBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var me = await CurrentUser(req, db);
    if (me == null) return Bad("Sign in required", 401);
    var hash = await db.ExecuteScalarAsync<string>("SELECT PasswordHash FROM Users WHERE Id=@id", new { id = me.Id });
    if (!BCrypt.Net.BCrypt.Verify(b.Password ?? "", hash)) return Bad("Wrong password", 401);
    await db.ExecuteAsync("UPDATE Users SET TwoFactorOn=0, TotpSecret=NULL WHERE Id=@id", new { id = me.Id });
    await Log(db, me.Id, "2fa-disabled", "", req.HttpContext);
    return Results.Json(new { ok = true });
});

// Common guard used by every protected endpoint below.
async Task<(Me? me, IResult? err)> Guard(HttpRequest req, SqlConnection db, params string[] roles)
{
    var me = await CurrentUser(req, db);
    if (me == null) return (null, Bad("Sign in required", 401));
    if (roles.Length > 0 && !roles.Contains(me.Role)) return (null, Bad("Not allowed", 403));
    if (req.Method != "GET" && me.Role != "superadmin")
    {
        var lic = await Get(db, "license_end");
        if (string.Compare(lic, Today(), StringComparison.Ordinal) < 0)
            return (null, Bad("Application license expired - read only. Contact the Super Admin.", 402));
    }
    return (me, null);
}

// =====================================================================
// USERS (Super Admin only)
// =====================================================================
app.MapGet("/api/users", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    var rows = (await db.QueryAsync("SELECT Id, Username, FullName AS Name, Role, Active, Created FROM Users")).ToList();
    var result = new List<object>();
    foreach (var r in rows)
    {
        var br = await db.QueryAsync<int>("SELECT BranchId FROM UserBranches WHERE UserId=@id", new { id = (int)r.Id });
        result.Add(new { r.Id, r.Username, r.Name, r.Role, active = (bool)r.Active, r.Created, branches = br });
    }
    return Results.Json(result);
});

app.MapPost("/api/users", async (HttpRequest req, NewUserBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    if (b.Username == null || !System.Text.RegularExpressions.Regex.IsMatch(b.Username, "^[a-z0-9._-]{3,30}$") ||
        string.IsNullOrWhiteSpace(b.Name) || (b.Password?.Length ?? 0) < 10 || (b.Role != "admin" && b.Role != "user"))
        return Bad("Invalid user (username 3-30 chars, password min 10, role admin or user)");
    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Users WHERE Username=@u", new { u = b.Username }) > 0)
        return Bad("Username already exists", 409);
    var hash = BCrypt.Net.BCrypt.HashPassword(b.Password, workFactor: 12);
    var id = await db.ExecuteScalarAsync<int>(
        "INSERT INTO Users(Username, FullName, PasswordHash, Role) OUTPUT INSERTED.Id VALUES(@u,@n,@h,@r)",
        new { u = b.Username, n = b.Name, h = hash, r = b.Role });
    foreach (var brId in b.Branches ?? new List<int>())
        await db.ExecuteAsync("INSERT INTO UserBranches VALUES(@id,@b)", new { id, b = brId });
    await Audit(db, me!.Id, "Users", id.ToString(), null, new { b.Username, b.Role, b.Branches });
    return Results.Json(new { id });
});

app.MapPatch("/api/users/{id:int}", async (HttpRequest req, int id, UserPatchBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    var t = await db.QuerySingleOrDefaultAsync("SELECT * FROM Users WHERE Id=@id", new { id });
    if (t == null) return Bad("Not found", 404);
    if ((string)t.Role == "superadmin" && (b.Active != null || b.Role != null))
        return Bad("The Super Admin account cannot be disabled or changed");
    if (b.Active != null) await db.ExecuteAsync("UPDATE Users SET Active=@v WHERE Id=@id", new { v = b.Active, id });
    if (b.Role is "admin" or "user") await db.ExecuteAsync("UPDATE Users SET Role=@v WHERE Id=@id", new { v = b.Role, id });
    if (!string.IsNullOrWhiteSpace(b.Name)) await db.ExecuteAsync("UPDATE Users SET FullName=@v WHERE Id=@id", new { v = b.Name.Left(80), id });
    if (!string.IsNullOrEmpty(b.Password))
    {
        if (b.Password.Length < 10) return Bad("Password min 10 characters");
        var h = BCrypt.Net.BCrypt.HashPassword(b.Password, workFactor: 12);
        await db.ExecuteAsync("UPDATE Users SET PasswordHash=@h WHERE Id=@id", new { h, id });
    }
    if (b.Branches != null)
    {
        await db.ExecuteAsync("DELETE FROM UserBranches WHERE UserId=@id", new { id });
        foreach (var brId in b.Branches) await db.ExecuteAsync("INSERT INTO UserBranches VALUES(@id,@b)", new { id, b = brId });
    }
    await Audit(db, me!.Id, "Users", id.ToString(), new { t.Active, t.Role }, b);
    return Results.Json(new { ok = true });
});

// =====================================================================
// BRANCHES & SETTINGS
// =====================================================================
app.MapGet("/api/branches", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var rows = await db.QueryAsync<BranchRow>($"SELECT Id, Name, CR AS Cr FROM Branches WHERE Id IN ({ids})");
    return Results.Json(rows);
});

app.MapPatch("/api/branches/{id:int}", async (HttpRequest req, int id, BranchPatchBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    var o = await db.QuerySingleOrDefaultAsync("SELECT * FROM Branches WHERE Id=@id", new { id });
    if (o == null) return Bad("Not found", 404);
    var name = (b.Name ?? (string)o.Name).Left(60);
    var cr = (b.Cr ?? (string)o.CR).Left(40);
    await db.ExecuteAsync("UPDATE Branches SET Name=@n, CR=@c WHERE Id=@id", new { n = name, c = cr, id });
    await Audit(db, me!.Id, "Branches", id.ToString(), o, b);
    return Results.Json(new { ok = true });
});

app.MapGet("/api/settings", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    return Results.Json(new
    {
        company_name = await Get(db, "company_name"),
        license_end = await Get(db, "license_end"),
        closed_through = await Get(db, "closed_through")
    });
});

app.MapPatch("/api/settings", async (HttpRequest req, SettingsBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    var old = new { company_name = await Get(db, "company_name"), license_end = await Get(db, "license_end") };
    if (!string.IsNullOrWhiteSpace(b.CompanyName)) await Put(db, "company_name", b.CompanyName.Left(120));
    if (DateOk(b.LicenseEnd)) await Put(db, "license_end", b.LicenseEnd!);
    await Audit(db, me!.Id, "Settings", "app", old, b);
    return Results.Json(new { ok = true });
});

app.MapPost("/api/close", async (HttpRequest req, CloseBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var m = b.Month ?? "";
    var closed = await Get(db, "closed_through");
    if (!System.Text.RegularExpressions.Regex.IsMatch(m, @"^\d{4}-\d{2}$") ||
        string.Compare(m, Today()[..7], StringComparison.Ordinal) >= 0 ||
        string.Compare(m, closed, StringComparison.Ordinal) <= 0)
        return Bad("Month must be a past month after the last closed month");
    await Put(db, "closed_through", m);
    await Audit(db, me!.Id, "Settings", "closed_through", new { v = closed }, new { v = m });
    return Results.Json(new { ok = true });
});

// =====================================================================
// LEDGER (Transactions)
// =====================================================================
app.MapPost("/api/tx", async (HttpRequest req, TxBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (!me!.Branches.Contains(b.BranchId) || b.Type is not ("income" or "expense" or "transfer") ||
        !DateOk(b.Date) || !(b.Amount > 0) || string.IsNullOrWhiteSpace(b.Category))
        return Bad("Invalid entry: branch, type, date, category and a positive amount are required");
    var closed = await Get(db, "closed_through");
    if (string.Compare(b.Date![..7], closed, StringComparison.Ordinal) <= 0)
        return Bad("This month is closed", 423);
    var status = me.Role == "user" ? "pending" : "approved";
    var id = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Transactions(Date, BranchId, Type, Category, Description, Amount, PaidBy, WorkerQID, ExtCode, Status, CreatedBy, ApprovedBy)
        OUTPUT INSERTED.Id
        VALUES(@date,@bid,@type,@cat,@desc,@amt,@by,@qid,@ext,@st,@uid,@app)",
        new
        {
            date = b.Date, bid = b.BranchId, type = b.Type, cat = b.Category.Left(60), desc = (b.Description ?? "").Left(300),
            amt = R2(b.Amount), by = (b.PaidBy ?? "").Left(60), qid = (b.WorkerQid ?? "").Left(20), ext = (b.ExtCode ?? "").Left(20),
            st = status, uid = me.Id, app = status == "approved" ? me.Id : (int?)null
        });
    await db.ExecuteAsync("UPDATE Transactions SET ReceiptNo = 'R' + RIGHT('000000' + CAST(Id AS NVARCHAR(6)), 6) WHERE Id=@id", new { id });
    await Audit(db, me.Id, "Transactions", id.ToString(), null, b);
    return Results.Json(new { id, status });
});

app.MapGet("/api/tx", async (HttpRequest req, string? branch_id, string? from, string? to, string? status) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var where = new List<string> { $"BranchId IN ({ids})" };
    var p = new DynamicParameters();
    if (!string.IsNullOrEmpty(branch_id))
    {
        if (!me.Branches.Contains(int.Parse(branch_id))) return Bad("Not allowed", 403);
        where.Add("BranchId=@bid"); p.Add("bid", int.Parse(branch_id));
    }
    if (DateOk(from)) { where.Add("Date>=@f"); p.Add("f", from); }
    if (DateOk(to)) { where.Add("Date<=@t"); p.Add("t", to); }
    if (status is "pending" or "approved" or "cancelled") { where.Add("Status=@s"); p.Add("s", status); }
    var sql = $@"SELECT TOP 500 Id, ReceiptNo, CONVERT(varchar(10), Date, 23) AS Date, BranchId, Type, Category, Description, Amount, PaidBy,
                        WorkerQID AS WorkerQid, ExtCode, Status, CancelReason, CreatedBy, ApprovedBy, Created
                 FROM Transactions WHERE {string.Join(" AND ", where)} ORDER BY Date DESC, Id DESC";
    var rows = await db.QueryAsync<TxRow>(sql, p);
    return Results.Json(rows);
});

async Task<IResult> Decide(HttpRequest req, int id, string newStatus, string? reason)
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err!;
    var x = await db.QuerySingleOrDefaultAsync("SELECT * FROM Transactions WHERE Id=@id", new { id });
    if (x == null || !me!.Branches.Contains((int)x.BranchId)) return Bad("Not found", 404);
    var closed = await Get(db, "closed_through");
    if (string.Compare(((DateTime)x.Date).ToString("yyyy-MM"), closed, StringComparison.Ordinal) <= 0) return Bad("This month is closed", 423);
    if (newStatus == "approved" && (string)x.Status != "pending") return Bad("Only pending entries can be approved");
    if (newStatus == "cancelled" && ((string)x.Status == "cancelled" || string.IsNullOrWhiteSpace(reason)))
        return Bad("A reason is required and the entry must not be cancelled already");
    await db.ExecuteAsync("UPDATE Transactions SET Status=@s, CancelReason=@r, ApprovedBy=@u WHERE Id=@id",
        new { s = newStatus, r = newStatus == "cancelled" ? reason : "", u = me!.Id, id });
    await Audit(db, me.Id, "Transactions", id.ToString(), new { status = (string)x.Status }, new { status = newStatus, reason });
    return Results.Json(new { ok = true });
}
app.MapPost("/api/tx/{id:int}/approve", (HttpRequest req, int id) => Decide(req, id, "approved", null));
app.MapPost("/api/tx/{id:int}/cancel", (HttpRequest req, int id, CancelBody b) => Decide(req, id, "cancelled", b.Reason));

app.MapGet("/api/summary", async (HttpRequest req, string? from, string? to) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var f = DateOk(from) ? from! : "0000-01-01";
    var t = DateOk(to) ? to! : "9999-12-31";
    async Task<decimal> G(int id, string type, string a, string b) =>
        await db.ExecuteScalarAsync<decimal?>(
            "SELECT SUM(Amount) FROM Transactions WHERE BranchId=@id AND Type=@type AND Status='approved' AND Date BETWEEN @a AND @b",
            new { id, type, a, b }) ?? 0m;
    var branchRows = new List<object>();
    decimal aIn = 0, aOut = 0, aCash = 0;
    foreach (var id in me!.Branches)
    {
        var name = await db.ExecuteScalarAsync<string>("SELECT Name FROM Branches WHERE Id=@id", new { id });
        var inc = await G(id, "income", f, t); var exp = await G(id, "expense", f, t);
        var cashIn = await G(id, "income", "0000-01-01", "9999-12-31") - await G(id, "expense", "0000-01-01", "9999-12-31");
        branchRows.Add(new { branch_id = id, name, income = R2(inc), expense = R2(exp), profit = R2(inc - exp), cash_in_hand = R2(cashIn) });
        aIn += inc; aOut += exp; aCash += cashIn;
    }
    return Results.Json(new
    {
        branches = branchRows,
        all_branches = new { income = R2(aIn), expense = R2(aOut), profit = R2(aIn - aOut), cash_in_hand = R2(aCash) }
    });
});

// =====================================================================
// WORKERS (Manpower)
// =====================================================================
app.MapGet("/api/workers", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var rows = (await db.QueryAsync(
        $@"SELECT w.*, b.Name AS Branch FROM Workers w JOIN Branches b ON b.Id=w.BranchId
           WHERE w.BranchId IN ({ids}) ORDER BY w.RPExpiry")).ToList();
    var today = DateTime.UtcNow.Date;
    var result = new List<Dictionary<string, object?>>();
    foreach (var w in rows)
    {
        int? days = w.RPExpiry == null ? null : (int)((DateTime)w.RPExpiry - today).TotalDays;
        var status = days == null ? "unknown" : days < 0 ? "expired" : days <= 30 ? "near_expiry" : "valid";
        var d = new Dictionary<string, object?>
        {
            ["qid"] = w.QID, ["name"] = w.FullName, ["nationality"] = w.Nationality, ["branch_id"] = w.BranchId,
            ["branch"] = w.Branch, ["rp_expiry"] = w.RPExpiry == null ? null : ((DateTime)w.RPExpiry).ToString("yyyy-MM-dd"), ["mobile"] = w.Mobile,
            ["status"] = w.EmploymentStatus, ["days_left"] = days, ["rp_status"] = status
        };
        if (me.Role != "user")
        {
            decimal fee = w.Fee; string qid = w.QID;
            decimal paid = await db.ExecuteScalarAsync<decimal?>("SELECT SUM(Amount) FROM WorkerPayments WHERE QID=@q", new { q = qid }) ?? 0m;
            d["fee"] = fee; d["passport_no"] = w.PassportNo; d["paid"] = R2(paid); d["balance"] = R2(fee - paid);
        }
        result.Add(d);
    }
    return Results.Json(result);
});

app.MapPost("/api/workers", async (HttpRequest req, WorkerBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (b.Qid == null || !System.Text.RegularExpressions.Regex.IsMatch(b.Qid, @"^\d{11}$") ||
        string.IsNullOrWhiteSpace(b.Name) || !me!.Branches.Contains(b.BranchId) ||
        (b.RpExpiry != null && !DateOk(b.RpExpiry)))
        return Bad("Invalid worker: 11-digit QID, name, branch and valid dates are required");
    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Workers WHERE QID=@q", new { q = b.Qid }) > 0)
        return Bad("This QID already exists", 409);
    await db.ExecuteAsync(@"
        INSERT INTO Workers(QID, FullName, Nationality, BranchId, RPExpiry, PassportNo, PassportExpiry, Mobile, Fee)
        VALUES(@q,@n,@nat,@bid,@rp,@pn,@pe,@mob,@fee)",
        new
        {
            q = b.Qid, n = b.Name.Left(80), nat = (b.Nationality ?? "").Left(40), bid = b.BranchId,
            rp = b.RpExpiry, pn = (b.PassportNo ?? "").Left(20), pe = DateOk(b.PassportExpiry) ? b.PassportExpiry : null,
            mob = (b.Mobile ?? "").Left(20), fee = Math.Max(0, b.Fee ?? 0)
        });
    await Audit(db, me!.Id, "Workers", b.Qid, null, new { b.Name, b.BranchId });
    return Results.Json(new { ok = true });
});

// =====================================================================
// WORKER PAYMENTS (yearly fee, installments, balance)
// =====================================================================
app.MapGet("/api/workers/{qid}/payments", async (HttpRequest req, string qid) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var w = await db.QuerySingleOrDefaultAsync("SELECT BranchId FROM Workers WHERE QID=@q", new { q = qid });
    if (w == null || !me!.Branches.Contains((int)w.BranchId)) return Bad("Not found", 404);
    var rows = await db.QueryAsync<PaymentRow>(
        "SELECT Id, QID, CONVERT(varchar(10), PaymentDate, 23) AS PaymentDate, Amount, Method, ReceivedBy, ReferenceNo, Remarks, Created " +
        "FROM WorkerPayments WHERE QID=@q ORDER BY PaymentDate DESC, Id DESC", new { q = qid });
    return Results.Json(rows);
});

app.MapPost("/api/workers/{qid}/payments", async (HttpRequest req, string qid, PaymentBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var w = await db.QuerySingleOrDefaultAsync("SELECT BranchId FROM Workers WHERE QID=@q", new { q = qid });
    if (w == null || !me!.Branches.Contains((int)w.BranchId)) return Bad("Not found", 404);
    if (!DateOk(b.Date) || !(b.Amount > 0)) return Bad("A valid date and a positive amount are required");
    var id = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO WorkerPayments(QID, PaymentDate, Amount, Method, ReceivedBy, ReferenceNo, Remarks, CreatedBy)
        OUTPUT INSERTED.Id VALUES(@q,@d,@a,@m,@r,@ref,@rem,@u)",
        new { q = qid, d = b.Date, a = R2(b.Amount), m = (b.Method ?? "Cash").Left(20), r = (b.ReceivedBy ?? "").Left(60), @ref = (b.ReferenceNo ?? "").Left(30), rem = (b.Remarks ?? "").Left(200), u = me.Id });
    await Audit(db, me.Id, "WorkerPayments", id.ToString(), null, new { qid, b.Amount, b.Date });
    return Results.Json(new { id });
});

// =====================================================================
// EXTERNAL CONTACTS (outside people/employees) with their money in/out
// =====================================================================
app.MapGet("/api/contacts", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var rows = await db.QueryAsync<ContactRow>($@"
        SELECT c.Code, c.Name, c.ContactType, c.Company, c.Position, c.IdNo, c.Mobile, c.Email, c.BranchId, c.Status, c.Remarks,
               ISNULL((SELECT SUM(Amount) FROM Transactions WHERE ExtCode=c.Code AND Type='income' AND Status='approved'),0) AS MoneyIn,
               ISNULL((SELECT SUM(Amount) FROM Transactions WHERE ExtCode=c.Code AND Type='expense' AND Status='approved'),0) AS MoneyOut
        FROM ExternalContacts c WHERE c.BranchId IS NULL OR c.BranchId IN ({ids}) ORDER BY c.Name");
    return Results.Json(rows);
});

app.MapPost("/api/contacts", async (HttpRequest req, ContactBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (string.IsNullOrWhiteSpace(b.Code) || string.IsNullOrWhiteSpace(b.Name)) return Bad("A code and a name are required");
    if (b.BranchId != null && !me!.Branches.Contains(b.BranchId.Value)) return Bad("Not allowed", 403);
    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ExternalContacts WHERE Code=@c", new { c = b.Code }) > 0)
        return Bad("This contact code already exists", 409);
    await db.ExecuteAsync(@"
        INSERT INTO ExternalContacts(Code, Name, ContactType, Company, Position, IdNo, Mobile, Email, BranchId, Remarks, CreatedBy)
        VALUES(@c,@n,@t,@co,@p,@id,@m,@e,@b,@r,@u)",
        new { c = b.Code.Left(20), n = b.Name.Left(80), t = (b.ContactType ?? "External Employee").Left(30), co = (b.Company ?? "").Left(80),
              p = (b.Position ?? "").Left(60), id = (b.IdNo ?? "").Left(30), m = (b.Mobile ?? "").Left(20), e = (b.Email ?? "").Left(80),
              b = b.BranchId, r = (b.Remarks ?? "").Left(200), u = me!.Id });
    await Audit(db, me!.Id, "ExternalContacts", b.Code, null, b);
    return Results.Json(new { ok = true });
});

// =====================================================================
// ATTACHMENTS (files kept against any record, in every section)
// =====================================================================
var AttachDir = Path.Combine(app.Environment.ContentRootPath, "Data", "attachments");
Directory.CreateDirectory(AttachDir);
string[] AllowedExt = { ".pdf", ".jpg", ".jpeg", ".png", ".docx", ".xlsx" };
const long MaxFileBytes = 15 * 1024 * 1024;

app.MapGet("/api/attachments", async (HttpRequest req, string section, string record_id) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var rows = await db.QueryAsync<AttachmentRow>(
        "SELECT Id, Section, RecordId, FileName, ContentType, SizeBytes, Note, UploadedBy, Uploaded FROM Attachments " +
        "WHERE Section=@s AND RecordId=@r AND Deleted=0 ORDER BY Uploaded DESC", new { s = section, r = record_id });
    return Results.Json(rows);
});

app.MapPost("/api/attachments", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (!req.HasFormContentType) return Bad("Send the file as multipart form data");
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var section = form["section"].ToString();
    var recordId = form["record_id"].ToString();
    var note = form["note"].ToString();
    if (file == null || file.Length == 0) return Bad("Choose a file");
    if (file.Length > MaxFileBytes) return Bad("File is larger than 15 MB");
    if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(recordId)) return Bad("Section and record are required");
    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!AllowedExt.Contains(ext)) return Bad("Allowed file types: PDF, JPG, PNG, DOCX, XLSX");
    var stored = Guid.NewGuid().ToString("N") + ext;
    await using (var fs = File.Create(Path.Combine(AttachDir, stored)))
        await file.CopyToAsync(fs);
    var id = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Attachments(Section, RecordId, FileName, StoredName, ContentType, SizeBytes, Note, UploadedBy)
        OUTPUT INSERTED.Id VALUES(@sec,@rid,@fn,@sn,@ct,@sz,@note,@u)",
        new { sec = section.Left(30), rid = recordId.Left(40), fn = Path.GetFileName(file.FileName).Left(200), sn = stored,
              ct = (file.ContentType ?? "application/octet-stream").Left(100), sz = file.Length, note = note.Left(200), u = me!.Id });
    await Log(db, me!.Id, "upload", $"{section}/{recordId}/{file.FileName}", req.HttpContext);
    return Results.Json(new { id });
});

app.MapGet("/api/attachments/{id:int}/file", async (HttpRequest req, int id) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var a = await db.QuerySingleOrDefaultAsync("SELECT * FROM Attachments WHERE Id=@id AND Deleted=0", new { id });
    if (a == null) return Bad("Not found", 404);
    var path = Path.Combine(AttachDir, (string)a.StoredName);
    if (!File.Exists(path)) return Bad("File missing on disk", 404);
    await Log(db, me!.Id, "download", $"attachment {id}", req.HttpContext);
    return Results.File(path, (string)a.ContentType, (string)a.FileName);
});

app.MapDelete("/api/attachments/{id:int}", async (HttpRequest req, int id) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var a = await db.QuerySingleOrDefaultAsync("SELECT * FROM Attachments WHERE Id=@id", new { id });
    if (a == null) return Bad("Not found", 404);
    await db.ExecuteAsync("UPDATE Attachments SET Deleted=1 WHERE Id=@id", new { id });
    await Audit(db, me!.Id, "Attachments", id.ToString(), a, new { deleted = true });
    return Results.Json(new { ok = true });
});

// =====================================================================
// EXCEL IMPORT / EXPORT — real .xlsx files (ClosedXML), no external service
// =====================================================================
IResult XlsxFile(XLWorkbook wb, string filename)
{
    using var ms = new MemoryStream();
    wb.SaveAs(ms);
    return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
}
void StyleHeader(IXLWorksheet ws, string[] headers)
{
    for (int i = 0; i < headers.Length; i++)
    {
        var c = ws.Cell(1, i + 1);
        c.Value = headers[i];
        c.Style.Font.Bold = true;
        c.Style.Font.FontColor = XLColor.White;
        c.Style.Fill.BackgroundColor = XLColor.FromHtml("#0B3C8C");
    }
    ws.SheetView.FreezeRows(1);
    ws.Columns().AdjustToContents();
}

// ---- Export: a blank, ready-to-fill template with the branch and category lists ----
app.MapGet("/api/export/template", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var branches = (await db.QueryAsync<string>("SELECT Name FROM Branches ORDER BY Name")).ToList();
    using var wb = new XLWorkbook();

    var wl = wb.Worksheets.Add("Lists");
    wl.Cell(1, 1).Value = "Branch"; wl.Cell(1, 2).Value = "Category"; wl.Cell(1, 3).Value = "Type";
    for (int i = 0; i < branches.Count; i++) wl.Cell(i + 2, 1).Value = branches[i];
    var cats = new[] { "ID Renew", "Fine", "NOC", "Food Certificate", "Commercial Permit", "Salary", "Rent", "Mobile Recharge", "Water", "Petrol", "Other" };
    for (int i = 0; i < cats.Length; i++) wl.Cell(i + 2, 2).Value = cats[i];
    wl.Cell(2, 3).Value = "income"; wl.Cell(3, 3).Value = "expense";

    var wLedger = wb.Worksheets.Add("Ledger");
    StyleHeader(wLedger, new[] { "Date (YYYY-MM-DD)", "Branch", "Type", "Category", "Description", "Amount", "Paid By", "Worker QID" });
    wLedger.Cell(2, 1).Value = "2026-01-01"; wLedger.Cell(2, 2).Value = branches.FirstOrDefault() ?? "Branch 24";
    wLedger.Cell(2, 3).Value = "income"; wLedger.Cell(2, 4).Value = "ID Renew"; wLedger.Cell(2, 5).Value = "Sample row - delete me"; wLedger.Cell(2, 6).Value = 1500;
	wLedger.Range("B3:B500").CreateDataValidation().List(wl.Range("A2:A" + (branches.Count + 1)));
	wLedger.Range("C3:C500").CreateDataValidation().List(wl.Range("C2:C3"));
	wLedger.Range("D3:D500").CreateDataValidation().List(wl.Range("B2:B" + (cats.Length + 1)));	

    var wWorkers = wb.Worksheets.Add("Workers");
    StyleHeader(wWorkers, new[] { "QID (11 digits)", "Name", "Nationality", "Branch", "RP Expiry (YYYY-MM-DD)", "Passport No", "Mobile", "Yearly Fee" });
    wWorkers.Cell(2, 1).Value = "28000000000"; wWorkers.Cell(2, 2).Value = "Sample Worker - delete me"; wWorkers.Cell(2, 4).Value = branches.FirstOrDefault() ?? "Branch 24";
    wWorkers.Range("D3:D500").CreateDataValidation().List(wl.Range("A2:A" + (branches.Count + 1)));

    return XlsxFile(wb, "Mountain_Import_Template.xlsx");
});

// ---- Export: current data, in the same layout as the template ----
app.MapGet("/api/export/ledger", async (HttpRequest req, string? branch_id, string? from, string? to) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var where = new List<string> { $"t.BranchId IN ({ids})" }; var p = new DynamicParameters();
    if (!string.IsNullOrEmpty(branch_id) && me.Branches.Contains(int.Parse(branch_id))) { where.Add("t.BranchId=@bid"); p.Add("bid", int.Parse(branch_id)); }
    if (DateOk(from)) { where.Add("t.Date>=@f"); p.Add("f", from); }
    if (DateOk(to)) { where.Add("t.Date<=@t"); p.Add("t", to); }
    var rows = await db.QueryAsync($@"
        SELECT t.ReceiptNo, CONVERT(varchar(10),t.Date,23) AS Date, b.Name AS Branch, t.Type, t.Category, t.Description, t.Amount, t.PaidBy, t.WorkerQID, t.Status
        FROM Transactions t JOIN Branches b ON b.Id=t.BranchId WHERE {string.Join(" AND ", where)} ORDER BY t.Date, t.Id", p);
    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("Ledger");
    StyleHeader(ws, new[] { "Receipt No", "Date", "Branch", "Type", "Category", "Description", "Amount", "Paid By", "Worker QID", "Status" });
    int r = 2;
    foreach (var x in rows)
    {
        ws.Cell(r, 1).Value = (string)x.ReceiptNo; ws.Cell(r, 2).Value = (string)x.Date; ws.Cell(r, 3).Value = (string)x.Branch;
        ws.Cell(r, 4).Value = (string)x.Type; ws.Cell(r, 5).Value = (string)x.Category; ws.Cell(r, 6).Value = (string)x.Description;
        ws.Cell(r, 7).Value = (decimal)x.Amount; ws.Cell(r, 8).Value = (string)x.PaidBy; ws.Cell(r, 9).Value = (string)x.WorkerQID; ws.Cell(r, 10).Value = (string)x.Status;
        r++;
    }
    ws.Columns().AdjustToContents();
    return XlsxFile(wb, "ledger.xlsx");
});

app.MapGet("/api/export/workers", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var rows = await db.QueryAsync($@"
        SELECT w.QID, w.FullName, w.Nationality, b.Name AS Branch, CONVERT(varchar(10),w.RPExpiry,23) AS RPExpiry, w.PassportNo, w.Mobile, w.Fee
        FROM Workers w JOIN Branches b ON b.Id=w.BranchId WHERE w.BranchId IN ({ids}) ORDER BY w.FullName");
    using var wb = new XLWorkbook();
    var ws = wb.Worksheets.Add("Workers");
    StyleHeader(ws, new[] { "QID", "Name", "Nationality", "Branch", "RP Expiry", "Passport No", "Mobile", "Yearly Fee" });
    int r = 2;
    foreach (var x in rows)
    {
        ws.Cell(r, 1).Value = (string)x.QID; ws.Cell(r, 2).Value = (string)x.FullName; ws.Cell(r, 3).Value = (string)x.Nationality;
        ws.Cell(r, 4).Value = (string)x.Branch; ws.Cell(r, 5).Value = (string?)x.RPExpiry ?? ""; ws.Cell(r, 6).Value = (string)x.PassportNo;
        ws.Cell(r, 7).Value = (string)x.Mobile; ws.Cell(r, 8).Value = (decimal)x.Fee;
        r++;
    }
    ws.Columns().AdjustToContents();
    return XlsxFile(wb, "workers.xlsx");
});

// ---- Import: read an .xlsx built from the template above ----
app.MapPost("/api/import/ledger", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    if (!req.HasFormContentType) return Bad("Send the file as multipart form data");
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Bad("Choose a file");
    var branchByName = (await db.QueryAsync("SELECT Id, Name FROM Branches")).ToDictionary(b => (string)b.Name, b => (int)b.Id, StringComparer.OrdinalIgnoreCase);
    var closed = await Get(db, "closed_through");
    var errors = new List<string>(); int accepted = 0, rejected = 0;
    using var stream = file.OpenReadStream();
    using var wb = new XLWorkbook(stream);
    var ws = wb.TryGetWorksheet("Ledger", out var wsLedger) ? wsLedger : wb.Worksheet(1);
    var last = ws.LastRowUsed()?.RowNumber() ?? 1;
    for (int r = 2; r <= last; r++)
    {
        var row = ws.Row(r);
        if (row.IsEmpty()) continue;
        string dateS = row.Cell(1).GetString().Trim();
        string branchS = row.Cell(2).GetString().Trim();
        string typeS = row.Cell(3).GetString().Trim().ToLowerInvariant();
        string cat = row.Cell(4).GetString().Trim();
        string desc = row.Cell(5).GetString().Trim();
        string amtS = row.Cell(6).GetString().Trim();
        string paidBy = row.Cell(7).GetString().Trim();
        string qid = row.Cell(8).GetString().Trim();
        if (dateS.StartsWith("Sample")) continue; // skip leftover template sample row text
        bool okAmt = decimal.TryParse(amtS, out var amt);
        if (!DateOk(dateS) || !branchByName.TryGetValue(branchS, out var bid) || !me!.Branches.Contains(bid) ||
            typeS is not ("income" or "expense" or "transfer") || string.IsNullOrWhiteSpace(cat) || !okAmt || amt <= 0)
        { errors.Add($"Row {r}: check date, branch, type, category and amount"); rejected++; continue; }
        if (dateS[..7].CompareTo(closed) <= 0) { errors.Add($"Row {r}: that month is closed"); rejected++; continue; }
        var status = me!.Role == "user" ? "pending" : "approved";
        var id = await db.ExecuteScalarAsync<int>(@"
            INSERT INTO Transactions(Date,BranchId,Type,Category,Description,Amount,PaidBy,WorkerQID,Status,CreatedBy,ApprovedBy)
            OUTPUT INSERTED.Id VALUES(@d,@bid,@ty,@cat,@desc,@amt,@pb,@qid,@st,@u,@app)",
            new { d = dateS, bid, ty = typeS, cat = cat.Left(60), desc = desc.Left(300), amt = R2(amt), pb = paidBy.Left(60), qid = qid.Left(20), st = status, u = me.Id, app = status == "approved" ? me.Id : (int?)null });
        await db.ExecuteAsync("UPDATE Transactions SET ReceiptNo='R'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id });
        accepted++;
    }
    await Log(db, me!.Id, "import-ledger", $"{accepted} accepted, {rejected} rejected", req.HttpContext);
    await Audit(db, me.Id, "Transactions", "bulk-import", null, new { accepted, rejected });
    return Results.Json(new { accepted, rejected, errors = errors.Take(50) });
});

app.MapPost("/api/import/workers", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    if (!req.HasFormContentType) return Bad("Send the file as multipart form data");
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Bad("Choose a file");
    var branchByName = (await db.QueryAsync("SELECT Id, Name FROM Branches")).ToDictionary(b => (string)b.Name, b => (int)b.Id, StringComparer.OrdinalIgnoreCase);
    var errors = new List<string>(); int accepted = 0, rejected = 0, skippedDup = 0;
    using var stream = file.OpenReadStream();
    using var wb = new XLWorkbook(stream);
    var ws = wb.TryGetWorksheet("Workers", out var wsWorkers) ? wsWorkers : wb.Worksheet(1);
    var last = ws.LastRowUsed()?.RowNumber() ?? 1;
    for (int r = 2; r <= last; r++)
    {
        var row = ws.Row(r);
        if (row.IsEmpty()) continue;
        string qid = row.Cell(1).GetString().Trim();
        string name = row.Cell(2).GetString().Trim();
        string nat = row.Cell(3).GetString().Trim();
        string branchS = row.Cell(4).GetString().Trim();
        string rpS = row.Cell(5).GetString().Trim();
        string passport = row.Cell(6).GetString().Trim();
        string mobile = row.Cell(7).GetString().Trim();
        decimal.TryParse(row.Cell(8).GetString().Trim(), out var fee);
        if (name.StartsWith("Sample")) continue;
        if (!System.Text.RegularExpressions.Regex.IsMatch(qid, @"^\d{11}$") || string.IsNullOrWhiteSpace(name) ||
            !branchByName.TryGetValue(branchS, out var bid) || !me!.Branches.Contains(bid))
        { errors.Add($"Row {r}: check QID (11 digits), name and branch"); rejected++; continue; }
        if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Workers WHERE QID=@q", new { q = qid }) > 0) { skippedDup++; continue; }
        await db.ExecuteAsync(@"
            INSERT INTO Workers(QID,FullName,Nationality,BranchId,RPExpiry,PassportNo,Mobile,Fee)
            VALUES(@q,@n,@nat,@bid,@rp,@pn,@mob,@fee)",
            new { q = qid, n = name.Left(80), nat = nat.Left(40), bid, rp = DateOk(rpS) ? rpS : null, pn = passport.Left(20), mob = mobile.Left(20), fee = Math.Max(0, fee) });
        accepted++;
    }
    await Log(db, me!.Id, "import-workers", $"{accepted} accepted, {rejected} rejected, {skippedDup} duplicates", req.HttpContext);
    await Audit(db, me.Id, "Workers", "bulk-import", null, new { accepted, rejected, skippedDup });
    return Results.Json(new { accepted, rejected, skipped_duplicates = skippedDup, errors = errors.Take(50) });
});

// =====================================================================
// ALERTS / NOTIFICATIONS — computed each call, nothing stored
// =====================================================================
app.MapGet("/api/alerts", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var list = new List<object>();
    var today = DateTime.UtcNow.Date;

    var expiring = await db.QueryAsync(
        $"SELECT QID, FullName, RPExpiry FROM Workers WHERE BranchId IN ({ids}) AND RPExpiry IS NOT NULL AND RPExpiry <= @d",
        new { d = today.AddDays(30) });
    foreach (var w in expiring)
    {
        int days = (int)((DateTime)w.RPExpiry - today).TotalDays;
        list.Add(new { priority = days <= 7 ? "urgent" : "important",
                        title = "RP expiry", detail = $"{w.FullName} ({w.QID})", days_left = days });
    }

    if (me.Role != "user")
    {
        var pending = await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM Transactions WHERE Status='pending' AND BranchId IN ({ids})");
        if (pending > 0) list.Add(new { priority = "important", title = "Pending approval", detail = $"{pending} entr{(pending == 1 ? "y" : "ies")} waiting", days_left = (int?)null });
    }

    var licEnd = await Get(db, "license_end");
    if (DateTime.TryParse(licEnd, out var lic))
    {
        int d = (int)(lic.Date - today).TotalDays;
        if (d <= 30) list.Add(new { priority = "urgent", title = "Application license", detail = licEnd, days_left = d });
    }
    return Results.Json(list);
});

// =====================================================================
// CUSTOMERS / SUPPLIERS
// =====================================================================
app.MapGet("/api/customers", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    return Results.Json(await db.QueryAsync<CustomerRow>(
        $"SELECT Id, Name, Company, Phone, Email, Address, BranchId FROM Customers WHERE BranchId IS NULL OR BranchId IN ({ids}) ORDER BY Name"));
});
app.MapPost("/api/customers", async (HttpRequest req, CustomerBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (string.IsNullOrWhiteSpace(b.Name)) return Bad("Name is required");
    if (b.BranchId != null && !me!.Branches.Contains(b.BranchId.Value)) return Bad("Not allowed", 403);
    var id = await db.ExecuteScalarAsync<int>(
        "INSERT INTO Customers(Name,Company,Phone,Email,Address,BranchId) OUTPUT INSERTED.Id VALUES(@n,@c,@p,@e,@a,@b)",
        new { n = b.Name.Left(80), c = (b.Company ?? "").Left(80), p = (b.Phone ?? "").Left(20), e = (b.Email ?? "").Left(80), a = (b.Address ?? "").Left(200), b = b.BranchId });
    await Audit(db, me!.Id, "Customers", id.ToString(), null, b);
    return Results.Json(new { id });
});
app.MapGet("/api/suppliers", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    return Results.Json(await db.QueryAsync<SupplierRow>(
        $"SELECT Id, Name, Company, Phone, Email, Address, BranchId FROM Suppliers WHERE BranchId IS NULL OR BranchId IN ({ids}) ORDER BY Name"));
});
app.MapPost("/api/suppliers", async (HttpRequest req, SupplierBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (string.IsNullOrWhiteSpace(b.Name)) return Bad("Name is required");
    if (b.BranchId != null && !me!.Branches.Contains(b.BranchId.Value)) return Bad("Not allowed", 403);
    var id = await db.ExecuteScalarAsync<int>(
        "INSERT INTO Suppliers(Name,Company,Phone,Email,Address,BranchId) OUTPUT INSERTED.Id VALUES(@n,@c,@p,@e,@a,@b)",
        new { n = b.Name.Left(80), c = (b.Company ?? "").Left(80), p = (b.Phone ?? "").Left(20), e = (b.Email ?? "").Left(80), a = (b.Address ?? "").Left(200), b = b.BranchId });
    await Audit(db, me!.Id, "Suppliers", id.ToString(), null, b);
    return Results.Json(new { id });
});

// =====================================================================
// PRODUCTS & STOCK
// =====================================================================
app.MapGet("/api/products", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    return Results.Json(await db.QueryAsync<ProductRow>(
        $"SELECT Id, Sku, Name, Category, Unit, CostPrice, SalePrice, StockQty, ReorderLevel, BranchId FROM Products WHERE BranchId IS NULL OR BranchId IN ({ids}) ORDER BY Name"));
});
app.MapPost("/api/products", async (HttpRequest req, ProductBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    if (string.IsNullOrWhiteSpace(b.Sku) || string.IsNullOrWhiteSpace(b.Name)) return Bad("SKU and name are required");
    if (b.BranchId != null && !me!.Branches.Contains(b.BranchId.Value)) return Bad("Not allowed", 403);
    if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Products WHERE Sku=@s", new { s = b.Sku }) > 0) return Bad("This SKU already exists", 409);
    var id = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Products(Sku,Name,Category,Unit,CostPrice,SalePrice,StockQty,ReorderLevel,BranchId) OUTPUT INSERTED.Id
        VALUES(@sku,@n,@cat,@u,@cp,@sp,@qty,@rl,@b)",
        new { sku = b.Sku.Left(40), n = b.Name.Left(100), cat = (b.Category ?? "").Left(60), u = (b.Unit ?? "pcs").Left(20),
              cp = Math.Max(0, b.CostPrice), sp = Math.Max(0, b.SalePrice), qty = Math.Max(0, b.StockQty), rl = Math.Max(0, b.ReorderLevel), b = b.BranchId });
    await Audit(db, me!.Id, "Products", id.ToString(), null, b);
    return Results.Json(new { id });
});
app.MapPost("/api/products/{id:int}/stock", async (HttpRequest req, int id, StockAdjustBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var p = await db.QuerySingleOrDefaultAsync("SELECT * FROM Products WHERE Id=@id", new { id });
    if (p == null) return Bad("Not found", 404);
    if (b.Qty == 0) return Bad("Enter a non-zero quantity (positive to add, negative to remove)");
    await db.ExecuteAsync("UPDATE Products SET StockQty = StockQty + @q WHERE Id=@id", new { q = b.Qty, id });
    await db.ExecuteAsync("INSERT INTO StockMovements(ProductId,MoveType,Qty,RefType,MoveDate,Note,CreatedBy) VALUES(@id,'adjust',@q,'manual',@d,@note,@u)",
        new { id, q = b.Qty, d = Today(), note = (b.Note ?? "").Left(200), u = me!.Id });
    await Audit(db, me!.Id, "Products", id.ToString(), new { stock = (decimal)p.StockQty }, new { change = b.Qty });
    return Results.Json(new { ok = true });
});

// =====================================================================
// ESTIMATES
// =====================================================================
app.MapGet("/api/estimates", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    return Results.Json(await db.QueryAsync<EstimateRow>($@"
        SELECT e.Id, e.EstimateNo, e.CustomerId, c.Name AS CustomerName, e.BranchId,
               CONVERT(varchar(10), e.EstDate, 23) AS EstDate, CONVERT(varchar(10), e.ExpiryDate, 23) AS ExpiryDate,
               e.Status, e.Total
        FROM Estimates e JOIN Customers c ON c.Id=e.CustomerId WHERE e.BranchId IN ({ids}) ORDER BY e.EstDate DESC, e.Id DESC"));
});
app.MapPost("/api/estimates", async (HttpRequest req, int branch_id, EstimateBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (!me!.Branches.Contains(branch_id)) return Bad("Not allowed", 403);
    if (b.Items == null || b.Items.Count == 0) return Bad("Add at least one item");
    var date = DateOk(b.Date) ? b.Date! : Today();
    var sub = b.Items.Sum(i => i.Qty * i.UnitPrice);
    var total = R2(sub - b.Discount + b.Tax);
    var id = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Estimates(CustomerId,BranchId,EstDate,ExpiryDate,Subtotal,Discount,Tax,Total,Notes,CreatedBy) OUTPUT INSERTED.Id
        VALUES(@cid,@bid,@d,@exp,@sub,@disc,@tax,@tot,@notes,@u)",
        new { cid = b.CustomerId, bid = branch_id, d = date, exp = DateOk(b.ExpiryDate) ? b.ExpiryDate : null,
              sub = R2(sub), disc = R2(b.Discount), tax = R2(b.Tax), tot = total, notes = (b.Notes ?? "").Left(300), u = me!.Id });
    await db.ExecuteAsync("UPDATE Estimates SET EstimateNo='EST'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id });
    foreach (var i in b.Items)
        await db.ExecuteAsync("INSERT INTO EstimateItems(EstimateId,ProductId,Description,Qty,UnitPrice,LineTotal) VALUES(@id,@pid,@desc,@q,@up,@lt)",
            new { id, pid = i.ProductId, desc = (i.Description ?? "").Left(200), q = i.Qty, up = i.UnitPrice, lt = R2(i.Qty * i.UnitPrice) });
    await Audit(db, me!.Id, "Estimates", id.ToString(), null, new { b.CustomerId, total });
    return Results.Json(new { id });
});
app.MapPost("/api/estimates/{id:int}/convert", async (HttpRequest req, int id) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var e = await db.QuerySingleOrDefaultAsync("SELECT * FROM Estimates WHERE Id=@id", new { id });
    if (e == null || !me!.Branches.Contains((int)e.BranchId)) return Bad("Not found", 404);
    if ((string)e.Status == "converted") return Bad("Already converted");
    var items = await db.QueryAsync("SELECT * FROM EstimateItems WHERE EstimateId=@id", new { id });
    var invId = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Invoices(CustomerId,BranchId,InvDate,DueDate,Subtotal,Discount,Tax,Total,Notes,CreatedBy) OUTPUT INSERTED.Id
        VALUES(@cid,@bid,@d,@due,@sub,@disc,@tax,@tot,@notes,@u)",
        new { cid = (int)e.CustomerId, bid = (int)e.BranchId, d = Today(), due = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-dd"),
              sub = (decimal)e.Subtotal, disc = (decimal)e.Discount, tax = (decimal)e.Tax, tot = (decimal)e.Total, notes = "Converted from " + e.EstimateNo, u = me!.Id });
    await db.ExecuteAsync("UPDATE Invoices SET InvoiceNo='INV'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id = invId });
    foreach (var i in items)
        await db.ExecuteAsync("INSERT INTO InvoiceItems(InvoiceId,ProductId,Description,Qty,UnitPrice,LineTotal) VALUES(@id,@pid,@desc,@q,@up,@lt)",
            new { id = invId, pid = (int?)i.ProductId, desc = (string)i.Description, q = (decimal)i.Qty, up = (decimal)i.UnitPrice, lt = (decimal)i.LineTotal });
    await db.ExecuteAsync("UPDATE Estimates SET Status='converted' WHERE Id=@id", new { id });
    await Audit(db, me!.Id, "Estimates", id.ToString(), new { status = "sent" }, new { status = "converted", invoice_id = invId });
    return Results.Json(new { invoice_id = invId });
});

// =====================================================================
// INVOICES (sales) — creating one deducts stock and posts to the ledger
// via payments recorded against it
// =====================================================================
async Task<string> InvStatus(SqlConnection db, int id, decimal total)
{
    var paid = await db.ExecuteScalarAsync<decimal?>("SELECT SUM(Amount) FROM InvoicePayments WHERE InvoiceId=@id", new { id }) ?? 0m;
    var due = (DateTime)(await db.ExecuteScalarAsync<DateTime>("SELECT DueDate FROM Invoices WHERE Id=@id", new { id }));
    string st = paid >= total ? "paid" : paid > 0 ? "partial" : (due.Date < DateTime.UtcNow.Date ? "overdue" : "sent");
    await db.ExecuteAsync("UPDATE Invoices SET Status=@s WHERE Id=@id", new { s = st, id });
    return st;
}

app.MapGet("/api/invoices", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var rows = (await db.QueryAsync($@"
        SELECT i.Id, i.InvoiceNo, i.CustomerId, c.Name AS CustomerName, i.BranchId,
               CONVERT(varchar(10), i.InvDate, 23) AS InvDate, CONVERT(varchar(10), i.DueDate, 23) AS DueDate,
               i.Status, i.Total, ISNULL((SELECT SUM(Amount) FROM InvoicePayments WHERE InvoiceId=i.Id),0) AS Paid
        FROM Invoices i JOIN Customers c ON c.Id=i.CustomerId WHERE i.BranchId IN ({ids}) ORDER BY i.InvDate DESC, i.Id DESC")).ToList();
    var result = rows.Select(r => new InvoiceRow((int)r.Id, r.InvoiceNo, (int)r.CustomerId, r.CustomerName, (int)r.BranchId,
        r.InvDate, r.DueDate, r.Status, r.Total, r.Paid, R2((decimal)r.Total - (decimal)r.Paid)));
    return Results.Json(result);
});
app.MapPost("/api/invoices", async (HttpRequest req, int branch_id, InvoiceBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (!me!.Branches.Contains(branch_id)) return Bad("Not allowed", 403);
    if (b.Items == null || b.Items.Count == 0) return Bad("Add at least one item");
    foreach (var i in b.Items) if (i.ProductId != null)
    {
        var stock = await db.ExecuteScalarAsync<decimal?>("SELECT StockQty FROM Products WHERE Id=@id", new { id = i.ProductId });
        if (stock == null) return Bad("A selected product does not exist");
        if (stock < i.Qty) return Bad($"Not enough stock for one of the items (available: {stock})");
    }
    var date = DateOk(b.Date) ? b.Date! : Today();
    var due = DateOk(b.DueDate) ? b.DueDate! : DateTime.Parse(date).AddDays(30).ToString("yyyy-MM-dd");
    var sub = b.Items.Sum(i => i.Qty * i.UnitPrice);
    var total = R2(sub - b.Discount + b.Tax);
    var id = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Invoices(CustomerId,BranchId,InvDate,DueDate,Subtotal,Discount,Tax,Total,Notes,CreatedBy) OUTPUT INSERTED.Id
        VALUES(@cid,@bid,@d,@due,@sub,@disc,@tax,@tot,@notes,@u)",
        new { cid = b.CustomerId, bid = branch_id, d = date, due, sub = R2(sub), disc = R2(b.Discount), tax = R2(b.Tax), tot = total, notes = (b.Notes ?? "").Left(300), u = me!.Id });
    await db.ExecuteAsync("UPDATE Invoices SET InvoiceNo='INV'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id });
    foreach (var i in b.Items)
    {
        await db.ExecuteAsync("INSERT INTO InvoiceItems(InvoiceId,ProductId,Description,Qty,UnitPrice,LineTotal) VALUES(@id,@pid,@desc,@q,@up,@lt)",
            new { id, pid = i.ProductId, desc = (i.Description ?? "").Left(200), q = i.Qty, up = i.UnitPrice, lt = R2(i.Qty * i.UnitPrice) });
        if (i.ProductId != null)
        {
            await db.ExecuteAsync("UPDATE Products SET StockQty = StockQty - @q WHERE Id=@pid", new { q = i.Qty, pid = i.ProductId });
            await db.ExecuteAsync("INSERT INTO StockMovements(ProductId,BranchId,MoveType,Qty,RefType,RefId,MoveDate,CreatedBy) VALUES(@pid,@bid,'out',@q,'invoice',@id,@d,@u)",
                new { pid = i.ProductId, bid = branch_id, q = i.Qty, id = id.ToString(), d = date, u = me.Id });
        }
    }
    await Audit(db, me!.Id, "Invoices", id.ToString(), null, new { b.CustomerId, total });
    return Results.Json(new { id });
});
app.MapGet("/api/invoices/{id:int}", async (HttpRequest req, int id) =>
{
    // Note: this detail endpoint is not used by the current screens (which only
    // need the list and the payment form) and returns raw column names/timestamps
    // rather than the app's usual snake_case, plain-date fields. Clean this up
    // (like the list endpoint above) before building a dedicated invoice-detail screen.
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var inv = await db.QuerySingleOrDefaultAsync("SELECT * FROM Invoices WHERE Id=@id", new { id });
    if (inv == null || !me!.Branches.Contains((int)inv.BranchId)) return Bad("Not found", 404);
    var items = await db.QueryAsync<LineItemRow>("SELECT Id, ProductId, Description, Qty, UnitPrice, LineTotal FROM InvoiceItems WHERE InvoiceId=@id", new { id });
    var pays = await db.QueryAsync<MoneyRow>("SELECT Id, CONVERT(varchar(10),PayDate,23) AS PayDate, Amount, Method, ReferenceNo, ReceivedBy AS By FROM InvoicePayments WHERE InvoiceId=@id ORDER BY PayDate DESC", new { id });
    return Results.Json(new { invoice = inv, items, payments = pays });
});
app.MapPost("/api/invoices/{id:int}/payments", async (HttpRequest req, int id, InvBillPaymentBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var inv = await db.QuerySingleOrDefaultAsync("SELECT * FROM Invoices WHERE Id=@id", new { id });
    if (inv == null || !me!.Branches.Contains((int)inv.BranchId)) return Bad("Not found", 404);
    if (!(b.Amount > 0)) return Bad("Enter a positive amount");
    var date = DateOk(b.Date) ? b.Date! : Today();
    var txId = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Transactions(Date,BranchId,Type,Category,Description,Amount,PaidBy,Status,CreatedBy,ApprovedBy)
        OUTPUT INSERTED.Id VALUES(@d,@bid,'income','Sales Invoice',@desc,@amt,@by,'approved',@u,@u)",
        new { d = date, bid = (int)inv.BranchId, desc = "Payment for " + (string)inv.InvoiceNo, amt = R2(b.Amount), by = (b.By ?? "").Left(60), u = me!.Id });
    await db.ExecuteAsync("UPDATE Transactions SET ReceiptNo='R'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id = txId });
    var pid = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO InvoicePayments(InvoiceId,PayDate,Amount,Method,ReferenceNo,ReceivedBy,TxId,CreatedBy) OUTPUT INSERTED.Id
        VALUES(@id,@d,@amt,@m,@ref,@by,@tx,@u)",
        new { id, d = date, amt = R2(b.Amount), m = (b.Method ?? "Cash").Left(20), @ref = (b.ReferenceNo ?? "").Left(30), by = (b.By ?? "").Left(60), tx = txId, u = me.Id });
    var status = await InvStatus(db, id, (decimal)inv.Total);
    await Audit(db, me.Id, "InvoicePayments", pid.ToString(), null, new { id, b.Amount });
    return Results.Json(new { id = pid, status });
});

// =====================================================================
// BILLS (purchases / payables)
// =====================================================================
async Task<string> BillStatus(SqlConnection db, int id, decimal total)
{
    var paid = await db.ExecuteScalarAsync<decimal?>("SELECT SUM(Amount) FROM BillPayments WHERE BillId=@id", new { id }) ?? 0m;
    var due = (DateTime)(await db.ExecuteScalarAsync<DateTime>("SELECT DueDate FROM Bills WHERE Id=@id", new { id }));
    string st = paid >= total ? "paid" : paid > 0 ? "partial" : (due.Date < DateTime.UtcNow.Date ? "overdue" : "open");
    await db.ExecuteAsync("UPDATE Bills SET Status=@s WHERE Id=@id", new { s = st, id });
    return st;
}
app.MapGet("/api/bills", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var rows = (await db.QueryAsync($@"
        SELECT bl.Id, bl.BillNo, bl.SupplierId, s.Name AS SupplierName, bl.BranchId,
               CONVERT(varchar(10), bl.BillDate, 23) AS BillDate, CONVERT(varchar(10), bl.DueDate, 23) AS DueDate,
               bl.Status, bl.Total, ISNULL((SELECT SUM(Amount) FROM BillPayments WHERE BillId=bl.Id),0) AS Paid
        FROM Bills bl JOIN Suppliers s ON s.Id=bl.SupplierId WHERE bl.BranchId IN ({ids}) ORDER BY bl.BillDate DESC, bl.Id DESC")).ToList();
    var result = rows.Select(r => new BillRow((int)r.Id, r.BillNo, (int)r.SupplierId, r.SupplierName, (int)r.BranchId,
        r.BillDate, r.DueDate, r.Status, r.Total, r.Paid, R2((decimal)r.Total - (decimal)r.Paid)));
    return Results.Json(result);
});
app.MapPost("/api/bills", async (HttpRequest req, int branch_id, BillBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    if (!me!.Branches.Contains(branch_id)) return Bad("Not allowed", 403);
    if (!(b.Total > 0)) return Bad("Enter a positive total");
    var date = DateOk(b.Date) ? b.Date! : Today();
    var due = DateOk(b.DueDate) ? b.DueDate! : DateTime.Parse(date).AddDays(30).ToString("yyyy-MM-dd");
    var id = await db.ExecuteScalarAsync<int>(
        "INSERT INTO Bills(SupplierId,BranchId,BillDate,DueDate,Total,Notes,CreatedBy) OUTPUT INSERTED.Id VALUES(@sid,@bid,@d,@due,@t,@notes,@u)",
        new { sid = b.SupplierId, bid = branch_id, d = date, due, t = R2(b.Total), notes = (b.Notes ?? "").Left(300), u = me!.Id });
    await db.ExecuteAsync("UPDATE Bills SET BillNo='BILL'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id });
    await Audit(db, me!.Id, "Bills", id.ToString(), null, b);
    return Results.Json(new { id });
});
app.MapPost("/api/bills/{id:int}/payments", async (HttpRequest req, int id, InvBillPaymentBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var bl = await db.QuerySingleOrDefaultAsync("SELECT * FROM Bills WHERE Id=@id", new { id });
    if (bl == null || !me!.Branches.Contains((int)bl.BranchId)) return Bad("Not found", 404);
    if (!(b.Amount > 0)) return Bad("Enter a positive amount");
    var date = DateOk(b.Date) ? b.Date! : Today();
    var txId = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO Transactions(Date,BranchId,Type,Category,Description,Amount,PaidBy,Status,CreatedBy,ApprovedBy)
        OUTPUT INSERTED.Id VALUES(@d,@bid,'expense','Supplier Payment',@desc,@amt,@by,'approved',@u,@u)",
        new { d = date, bid = (int)bl.BranchId, desc = "Payment for " + (string)bl.BillNo, amt = R2(b.Amount), by = (b.By ?? "").Left(60), u = me!.Id });
    await db.ExecuteAsync("UPDATE Transactions SET ReceiptNo='R'+RIGHT('000000'+CAST(Id AS NVARCHAR(6)),6) WHERE Id=@id", new { id = txId });
    var pid = await db.ExecuteScalarAsync<int>(@"
        INSERT INTO BillPayments(BillId,PayDate,Amount,Method,ReferenceNo,PaidBy,TxId,CreatedBy) OUTPUT INSERTED.Id
        VALUES(@id,@d,@amt,@m,@ref,@by,@tx,@u)",
        new { id, d = date, amt = R2(b.Amount), m = (b.Method ?? "Cash").Left(20), @ref = (b.ReferenceNo ?? "").Left(30), by = (b.By ?? "").Left(60), tx = txId, u = me.Id });
    var status = await BillStatus(db, id, (decimal)bl.Total);
    await Audit(db, me.Id, "BillPayments", pid.ToString(), null, new { id, b.Amount });
    return Results.Json(new { id = pid, status });
});

// =====================================================================
// PAYMENT-DUE REMINDERS — the list to remind on; sending itself uses
// WhatsApp (wa.me) and email (mailto:) links built in the browser, which
// need no account. Automatic bulk SMS/email needs a paid provider
// (e.g. Twilio, SendGrid) and its own account — not included here.
// =====================================================================
app.MapGet("/api/reminders", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var recv = (await db.QueryAsync($@"
        SELECT i.Id, i.InvoiceNo AS No, c.Name, c.Phone, c.Email, i.Total - ISNULL((SELECT SUM(Amount) FROM InvoicePayments WHERE InvoiceId=i.Id),0) AS Balance,
               CONVERT(varchar(10), i.DueDate, 23) AS DueDate
        FROM Invoices i JOIN Customers c ON c.Id=i.CustomerId
        WHERE i.BranchId IN ({ids}) AND i.Status NOT IN ('paid','cancelled')")).ToList();
    var pay = (await db.QueryAsync($@"
        SELECT bl.Id, bl.BillNo AS No, s.Name, s.Phone, s.Email, bl.Total - ISNULL((SELECT SUM(Amount) FROM BillPayments WHERE BillId=bl.Id),0) AS Balance,
               CONVERT(varchar(10), bl.DueDate, 23) AS DueDate
        FROM Bills bl JOIN Suppliers s ON s.Id=bl.SupplierId
        WHERE bl.BranchId IN ({ids}) AND bl.Status NOT IN ('paid','cancelled')")).ToList();
    var today = DateTime.UtcNow.Date;
    object Shape(dynamic x, string kind) => new
    {
        kind, id = (int)x.Id, no = x.No, name = x.Name, phone = x.Phone, email = x.Email,
        balance = R2((decimal)x.Balance), due_date = (string)x.DueDate,
        days_left = (int)(DateTime.Parse((string)x.DueDate) - today).TotalDays
    };
    var list = recv.Where(x => (decimal)x.Balance > 0).Select(x => Shape(x, "receivable"))
        .Concat(pay.Where(x => (decimal)x.Balance > 0).Select(x => Shape(x, "payable")))
        .OrderBy(x => ((dynamic)x).days_left);
    return Results.Json(list);
});

// =====================================================================
// BUSINESS REPORTS AT A GLANCE
// =====================================================================
app.MapGet("/api/reports/business", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var sales = await db.ExecuteScalarAsync<decimal?>($"SELECT SUM(Total) FROM Invoices WHERE BranchId IN ({ids}) AND Status<>'cancelled'") ?? 0;
    var purchases = await db.ExecuteScalarAsync<decimal?>($"SELECT SUM(Total) FROM Bills WHERE BranchId IN ({ids}) AND Status<>'cancelled'") ?? 0;
    var receivables = await db.ExecuteScalarAsync<decimal?>($@"
        SELECT SUM(i.Total - ISNULL((SELECT SUM(p.Amount) FROM InvoicePayments p WHERE p.InvoiceId=i.Id),0))
        FROM Invoices i WHERE i.BranchId IN ({ids}) AND i.Status NOT IN ('paid','cancelled')") ?? 0;
    var payables = await db.ExecuteScalarAsync<decimal?>($@"
        SELECT SUM(bl.Total - ISNULL((SELECT SUM(p.Amount) FROM BillPayments p WHERE p.BillId=bl.Id),0))
        FROM Bills bl WHERE bl.BranchId IN ({ids}) AND bl.Status NOT IN ('paid','cancelled')") ?? 0;
    var lowStock = await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM Products WHERE (BranchId IS NULL OR BranchId IN ({ids})) AND StockQty <= ReorderLevel");
    var dueSoon = await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM Invoices WHERE BranchId IN ({ids}) AND Status NOT IN ('paid','cancelled') AND DueDate <= @d",
        new { d = DateTime.UtcNow.Date.AddDays(7) });
    return Results.Json(new
    {
        total_sales = R2(sales), total_purchases = R2(purchases),
        receivables_total = R2(receivables), payables_total = R2(payables),
        low_stock_count = lowStock, invoices_due_soon = dueSoon
    });
});

app.MapGet("/api/reports/by-category", async (HttpRequest req, string? from, string? to) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var ids = me!.Branches.Count > 0 ? string.Join(",", me.Branches) : "-1";
    var f = DateOk(from) ? from! : DateTime.UtcNow.Date.ToString("yyyy-MM-01");
    var t = DateOk(to) ? to! : Today();
    var rows = await db.QueryAsync(
        $@"SELECT Type, Category, SUM(Amount) AS Amount FROM Transactions
           WHERE BranchId IN ({ids}) AND Status='approved' AND Date BETWEEN @f AND @t AND Type IN ('income','expense')
           GROUP BY Type, Category ORDER BY Type, SUM(Amount) DESC", new { f, t });
    var list = rows.Select(r => new { type = (string)r.Type, category = (string)r.Category, amount = R2((decimal)r.Amount) });
    return Results.Json(list);
});

// =====================================================================
// LINKS (office use)
// =====================================================================
app.MapGet("/api/links", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db); if (err != null) return err;
    return Results.Json(await db.QueryAsync<LinkRow>(
        "SELECT Id, Name, Url, Notes, SortOrder, CreatedBy, Created FROM Links ORDER BY SortOrder, Name"));
});

app.MapPost("/api/links", async (HttpRequest req, LinkBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    if (string.IsNullOrWhiteSpace(b.Name) || !IsUrl(b.Url)) return Bad("Name and a valid http(s) link are required");
    var id = await db.ExecuteScalarAsync<int>(
        "INSERT INTO Links(Name, Url, Notes, SortOrder, CreatedBy) OUTPUT INSERTED.Id VALUES(@n,@u,@no,@s,@c)",
        new { n = b.Name.Left(80), u = b.Url!.Left(500), no = (b.Notes ?? "").Left(200), s = b.Sort ?? 0, c = me!.Id });
    await Audit(db, me!.Id, "Links", id.ToString(), null, b);
    return Results.Json(new { id });
});

app.MapPatch("/api/links/{id:int}", async (HttpRequest req, int id, LinkBody b) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var o = await db.QuerySingleOrDefaultAsync("SELECT * FROM Links WHERE Id=@id", new { id });
    if (o == null) return Bad("Not found", 404);
    if (b.Url != null && !IsUrl(b.Url)) return Bad("Invalid link");
    await db.ExecuteAsync("UPDATE Links SET Name=@n, Url=@u, Notes=@no, SortOrder=@s WHERE Id=@id",
        new { n = (b.Name ?? (string)o.Name).Left(80), u = (b.Url ?? (string)o.Url).Left(500), no = (b.Notes ?? (string)o.Notes).Left(200), s = b.Sort ?? (int)o.SortOrder, id });
    await Audit(db, me!.Id, "Links", id.ToString(), o, b);
    return Results.Json(new { ok = true });
});

app.MapDelete("/api/links/{id:int}", async (HttpRequest req, int id) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "admin", "superadmin"); if (err != null) return err;
    var o = await db.QuerySingleOrDefaultAsync("SELECT * FROM Links WHERE Id=@id", new { id });
    if (o == null) return Bad("Not found", 404);
    await db.ExecuteAsync("DELETE FROM Links WHERE Id=@id", new { id });
    await Audit(db, me!.Id, "Links", id.ToString(), o, null);
    return Results.Json(new { ok = true });
});

// =====================================================================
// LOGS (read-only, Super Admin)
// =====================================================================
app.MapGet("/api/activity", async (HttpRequest req, int? user_id, string? action) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    var where = new List<string> { "1=1" }; var p = new DynamicParameters();
    if (user_id != null) { where.Add("a.UserId=@u"); p.Add("u", user_id); }
    if (!string.IsNullOrEmpty(action)) { where.Add("a.Action LIKE @a"); p.Add("a", "%" + action.Left(30) + "%"); }
    var sql = $@"SELECT TOP 300 a.Id, a.Ts, a.UserId, a.Action, a.Detail, a.Ip, a.Ua, a.Ok, u.Username
                 FROM ActivityLog a LEFT JOIN Users u ON u.Id=a.UserId
                 WHERE {string.Join(" AND ", where)} ORDER BY a.Id DESC";
    return Results.Json(await db.QueryAsync<ActivityRow>(sql, p));
});

app.MapGet("/api/audit", async (HttpRequest req) =>
{
    using var db = Db(); await db.OpenAsync();
    var (me, err) = await Guard(req, db, "superadmin"); if (err != null) return err;
    return Results.Json(await db.QueryAsync<AuditRow>(
        "SELECT TOP 300 Id, Ts, UserId, Tbl, RecId, OldVal, NewVal FROM AuditLog ORDER BY Id DESC"));
});

app.MapFallbackToFile("index.html");
app.Run();

// =====================================================================
// Request body records and a small string helper
// =====================================================================
record Me(int Id, string Username, string FullName, string Role, string Theme, string Lang, bool TwoFactorOn, List<int> Branches);
record LoginBody(string? Username, string? Password, string? Code);
record TotpCodeBody(string? Code);
record PasswordCheckBody(string? Password);
record MeBody(string? Theme, string? Lang, string? Old, string? Password);
record NewUserBody(string? Username, string? Name, string? Password, string? Role, List<int>? Branches);
record UserPatchBody(bool? Active, string? Role, string? Name, string? Password, List<int>? Branches);
record BranchPatchBody(string? Name, string? Cr);
record SettingsBody(string? CompanyName, string? LicenseEnd);
record CloseBody(string? Month);
record TxBody(string? Date, int BranchId, string? Type, string? Category, string? Description, decimal Amount, string? PaidBy, string? WorkerQid, string? ExtCode);
record CancelBody(string? Reason);
record WorkerBody(string? Qid, string? Name, string? Nationality, int BranchId, string? RpExpiry, string? PassportNo, string? PassportExpiry, string? Mobile, decimal? Fee);
record LinkBody(string? Name, string? Url, string? Notes, int? Sort);
record PaymentBody(string? Date, decimal Amount, string? Method, string? ReceivedBy, string? ReferenceNo, string? Remarks);
record PaymentRow(int Id, string Qid, string PaymentDate, decimal Amount, string Method, string ReceivedBy, string ReferenceNo, string Remarks, DateTime Created);
record ContactBody(string? Code, string? Name, string? ContactType, string? Company, string? Position, string? IdNo, string? Mobile, string? Email, int? BranchId, string? Remarks);
record ContactRow(string Code, string Name, string ContactType, string Company, string Position, string IdNo, string Mobile, string Email, int? BranchId, string Status, string Remarks, decimal MoneyIn, decimal MoneyOut);
record AttachmentRow(int Id, string Section, string RecordId, string FileName, string ContentType, long SizeBytes, string Note, int? UploadedBy, DateTime Uploaded);

// ---- Business management (sales, purchases, inventory) ----
record ItemBody(int? ProductId, string? Description, decimal Qty, decimal UnitPrice);
record CustomerBody(string? Name, string? Company, string? Phone, string? Email, string? Address, int? BranchId);
record SupplierBody(string? Name, string? Company, string? Phone, string? Email, string? Address, int? BranchId);
record ProductBody(string? Sku, string? Name, string? Category, string? Unit, decimal CostPrice, decimal SalePrice, decimal StockQty, decimal ReorderLevel, int? BranchId);
record StockAdjustBody(decimal Qty, string? Note);
record EstimateBody(int CustomerId, string? Date, string? ExpiryDate, decimal Discount, decimal Tax, string? Notes, List<ItemBody>? Items);
record InvoiceBody(int CustomerId, string? Date, string? DueDate, decimal Discount, decimal Tax, string? Notes, List<ItemBody>? Items);
record BillBody(int SupplierId, string? Date, string? DueDate, decimal Total, string? Notes);
record InvBillPaymentBody(string? Date, decimal Amount, string? Method, string? ReferenceNo, string? By);

record CustomerRow(int Id, string Name, string Company, string Phone, string Email, string Address, int? BranchId);
record SupplierRow(int Id, string Name, string Company, string Phone, string Email, string Address, int? BranchId);
record ProductRow(int Id, string Sku, string Name, string Category, string Unit, decimal CostPrice, decimal SalePrice, decimal StockQty, decimal ReorderLevel, int? BranchId);
record InvoiceRow(int Id, string? InvoiceNo, int CustomerId, string CustomerName, int BranchId, string InvDate, string DueDate, string Status, decimal Total, decimal Paid, decimal Balance);
record EstimateRow(int Id, string? EstimateNo, int CustomerId, string CustomerName, int BranchId, string EstDate, string? ExpiryDate, string Status, decimal Total);
record BillRow(int Id, string? BillNo, int SupplierId, string SupplierName, int BranchId, string BillDate, string DueDate, string Status, decimal Total, decimal Paid, decimal Balance);
record LineItemRow(int Id, int? ProductId, string Description, decimal Qty, decimal UnitPrice, decimal LineTotal);
record MoneyRow(int Id, string PayDate, decimal Amount, string Method, string ReferenceNo, string By);

// Plain result shapes for endpoints that used to return raw dynamic Dapper
// rows. Property names here decide the JSON field names the browser sees
// once the SnakeCaseLower policy above runs, so they are kept simple and
// match the front-end (wwwroot/index.html) exactly: BranchId -> branch_id,
// ReceiptNo -> receipt_no, WorkerQid -> worker_qid, and so on.
record BranchRow(int Id, string Name, string Cr);
record TxRow(int Id, string? ReceiptNo, string Date, int BranchId, string Type, string Category,
    string Description, decimal Amount, string PaidBy, string? WorkerQid, string ExtCode, string Status,
    string CancelReason, int? CreatedBy, int? ApprovedBy, DateTime Created);
record LinkRow(int Id, string Name, string Url, string Notes, int SortOrder, int? CreatedBy, DateTime Created);
record ActivityRow(int Id, DateTime Ts, int? UserId, string Action, string Detail, string Ip, string Ua, bool Ok, string? Username);
record AuditRow(int Id, DateTime Ts, int? UserId, string Tbl, string RecId, string? OldVal, string? NewVal);

static class Ext { public static string Left(this string s, int n) => s.Length <= n ? s : s[..n]; }

// Self-contained TOTP (RFC 6238) two-factor codes — no external package,
// no outside account or paid service. Compatible with Google Authenticator,
// Microsoft Authenticator, Authy and similar apps.
static class TotpHelper
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret()
    {
        var bytes = new byte[20];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Base32Encode(bytes);
    }

    static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(value >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(value << (5 - bits)) & 31]);
        return sb.ToString();
    }

    static byte[] Base32Decode(string s)
    {
        s = s.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        int bits = 0, value = 0;
        foreach (var c in s)
        {
            int idx = Alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }

    static string ComputeCode(string base32Secret, long counter)
    {
        var key = Base32Decode(base32Secret);
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);
        using var hmac = new System.Security.Cryptography.HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);
        int offset = hash[^1] & 0x0F;
        int binCode = ((hash[offset] & 0x7F) << 24) | ((hash[offset + 1] & 0xFF) << 16) | ((hash[offset + 2] & 0xFF) << 8) | (hash[offset + 3] & 0xFF);
        return (binCode % 1_000_000).ToString("D6");
    }

    // Accepts the current 30-second code plus one step either side, to allow for clock drift.
    public static bool Verify(string base32Secret, string code, int step = 30, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code)) return false;
        long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / step;
        var trimmed = code.Trim();
        for (long i = -window; i <= window; i++)
            if (ComputeCode(base32Secret, counter + i) == trimmed) return true;
        return false;
    }
}

