using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace VintageHorizons.Checks;

/// <summary>
/// Teaches the runtime where the Vintage Story assemblies live.
///
/// The mod is built against DLLs inside the game install, which are not redistributable
/// and are referenced with Private=false so they never travel in the release zip. Copy-local
/// in this project's csproj handles the four direct references, but VintagestoryAPI.dll
/// names eight more in its own reference table (cairo-sharp, SkiaSharp, Newtonsoft.Json,
/// OpenTK x2, protobuf-net, System.Drawing.Primitives, Microsoft.Data.Sqlite). Which of
/// those actually load depends on which game types a check forces the CLR to build a vtable
/// for - Block alone has around two hundred virtual methods, and resolving their signatures
/// can reach almost anywhere.
///
/// Probing the install directly is one rule instead of a per-DLL guess list, and it keeps
/// working when the game adds a dependency.
/// </summary>
public static class GameAssemblies
{
    /// <summary>
    /// Runs before any check type is loaded, which matters: a resolver installed inside
    /// Main can already be too late if the JIT has prepared a method that names a game type.
    /// </summary>
    [ModuleInitializer]
    internal static void Install()
    {
        string game = GamePath;
        string lib = Path.Combine(game, "Lib");

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name == null) return null;

            foreach (string dir in new[] { game, lib })
            {
                string candidate = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };

        // Native probing is separate from managed probing, so the managed resolver above
        // does nothing for libe_sqlite3.so / libSkiaSharp.so. Nothing in the fast tier
        // should reach native code, but when something does the failure is an opaque
        // DllNotFoundException, so wire it up rather than debug that later.
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (assembly, name) =>
        {
            foreach (string dir in new[] { lib, game })
            {
                foreach (string candidate in new[] { name, "lib" + name + ".so", name + ".so" })
                {
                    string path = Path.Combine(dir, candidate);
                    if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
                }
            }
            return IntPtr.Zero;
        };
    }

    /// <summary>Same resolution order as the csproj and scripts/test-lib.sh.</summary>
    public static string GamePath
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Games", "vintagestory1.22.5");
        }
    }

    /// <summary>
    /// The repo root, found by walking up from the test binary looking for a sentinel.
    /// Checks that read committed files (shaders, modinfo.json, csproj) need this, and
    /// hardcoding a relative depth breaks the moment the output path changes.
    /// </summary>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "Could not find the repo root (no .git above " + AppContext.BaseDirectory + ")");
        }
    }
}
