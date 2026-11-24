using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

internal sealed record AdminGate(bool Login);
internal static class AdminAccess
{
    internal sealed record LoginInput(string Login,string Password,string Code);
    internal static bool ValidSeal(string? key) => key is {Length:64} && key.All(Uri.IsHexDigit);
    private static string Normalize(string? login) => (login ?? "").Trim().ToLowerInvariant();
    private static bool ValidLogin(string login) => Regex.IsMatch(login,@"\A[a-z0-9][a-z0-9._-]{2,63}\z");
    private static byte[] Password(string password,byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);

    // RFC 6238: 30-second counter, six digits, HMAC-SHA1.
    private static long? MatchStep(byte[] secret,string code,long previous)
    {
        if(code.Length!=6 || !code.All(char.IsAsciiDigit)) return null;
        long current=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;
        byte[] input=Encoding.ASCII.GetBytes(code);
        Span<byte> counter=stackalloc byte[8];
        foreach(long step in new[]{current,current-1,current+1})
        {
            if(step<=previous) continue;
            BinaryPrimitives.WriteInt64BigEndian(counter,step);
            var mac=HMACSHA1.HashData(secret,counter);
            var number=(BinaryPrimitives.ReadUInt32BigEndian(mac.AsSpan(mac[^1]&15)) & 0x7fffffff) % 1000000;
            if(CryptographicOperations.FixedTimeEquals(input,Encoding.ASCII.GetBytes(number.ToString("D6",CultureInfo.InvariantCulture))))
                return step;
        }
        return null;
    }

    internal static async Task Bootstrap(NpgsqlDataSource db,string? seal,string login,bool fromStdin)
    {
        login=Normalize(login);
        if(!ValidSeal(seal) || !ValidLogin(login)) throw new InvalidOperationException("Vérifier la clé serveur et l’identifiant administrateur (3 à 64 caractères).");
        await using var profile=db.CreateCommand("SELECT access_mode FROM company");
        if((string?)await profile.ExecuteScalarAsync()!="Company")
            throw new InvalidOperationException("Démarrer le serveur en profil Company avant l’inscription.");
        Console.WriteLine("Mot de passe administrateur (15 à 128 caractères) :");
        string password;
        if(fromStdin) password=Console.ReadLine() ?? "";
        else
        {
            var chars=new StringBuilder();
            while(true)
            {
                var key=Console.ReadKey(true);
                if(key.Key==ConsoleKey.Enter) break;
                if(key.Key==ConsoleKey.Backspace) { if(chars.Length>0) chars.Length--; }
                else if(!char.IsControl(key.KeyChar)) chars.Append(key.KeyChar);
            }
            password=chars.ToString();
        }
        if(password.Length is <15 or >128) throw new InvalidOperationException("Longueur du mot de passe invalide.");
        byte[] secret=RandomNumberGenerator.GetBytes(20),salt=RandomNumberGenerator.GetBytes(16);
        // 160 bits are exactly 32 base32 digits.
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits=string.Concat(secret.Select(x=>Convert.ToString(x,2).PadLeft(8,'0')));
        var base32=string.Concat(Enumerable.Range(0,32).Select(i=>alphabet[Convert.ToInt32(bits.Substring(i*5,5),2)]));
        Console.WriteLine("Clé TOTP : "+base32);
        Console.WriteLine("Saisir le code affiché par votre application (6 chiffres, 30 secondes) :");
        var step=MatchStep(secret,Console.ReadLine()?.Trim() ?? "",-1);
        if(!step.HasValue) throw new InvalidOperationException("Code incorrect. Compte non créé.");
        await using var cmd=db.CreateCommand("""
            INSERT INTO company_admin(login,salt,password_hash,otp_secret,last_step)
            VALUES(@login,@salt,@hash,pgp_sym_encrypt_bytea(@secret,@seal),@step) ON CONFLICT(login) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("login",login);
        cmd.Parameters.AddWithValue("salt",salt);
        cmd.Parameters.AddWithValue("hash",Password(password,salt));
        cmd.Parameters.AddWithValue("secret",secret);
        cmd.Parameters.AddWithValue("seal",seal!);
        cmd.Parameters.AddWithValue("step",step.Value);
        if(await cmd.ExecuteNonQueryAsync()!=1) throw new InvalidOperationException("Identifiant déjà attribué.");
        Console.WriteLine("Compte administrateur créé.");
    }

    internal static async Task<bool> Authorize(HttpContext ctx,NpgsqlDataSource db,AdminGate gate)
    {
        if(gate.Login) return true;
        string header=ctx.Request.Headers.Authorization.ToString();
        if(header.Length==71 && header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase) && header[7..].All(Uri.IsHexDigit))
        {
            await using var cmd=db.CreateCommand("""
                SELECT a.id FROM admin_session s JOIN company_admin a ON a.id=s.admin_id
                WHERE s.token_hash=digest(@token,'sha256') AND s.expires_at>now() AND a.enabled
                """);
            cmd.Parameters.AddWithValue("token",header[7..]);
            if(await cmd.ExecuteScalarAsync(ctx.RequestAborted) is Guid id)
            {
                ctx.Items[typeof(AdminGate)]=id;
                return true;
            }
        }
        await Results.Problem("Session administrateur requise.",statusCode:401).ExecuteAsync(ctx);
        return false;
    }

    internal static void Map(WebApplication app,NpgsqlDataSource db,string seal)
    {
        app.MapPost("/api/admin/session",(LoginInput input,CancellationToken ct)=>Login(db,seal,input,ct))
           .WithMetadata(new AdminGate(true)).RequireRateLimiting("admin-login");
        var group=app.MapGroup("/api/admin").WithMetadata(new AdminGate(false));
        group.MapDelete("/session",async(HttpContext ctx,CancellationToken ct)=>{
            await using var cmd=db.CreateCommand("DELETE FROM admin_session WHERE token_hash=digest(@token,'sha256')");
            cmd.Parameters.AddWithValue("token",ctx.Request.Headers.Authorization.ToString()[7..]);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });
        bool sandbox = app.Configuration.GetValue<bool>("Transport:SandboxPayments");
        SupportDiagnostic.Map(app,group,db);
        AdminStaff.Map(group,db,sandbox);
        if (sandbox) SandboxRefunds.Map(group,db);
    }

    internal static async Task<bool> SetActor(NpgsqlConnection conn,NpgsqlTransaction tx,HttpContext ctx,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("""
            SELECT set_config('transport.admin_id',a.id::text,true) FROM company_admin a JOIN admin_session s ON s.admin_id=a.id
            WHERE a.id=@id AND a.enabled AND s.token_hash=digest(@token,'sha256') AND s.expires_at>now() FOR SHARE OF a,s
            """,conn,tx);
        cmd.Parameters.AddWithValue("id",(Guid)ctx.Items[typeof(AdminGate)]!);
        cmd.Parameters.AddWithValue("token",ctx.Request.Headers.Authorization.ToString()[7..]);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<IResult> Login(NpgsqlDataSource db,string seal,LoginInput input,CancellationToken ct)
    {
        var denied=Results.Problem("Identifiants ou code non valides.",statusCode:401);
        string login=Normalize(input.Login);
        if(!ValidLogin(login) || input.Password is null or {Length:0} or {Length:>128} || input.Code is not {Length:6}) return denied;
        await using var conn=await db.OpenConnectionAsync(ct);
        await using var tx=await conn.BeginTransactionAsync(ct);
        await using var find=new NpgsqlCommand("""
            SELECT id,salt,password_hash,pgp_sym_decrypt_bytea(otp_secret,@seal),last_step
            FROM company_admin WHERE login=@login AND enabled AND (locked_until IS NULL OR locked_until<=now()) FOR UPDATE
            """,conn,tx);
        find.Parameters.AddWithValue("login",login);
        find.Parameters.AddWithValue("seal",seal);
        Guid id;
        long? step;
        await using(var r=await find.ExecuteReaderAsync(ct))
        {
            if(!await r.ReadAsync(ct))
            {
                Password(input.Password,new byte[16]);
                return denied;
            }
            id=r.GetGuid(0);
            var matches=CryptographicOperations.FixedTimeEquals(Password(input.Password,(byte[])r[1]),(byte[])r[2]);
            step=matches?MatchStep((byte[])r[3],input.Code,r.GetInt64(4)):null;
        }
        if(step is null)
        {
            await using var bad=new NpgsqlCommand("""
                UPDATE company_admin SET failures=(failures+1)%5,
                  locked_until=CASE WHEN failures=4 THEN clock_timestamp()+interval '5 minutes' END WHERE id=@id
                """,conn,tx);
            bad.Parameters.AddWithValue("id",id);
            await bad.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return denied;
        }
        string token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var issue=new NpgsqlCommand("""
            UPDATE company_admin SET failures=0,locked_until=NULL,last_step=@step WHERE id=@id;
            DELETE FROM admin_session WHERE expires_at<=now();
            INSERT INTO admin_session(admin_id,token_hash,expires_at) VALUES(@id,digest(@token,'sha256'),now()+interval '20 minutes');
            """,conn,tx);
        issue.Parameters.AddWithValue("id",id);
        issue.Parameters.AddWithValue("step",step.Value);
        issue.Parameters.AddWithValue("token",token);
        await issue.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new {token});
    }
}
