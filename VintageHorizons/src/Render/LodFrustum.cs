namespace VintageHorizons;

/// <summary>
/// Six view-frustum planes extracted from a view-projection matrix (Gribb-Hartmann),
/// used to skip draw calls for sections outside the camera's view.
///
/// The planes come from the SAME matrices handed to the LOD shader, so the test can
/// never disagree with what is actually rendered — deriving them from the game's own
/// culler would tie us to the vanilla view distance and to its per-frame update order,
/// neither of which matches our extended ZFar.
///
/// All coordinates are camera-relative (camera at the origin), matching the LOD model
/// matrices.
/// </summary>
public class LodFrustum
{
    // plane[i] = (a, b, c, d), normal (a,b,c) pointing INTO the frustum.
    readonly float[,] planes = new float[6, 4];
    readonly float[] viewProj = new float[16];

    public void Update(float[] projection, float[] view)
    {
        Vintagestory.API.MathTools.Mat4f.Multiply(viewProj, projection, view);
        float[] m = viewProj;

        // Column-major (OpenGL): m[col * 4 + row].
        // Row i of the matrix as used below: r0 = (m0, m4, m8, m12) etc.
        SetPlane(0, m[3] + m[0], m[7] + m[4], m[11] + m[8], m[15] + m[12]);   // left
        SetPlane(1, m[3] - m[0], m[7] - m[4], m[11] - m[8], m[15] - m[12]);   // right
        SetPlane(2, m[3] + m[1], m[7] + m[5], m[11] + m[9], m[15] + m[13]);   // bottom
        SetPlane(3, m[3] - m[1], m[7] - m[5], m[11] - m[9], m[15] - m[13]);   // top
        SetPlane(4, m[3] + m[2], m[7] + m[6], m[11] + m[10], m[15] + m[14]);  // near
        SetPlane(5, m[3] - m[2], m[7] - m[6], m[11] - m[10], m[15] - m[14]);  // far
    }

    void SetPlane(int i, float a, float b, float c, float d)
    {
        float len = MathF.Sqrt(a * a + b * b + c * c);
        if (len <= 0) len = 1;
        planes[i, 0] = a / len;
        planes[i, 1] = b / len;
        planes[i, 2] = c / len;
        planes[i, 3] = d / len;
    }

    /// <summary>
    /// True if any part of the camera-relative box may be visible. Uses the p-vertex
    /// test: only the box corner furthest along each plane normal has to be checked,
    /// so a box is rejected only when it is fully behind some plane.
    /// </summary>
    public bool BoxInView(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        for (int i = 0; i < 6; i++)
        {
            double a = planes[i, 0], b = planes[i, 1], c = planes[i, 2], d = planes[i, 3];
            double px = a >= 0 ? maxX : minX;
            double py = b >= 0 ? maxY : minY;
            double pz = c >= 0 ? maxZ : minZ;

            if (a * px + b * py + c * pz + d < 0) return false;
        }
        return true;
    }
}
