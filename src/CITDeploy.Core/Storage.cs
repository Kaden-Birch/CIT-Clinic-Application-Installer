using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace CITDeploy.Core;
public sealed class PortableStorage
{
    public string Root
    {
        get;
    }
    public PortableStorage(string root)
    {
        Root = Path.GetFullPath(root);
        foreach (var d in new[] { "Software/Universal", "Software/Clinics", "Syncro", "Logs", "Backups", "Support" })
            Directory.CreateDirectory(Path.Combine(Root, d));
    }
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidOperationException("A USB-relative path is required.");
        var p = Path.GetFullPath(Path.Combine(Root, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!p.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path leaves the USB kit.");
        for (var q = p; q != Root && q != null; q = Path.GetDirectoryName(q))
            if ((File.Exists(q) || Directory.Exists(q)) && (File.GetAttributes(q) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked files/directories are not permitted in the kit.");
        return p;
    }
    public string Import(string source, string category, string entrypoint)
    {
        var dest = Resolve(category + "/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            if (Directory.Exists(source))
            {
                var sourceRoot = Path.GetFullPath(source);
                if (dest.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Cannot import a folder containing this kit.");
                Copy(source, dest);
                var entry = Path.GetRelativePath(sourceRoot, Path.GetFullPath(entrypoint));
                if (entry.StartsWith("..") || Path.IsPathRooted(entry))
                    throw new InvalidOperationException("Choose an entrypoint inside the imported folder.");
                entrypoint = Path.Combine(dest, entry);
            }
            else
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Linked media is not supported.");
                File.Copy(source, Path.Combine(dest, Path.GetFileName(source)));
                entrypoint = Path.Combine(dest, Path.GetFileName(source));
            }
            if (!File.Exists(entrypoint))
                throw new InvalidOperationException("Entrypoint does not exist.");
            if (!new[] { ".exe", ".msi", ".ps1", ".cmd", ".bat" }.Contains(Path.GetExtension(entrypoint).ToLowerInvariant()))
                throw new InvalidOperationException("Unsupported entrypoint.");
            return Path.GetRelativePath(Root, entrypoint);
        }
        catch { Directory.Delete(dest, true); throw; }
    }
    static void Copy(string source, string dest)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Linked directories are not supported.");
        foreach (var f in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked files are not supported.");
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
        }
        foreach (var d in Directory.GetDirectories(source))
        {
            var child = Path.Combine(dest, Path.GetFileName(d));
            Directory.CreateDirectory(child);
            Copy(d, child);
        }
    }
    public List<string> Missing(IEnumerable<Package> packages, Clinic clinic)
    {
        var paths = packages.SelectMany(p => new[] { p.EntrypointRelativePath, p.PreInstallScript, p.PostInstallScript, p.DetectionScriptPath }.Where(x => !string.IsNullOrEmpty(x))).Append(clinic.SyncroRelativePath);
        return paths.Where(p => string.IsNullOrWhiteSpace(p) || !File.Exists(Resolve(p))).Select(p => string.IsNullOrEmpty(p) ? "Clinic Syncro installer is not configured" : p).ToList();
    }
}
/// <summary>Normalized relationships and typed scalar columns; one transaction per management edit.</summary>
public sealed class CatalogRepository : IDisposable
{
    readonly SqliteConnection db;
    readonly PortableStorage storage;
    static System.Reflection.PropertyInfo[] Columns<T>() => typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(int) || p.PropertyType == typeof(int?) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(DateTime) || p.PropertyType.IsEnum).ToArray();
    static string CreateTable<T>(string table, string constraints = "") => "CREATE TABLE " + table + "(" + string.Join(",", Columns<T>().Select(p => p.Name + " " + (p.PropertyType == typeof(int) || p.PropertyType == typeof(int?) || p.PropertyType == typeof(bool) ? "INTEGER" : "TEXT") + (p.Name == "Id" ? " PRIMARY KEY" : ""))) + constraints + ");";
    public CatalogRepository(PortableStorage storage)
    {
        this.storage = storage;
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(storage.Root, "CITDeploy.db"), ForeignKeys = true, Pooling = false }.ToString());
        db.Open();
        using var c = db.CreateCommand();
        c.CommandText = "PRAGMA journal_mode=DELETE;CREATE TABLE IF NOT EXISTS AppSettings(Key TEXT PRIMARY KEY,Value TEXT);";
        c.ExecuteNonQuery();
        c.CommandText = "SELECT Value FROM AppSettings WHERE Key='DatabaseSchemaVersion'";
        var version = c.ExecuteScalar()?.ToString();
        if (version != null && version != "1")
            throw new InvalidOperationException("Unsupported database version. Use a compatible CIT Deploy build.");
        if (version == null)
        {
            Backup();
            using var tx = db.BeginTransaction();
            c.Transaction = tx;
            c.CommandText = CreateTable<Clinic>("Clinics", ",UNIQUE(Name COLLATE NOCASE)") + CreateTable<Profile>("Profiles", ",FOREIGN KEY(ClinicId) REFERENCES Clinics(Id),UNIQUE(ClinicId,Name COLLATE NOCASE)") + CreateTable<Package>("Software", ",Scope TEXT NOT NULL,InstallerType TEXT NOT NULL,SuccessCodes TEXT NOT NULL,FOREIGN KEY(ClinicId) REFERENCES Clinics(Id)") + """
  CREATE TABLE ProfileSoftware(ProfileId INTEGER NOT NULL REFERENCES Profiles(Id),SoftwareId INTEGER NOT NULL REFERENCES Software(Id),InstallOrder INTEGER,PRIMARY KEY(ProfileId,SoftwareId));
  CREATE TABLE SoftwareDependencies(SoftwareId INTEGER NOT NULL REFERENCES Software(Id),DependencySoftwareId INTEGER NOT NULL REFERENCES Software(Id),PRIMARY KEY(SoftwareId,DependencySoftwareId));
  CREATE TABLE ClinicSoftwareSettings(ClinicId INTEGER NOT NULL REFERENCES Clinics(Id),SoftwareId INTEGER NOT NULL REFERENCES Software(Id),SiteKbUrl TEXT,PRIMARY KEY(ClinicId,SoftwareId));
  INSERT INTO AppSettings VALUES('DatabaseSchemaVersion','1');
  """;
            c.ExecuteNonQuery();
            tx.Commit();
        }
    }
    public void Backup()
    {
        using var target = new SqliteConnection("Data Source=" + Path.Combine(storage.Root, "Backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".db"));
        target.Open();
        db.BackupDatabase(target);
    }
    List<T> Read<T>(string table) where T : new()
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT * FROM " + table;
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            var row = new T();
            foreach (var p in Columns<T>())
            {
                var v = reader[p.Name];
                if (v is DBNull)
                {
                    p.SetValue(row, null);
                    continue;
                }
                p.SetValue(row, p.PropertyType.IsEnum ? Enum.Parse(p.PropertyType, (string)v) : p.PropertyType == typeof(DateTime) ? DateTime.Parse((string)v, null, System.Globalization.DateTimeStyles.RoundtripKind) : Convert.ChangeType(v, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
            }
            if (row is Package package)
                package.SuccessCodes = JsonSerializer.Deserialize<List<int>>(reader.GetString(reader.GetOrdinal("SuccessCodes")))!;
            result.Add(row);
        }
        return result;
    }
    public Catalog Load()
    {
        var result = new Catalog { Clinics = Read<Clinic>("Clinics"), Profiles = Read<Profile>("Profiles"), Packages = Read<Package>("Software") };
        void ReadRelation(string sql, Action<SqliteDataReader> consume)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                consume(reader);
        }
        ReadRelation("SELECT ProfileId,SoftwareId,InstallOrder FROM ProfileSoftware", r => result.Profiles.Single(p => p.Id == r.GetInt32(0)).Software.Add(r.GetInt32(1), r.GetInt32(2)));
        ReadRelation("SELECT SoftwareId,DependencySoftwareId FROM SoftwareDependencies", r => result.Packages.Single(p => p.Id == r.GetInt32(0)).Dependencies.Add(r.GetInt32(1)));
        ReadRelation("SELECT ClinicId,SoftwareId,SiteKbUrl FROM ClinicSoftwareSettings", r => result.SiteLinks.Add(new(r.GetInt32(0), r.GetInt32(1), r.GetString(2))));
        return result;
    }
    public void Save(Catalog data, string action)
    {
        Rules.Validate(data);
        Backup();
        using var tx = db.BeginTransaction();
        void Run(string sql, params object?[] args)
        {
            using var c = db.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            for (var i = 0; i < args.Length; i++)
                c.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
            c.ExecuteNonQuery();
        }
        void Insert<T>(string table, T row)
        {
            var properties = Columns<T>();
            var names = properties.Select(p => p.Name).ToList();
            var values = properties.Select(p => { var value = p.GetValue(row); return value is Enum ? value.ToString() : value is DateTime date ? date.ToString("O") : value; }).ToList();
            if (row is Package package)
            {
                names.AddRange(["Scope", "InstallerType", "SuccessCodes"]);
                values.AddRange([package.ClinicId == null ? "Universal" : "Clinic", Path.GetExtension(package.EntrypointRelativePath).TrimStart('.').ToUpperInvariant(), JsonSerializer.Serialize(package.SuccessCodes)]);
            }
            Run("INSERT INTO " + table + "(" + string.Join(",", names) + ") VALUES(" + string.Join(",", values.Select((_, i) => "$p" + i)) + ")", values.ToArray());
        }
        Run("DELETE FROM ProfileSoftware;DELETE FROM SoftwareDependencies;DELETE FROM ClinicSoftwareSettings;DELETE FROM Profiles;DELETE FROM Software;DELETE FROM Clinics;");
        foreach (var x in data.Clinics)
            Insert("Clinics", x);
        foreach (var x in data.Packages)
            Insert("Software", x);
        foreach (var x in data.Profiles)
        {
            Insert("Profiles", x);
            foreach (var pair in x.Software)
                Run("INSERT INTO ProfileSoftware VALUES($p0,$p1,$p2)", x.Id, pair.Key, pair.Value);
        }
        foreach (var x in data.Packages)
            foreach (var d in x.Dependencies)
                Run("INSERT INTO SoftwareDependencies VALUES($p0,$p1)", x.Id, d);
        foreach (var l in data.SiteLinks)
            Run("INSERT INTO ClinicSoftwareSettings VALUES($p0,$p1,$p2)", l.ClinicId, l.SoftwareId, l.Url);
        tx.Commit();
        File.AppendAllText(Path.Combine(storage.Root, "Logs", "management.log"), $"{DateTime.UtcNow:O} {action.Replace('\n', ' ')}\n");
    }
    public void Dispose() => db.Dispose();
}
