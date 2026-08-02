using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace VintageHorizons.Checks;

/// <summary>
/// Gives the runtime the location of the Vintage Story assemblies.
///
/// The build uses DLLs inside the game installation. Anego Studios does not permit
/// redistribution of them, thus the references use Private=false and the DLLs never go into
/// the release zip.
///
/// Copy-local in the csproj of this project covers the four direct references. But
/// VintagestoryAPI.dll names eight more in its own reference table: cairo-sharp, SkiaSharp,
/// Newtonsoft.Json, two OpenTK assemblies, protobuf-net, System.Drawing.Primitives and
/// Microsoft.Data.Sqlite.
///
/// Which of those load depends on the game types for which a check makes the CLR build a
/// vtable. Block alone has approximately two hundred virtual methods, and the resolution of
/// their signatures reaches almost anywhere.
///
/// A probe of the installation is one rule, and not a list of guesses for each DLL. It also
/// continues to operate when the game adds a dependency.
/// </summary>
public static class GameAssemblies
{
    /// <summary>
    /// This runs before the runtime loads any check type. That is important. A resolver
    /// that Main installs can be too late already, if the JIT prepared a method that names a
    /// game type.
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

        // The probe for native code is separate from the probe for managed code. Thus the
        // managed resolver above does nothing for libe_sqlite3.so or libSkiaSharp.so.
        //
        // Nothing in the fast tier reaches native code. But when something does, the failure
        // is a DllNotFoundException with no detail. Connect this now, and do not debug that
        // later.
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

    /// <summary>This uses the same order as the csproj and scripts/test-lib.sh.</summary>
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
    /// The root of the repository. The code walks up from the test binary and looks for a
    /// marker file.
    ///
    /// A check that reads a committed file needs this. Those files are the shaders,
    /// modinfo.json and the csproj. A fixed relative depth breaks when the output path
    /// changes.
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
