using System;
using System.Numerics;
using GameCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace CombatSimulator.Camera;

/// <summary>
/// The REAL view state of the game camera, read from the matrices the renderer actually
/// uses — the same ViewMatrix and ProjectionMatrix that WorldToScreen multiplies through.
///
/// This exists because reconstructing the view from the orbit angles (DirH/DirV/FoV)
/// requires knowing the game's conventions for them, and guessing those is how the first
/// two versions of the dynamic camera went wrong (a flipped pitch put the death camera
/// underground; the debug overlay's predicted projections landed nowhere near the game's
/// own). Reading the matrices removes every guess: the lens comes straight off the
/// projection matrix (tanHalf = 1/M11, 1/M22 for a standard perspective matrix), and the
/// forward axis off the view matrix, with the row/column and sign ambiguity settled at
/// runtime by checking against the camera→look-at direction.
///
/// The one game convention that still matters — which way DirV runs — is handled by the
/// controller's write-feedback loop, not by anything here.
/// </summary>
public readonly struct GameCameraView
{
    public readonly Vector3 Position;
    public readonly Vector3 Forward;
    public readonly float TanHalfH;
    public readonly float TanHalfV;
    /// <summary>The game camera's FoV FIELD (what we write), whatever it means optically.
    /// Its relation to the real lens is measured, not assumed: TanHalfV / tan(GameFov/2).</summary>
    public readonly float GameFov;
    public readonly float DirH;
    public readonly float DirV;

    private GameCameraView(Vector3 position, Vector3 forward, float tanHalfH, float tanHalfV,
        float gameFov, float dirH, float dirV)
    {
        Position = position;
        Forward = forward;
        TanHalfH = tanHalfH;
        TanHalfV = tanHalfV;
        GameFov = gameFov;
        DirH = dirH;
        DirV = dirV;
    }

    public static unsafe bool TryRead(out GameCameraView view)
    {
        view = default;
        try
        {
            var camMgr = GameCameraManager.Instance();
            if (camMgr == null || camMgr->Camera == null)
                return false;

            var gameCam = camMgr->Camera;
            var sceneCam = &gameCam->CameraBase.SceneCamera;
            var rc = sceneCam->RenderCamera;
            if (rc == null)
                return false;

            var origin = new Vector3(rc->Origin.X, rc->Origin.Y, rc->Origin.Z);
            var lookAt = sceneCam->LookAtVector;
            var lookDir = new Vector3(lookAt.X, lookAt.Y, lookAt.Z) - origin;
            if (lookDir.LengthSquared() < 1e-6f)
                return false;
            lookDir = Vector3.Normalize(lookDir);

            // The view matrix's axes are its rows or its columns depending on the library's
            // vector convention, and the z axis may point either way. Rather than trust any
            // of that, try all four candidates and keep the one that agrees with where the
            // camera is actually looking.
            var m = sceneCam->ViewMatrix;
            var rowZ = new Vector3(m.M31, m.M32, m.M33);
            var colZ = new Vector3(m.M13, m.M23, m.M33);
            var fwd = PickForward(lookDir, rowZ, colZ);
            if (fwd.LengthSquared() < 1e-6f)
                return false;

            // Real lens, straight off the projection matrix WorldToScreen uses:
            // for a perspective matrix, M11 = 1/tan(fovH/2) and M22 = 1/tan(fovV/2).
            var proj = rc->ProjectionMatrix;
            if (MathF.Abs(proj.M11) < 1e-6f || MathF.Abs(proj.M22) < 1e-6f)
                return false;

            view = new GameCameraView(
                origin,
                Vector3.Normalize(fwd),
                1f / proj.M11,
                1f / proj.M22,
                gameCam->FoV,
                gameCam->DirH,
                gameCam->DirV);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Vector3 PickForward(Vector3 lookDir, Vector3 rowZ, Vector3 colZ)
    {
        var best = Vector3.Zero;
        var bestDot = 0.5f; // demand at least rough agreement with the look direction
        foreach (var candidate in stackalloc[] { rowZ, -rowZ, colZ, -colZ })
        {
            var len = candidate.Length();
            if (len < 1e-4f)
                continue;
            var dot = Vector3.Dot(candidate / len, lookDir);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = candidate;
            }
        }
        // Matrices torn mid-update (or a convention we did not anticipate): fall back to
        // the look direction itself, which is always approximately right.
        return best == Vector3.Zero ? lookDir : best;
    }
}
