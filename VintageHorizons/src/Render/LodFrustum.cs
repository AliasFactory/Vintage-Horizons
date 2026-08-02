namespace VintageHorizons;

/// <summary>
/// The six planes of the view frustum, taken from a view-projection matrix with the
/// Gribb-Hartmann method. The mod uses them to skip a draw call for a section that is outside
/// the view of the camera.
///
/// The planes come from the SAME matrices that the LOD shader gets. Thus the test can never
/// disagree with what the mod draws.
///
/// The planes do not come from the culler of the game. That would connect this mod to the
/// vanilla view distance, and to the order in which the game updates it. Neither one matches
/// the extended ZFar of this mod.
///
/// Each coordinate is relative to the camera, with the camera at the origin. This matches the
/// LOD model matrices.
/// </summary>
public class LodFrustum
{
    // plane[i] = (a, b, c, d). The normal (a,b,c) points INTO the frustum.
    readonly float[,] planes = new float[6, 4];
    readonly float[] viewProj = new float[16];

    public void Update(float[] projection, float[] view)
    {
        Vintagestory.API.MathTools.Mat4f.Multiply(viewProj, projection, view);
        float[] m = viewProj;

        // The layout is column-major, as OpenGL uses: m[col * 4 + row].
        // Row i of the matrix, as the code below uses it: r0 = (m0, m4, m8, m12), and so
        // on.
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
    /// True when any part of the box can be visible. The box is relative to the camera.
    ///
    /// This uses the p-vertex test. The mod tests only the corner of the box that is
    /// furthest along the normal of each plane. Thus the mod rejects a box only when the box
    /// is fully behind one plane.
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
