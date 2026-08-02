using Vintagestory.API.MathTools;

namespace VintageHorizons.Checks;

/// <summary>
/// The extraction of the view-frustum planes, and the p-vertex box test.
///
/// From the performance work, docs/STATUS.md said that this code had its own harness. It did
/// not. Nothing that matches that claim is in the tree, or anywhere in the history. This file
/// is that harness.
///
/// The matrices come from Mat4f of the game, and not from code here. That keeps the
/// column-major assumption of the extraction honest. A test against a matrix that this file
/// arranged to match the assumption passes, whatever convention the game uses.
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

    /// <summary>The view goes along -Z, which is the OpenGL convention, from a camera at the
    /// origin.</summary>
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

        // This point is off the axis, but inside the cone. At 100 blocks, a vertical field
        // of view of approximately 60 degrees, at 16:9, leaves much space to the sides.
        c.True(Box(f, 30, 0, -100, 5), "a box off to the right but inside the cone is visible");
        c.True(Box(f, -30, 0, -100, 5), "a box off to the left but inside the cone is visible");
        c.True(Box(f, 0, 20, -100, 5), "a box above centre but inside the cone is visible");
    }

    static void Rejects(Check c, LodFrustum f)
    {
        // The case behind the camera is the most important one. Without it, the mod draws
        // each section behind the player, which is approximately half of them.
        c.False(Box(f, 0, 0, 100, 10), "a box behind the camera is rejected");
        c.False(Box(f, 0, 0, 500, 50), "a box far behind the camera is rejected");

        c.False(Box(f, 2000, 0, -100, 10), "a box far to the right is rejected");
        c.False(Box(f, -2000, 0, -100, 10), "a box far to the left is rejected");
        c.False(Box(f, 0, 2000, -100, 10), "a box far above is rejected");
        c.False(Box(f, 0, -2000, -100, 10), "a box far below is rejected");
    }

    static void NearAndFar(Check c, LodFrustum f)
    {
        // This point is past the far plane. This case connects the culling to the extended
        // ZFar of this mod. If the far distance of the projection and the LOD render distance
        // disagree, the mod culls terrain that it still draws, or it draws terrain that
        // nobody can see.
        c.False(Box(f, 0, 0, -5000, 10), "a box beyond the far plane is rejected");
        c.True(Box(f, 0, 0, -5000, 4500), "a box straddling the far plane is kept");
    }

    /// <summary>
    /// The p-vertex test rejects a box only when that box is fully outside a plane. An error
    /// toward keeping a box is the correct direction. A wrong accept costs one draw call. A
    /// wrong reject makes a hole in the terrain.
    /// </summary>
    static void Conservative(Check c, LodFrustum f)
    {
        c.True(Box(f, 0, 0, 0, 50), "a box containing the camera is kept");

        // A box the size of a section, across the left edge, must stay. The mod must not
        // reject it.
        c.True(Box(f, -100, 0, -100, 64), "a box straddling the frustum edge is kept");

        // Across the boundary, the accepted boxes must be next to each other. No visible box
        // can be between two rejected boxes. A sign error in one plane gives that result.
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

    /// <summary>A cube that lines up with the axes, with the given half extent, and with its
    /// center at the point.</summary>
    static bool Box(LodFrustum f, double x, double y, double z, double half) =>
        f.BoxInView(x - half, y - half, z - half, x + half, y + half, z + half);
}
