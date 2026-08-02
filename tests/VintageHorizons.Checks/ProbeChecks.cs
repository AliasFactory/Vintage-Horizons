using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

/// <summary>
/// This suite runs first. It exists to fail clearly and specifically when the runtime cannot
/// load the game assemblies.
///
/// Each other suite here uses the BCL only, or it touches game types very lightly. For those
/// suites, a loading failure appears as a confusing TypeInitializationException, in the
/// middle of an unrelated assertion.
///
/// These two calls exercise the only two real loading risks in the full fast tier. Thus when
/// they pass, the remainder is safe.
///
///   - Block has approximately two hundred virtual methods. The build of its vtable resolves
///     the signature of each one. That reaches further than any other type that the checks
///     touch.
///   - LodStore overrides CreateTablesIfNotExists(SqliteConnection). Thus a load of the type
///     alone makes Microsoft.Data.Sqlite resolve, and no database opens.
/// </summary>
public static class ProbeChecks
{
    public static void Run(Check c)
    {
        c.True(Directory.Exists(GameAssemblies.GamePath),
            "game install is present at " + GameAssemblies.GamePath);

        c.NoThrow(() =>
        {
            var block = new Block
            {
                BlockMaterial = EnumBlockMaterial.Stone,
                Code = new AssetLocation("game", "rock-granite"),
            };
            LodBlockPolicy.FlagsFor(block);
        }, "Block type loads and is constructible");

        c.NoThrow(() =>
        {
            byte[] blob = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));
            _ = new LodStore(null!).DeserializeForeign(blob, null);
        }, "LodStore loads without opening a database");
    }
}
