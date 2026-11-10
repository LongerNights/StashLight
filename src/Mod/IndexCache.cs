using FrooxEngine;
using StashLight.Core;
using ResoniteModLoader;

namespace StashLight;

/// <summary>
/// Reads/writes the index cache next to the game's other local data:
/// &lt;LocalDB.PermanentPath&gt;\StashLight\&lt;userId&gt;.index. It is only a cache: any problem means "rebuild".
/// </summary>
internal static class IndexCache
{
    private static readonly object _io = new();

    private static string Dir
    {
        get
        {
            string root = Engine.Current.LocalDB.PermanentPath, dir = Path.Combine(root, "StashLight");
            try   // the mod used to be called InventorySearch: keep its (expensive to rebuild) cache
            {
                string old = Path.Combine(root, "InventorySearch");
                if (!Directory.Exists(dir) && Directory.Exists(old)) Directory.Move(old, dir);
            }
            catch { /* only a cache: a failed move just means a rebuild */ }
            return dir;
        }
    }

    private static string FileFor(string userId)
    {
        var safe = new string(userId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return Path.Combine(Dir, safe + ".index");
    }

    public static IndexCacheFile? TryLoad(string userId)
    {
        try
        {
            var path = FileFor(userId);
            if (!File.Exists(path)) return null;
            lock (_io)
            {
                using var fs = File.OpenRead(path);
                var file = IndexCacheFile.TryRead(fs, userId);
                if (file == null)
                {
                    fs.Close();
                    try { File.Delete(path); } catch { /* it is only a cache */ }
                    ResoniteMod.Warn("[StashLight] ignored an unreadable or outdated index cache; it will be rebuilt");
                }
                return file;
            }
        }
        catch (Exception ex)
        {
            ResoniteMod.Warn("[StashLight] could not read the index cache: " + ex.Message);
            return null;
        }
    }

    /// <summary>Writes atomically (temp file, then replace) so a crash mid-write can't leave a half-written cache.</summary>
    public static long Save(IndexCacheFile file)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = FileFor(file.UserId);
            var tmp = path + ".tmp";
            lock (_io)
            {
                using (var fs = File.Create(tmp)) file.Write(fs);
                File.Move(tmp, path, overwrite: true);
            }
            return new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            ResoniteMod.Warn("[StashLight] could not save the index cache: " + ex.Message);
            return -1;
        }
    }
}
