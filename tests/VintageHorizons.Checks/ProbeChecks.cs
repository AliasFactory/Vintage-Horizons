using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

/// <summary>
/// Runs first, and exists only to fail loudly and specifically when the game assemblies
/// cannot be loaded.
///
/// Every other suite here is either pure BCL or touches game types so lightly that a
/// loading failure would surface as a confusing TypeInitializationException halfway
/// through an unrelated assertion. These two calls exercise the only two real loading
/// risks in the whole fast tier, so when they pass the rest is mechanically safe:
///
///   - Block: around two hundred virtual methods, and building its vtable resolves every
///     one of their signatures. That reaches further than any other type the checks touch.
///   - LodStore: overrides CreateTablesIfNotExists(SqliteConnection), so merely loading
///     the type forces Microsoft.Data.Sqlite to resolve, even though no database is opened.
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
