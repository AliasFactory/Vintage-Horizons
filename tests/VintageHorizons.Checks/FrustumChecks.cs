using Vintagestory.API.MathTools;

namespace VintageHorizons.Checks;

/// <summary>
/// View-frustum plane extraction and the p-vertex box test.
///
/// docs/STATUS.md has claimed since the performance pass that this has a standalone
/// harness. It did not - nothing matching it exists in the tree or anywhere in the history.
/// This is that harness, written for real.
///
/// Matrices are built with the game's own Mat4f rather than by hand, which is what keeps
/// the extraction's column-major assumption honest: an assumption tested against a matrix
/// laid out to match it will pass no matter which convention the game actually uses.
/// </summary>
public static class FrustumChecks
{
    public static void Run(Check c)
    {
        var frustum = new LodFrustum();
        frustum.Update(Projection(), View());

        Accepts(c, frustum);
        Rejects(c, frustum);
        NearAndFar(c, frustum);
        Conservative(c, frustum);
    }

    /// <summary>Looking down -Z, the OpenGL convention, from a camera at the origin.</summary>
    static float[] View() =>
        Mat4f.LookAt(Mat4f.Create(),
            eye: new[] { 0f, 0f, 0f },
            center: new[] { 0f, 0f, -1f },
            up: new[] { 0f, 1f, 0f });

    static float[] Projection() =>
        Mat4f.Perspective(Mat4f.Create(), fovy: 1.05f /* ~60 degrees */, aspect: 16f / 9f,
            near: 0.1f, far: 1000f);

    static void Accepts(Check c, LodFrustum f)
    {
        c.True(Box(f, 0, 0, -100, 10), "a box straight ahead is visible");
        c.True(Box(f, 0, 0, -10, 2), "a box close ahead is visible");
        c.True(Box(f, 0, 0, -900, 20), "a box near the far plane is visible");

        // Off-axis but inside the cone: at 100 blocks out a ~60 degree vertical fov and
        // 16:9 leaves plenty of room sideways.
        c.True(Box(f, 30, 0, -100, 5), "a box off to the right but inside the cone is visible");
        c.True(Box(f, -30, 0, -100, 5), "a box off to the left but inside the cone is visible");
        c.True(Box(f, 0, 20, -100, 5), "a box above centre but inside the cone is visible");
    }

    static void Rejects(Check c, LodFrustum f)
    {
        // Behind the camera is the case that matters most: without it, every section
        // behind the player is drawn, which is roughly half of them.
        c.False(Box(f, 0, 0, 100, 10), "a box behind the camera is rejected");
        c.False(Box(f, 0, 0, 500, 50), "a box far behind the camera is rejected");

        c.False(Box(f, 2000, 0, -100, 10), "a box far to the right is rejected");
        c.False(Box(f, -2000, 0, -100, 10), "a box far to the left is rejected");
        c.False(Box(f, 0, 2000, -100, 10), "a box far above is rejected");
        c.False(Box(f, 0, -2000, -100, 10), "a box far below is rejected");
    }

    static void NearAndFar(Check c, LodFrustum f)
    {
        // Beyond the far plane. This is the one that ties culling to our extended ZFar:
        // if the projection's far distance and the LOD render distance disagree, terrain
        // is either culled while still drawn or drawn while invisible.
        c.False(Box(f, 0, 0, -5000, 10), "a box beyond the far plane is rejected");
        c.True(Box(f, 0, 0, -5000, 4500), "a box straddling the far plane is kept");
    }

    /// <summary>
    /// The p-vertex test rejects only boxes fully outside a plane. Erring toward keeping
    /// a box is correct - a false accept costs one draw call, a false reject punches a
    /// hole in the terrain.
    /// </summary>
    static void Conservative(Check c, LodFrustum f)
    {
        c.True(Box(f, 0, 0, 0, 50), "a box containing the camera is kept");

        // A section-sized box straddling the left edge must be kept, not rejected.
        c.True(Box(f, -100, 0, -100, 64), "a box straddling the frustum edge is kept");

        // Sweeping across the boundary, acceptance must be contiguous: no visible box may
        // sit between two rejected ones, which is what a sign error in one plane produces.
        bool seenVisible = false, seenGapAfter = false;
        for (int x = -400; x <= 400; x += 10)
        {
            bool visible = Box(f, x, 0, -200, 8);
            if (visible && seenGapAfter) c.True(false, $"visibility is not contiguous across x (gap before x={x})");
            if (seenVisible && !visible) seenGapAfter = true;
            if (visible) seenVisible = true;
        }
        c.True(seenVisible, "some boxes along the sweep are visible");
        c.True(seenGapAfter, "the sweep leaves the frustum at its edge");
    }

    /// <summary>An axis-aligned cube of the given half-extent, centred on the point.</summary>
    static bool Box(LodFrustum f, double x, double y, double z, double half) =>
        f.BoxInView(x - half, y - half, z - half, x + half, y + half, z + half);
}
